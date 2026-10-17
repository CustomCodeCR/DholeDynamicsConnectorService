using Dhole.Dynamics.Contracts;
using Npgsql;
namespace Dhole.Dynamics.Api;

/// <summary>Deletion is disabled until an admin explicitly opts in per organization/entity.</summary>
public sealed class DeletePolicyRepository(NpgsqlDataSource source)
{
    public async Task<DeletePolicyState> GetAsync(Guid connection, string entity, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT enabled,min_age_days FROM dynamics.delete_policies
            WHERE connection_id=@connection AND entity_name=@entity
            """, db);
        cmd.Parameters.AddWithValue("connection", connection);
        cmd.Parameters.AddWithValue("entity", entity);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new(entity, r.GetBoolean(0), r.GetInt32(1))
            : new(entity, false, 0);
    }

    public async Task<bool> SetAsync(Guid connection, string entity, DeletePolicyRequest request,
        string actor, CancellationToken ct)
    {
        if (request.MinAgeDays is < 0 or > 36500)
            throw new ReadRejected(400, "invalid_retention_days");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.delete_policies(connection_id,entity_name,enabled,min_age_days)
            SELECT @connection,@entity,@enabled,@days
            FROM dynamics.metadata_allowed_entities
            WHERE connection_id=@connection AND logical_name=@entity AND enabled
            ON CONFLICT(connection_id,entity_name)
            DO UPDATE SET enabled=EXCLUDED.enabled,min_age_days=EXCLUDED.min_age_days,updated_at=now()
            RETURNING entity_name
            """, db, tx);
        cmd.Parameters.AddWithValue("connection", connection);
        cmd.Parameters.AddWithValue("entity", entity);
        cmd.Parameters.AddWithValue("enabled", request.Enabled);
        cmd.Parameters.AddWithValue("days", request.MinAgeDays);
        if (await cmd.ExecuteScalarAsync(ct) is null) return false;
        await using var audit = new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
            VALUES (@id,@connection,@actor,@action)
            """, db, tx);
        audit.Parameters.AddWithValue("id", Guid.NewGuid());
        audit.Parameters.AddWithValue("connection", connection);
        audit.Parameters.AddWithValue("actor", actor);
        audit.Parameters.AddWithValue("action", request.Enabled ? "delete.policy.enable" : "delete.policy.disable");
        await audit.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
