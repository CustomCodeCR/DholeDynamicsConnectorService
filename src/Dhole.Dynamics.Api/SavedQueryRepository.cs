using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Npgsql;

namespace Dhole.Dynamics.Api;

/// <summary>Scoped, audited, opt-in named search templates; no SQL or raw OData is accepted.</summary>
public sealed class SavedQueryRepository(NpgsqlDataSource database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SavedQuerySummary>> ListAsync(Guid connectionId, CancellationToken ct)
    {
        var items = new List<SavedQuerySummary>();
        await using var db = await database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT id,name,entity_name,spec::text,parameter_schema::text,enabled,updated_at
            FROM dynamics.saved_queries WHERE connection_id=@connection ORDER BY name LIMIT 200
            """, db);
        cmd.Parameters.AddWithValue("connection", connectionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(Read(reader));
        return items;
    }

    public async Task<SavedQuerySummary?> FindAsync(Guid connectionId, string name, CancellationToken ct)
    {
        await using var db = await database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT id,name,entity_name,spec::text,parameter_schema::text,enabled,updated_at
            FROM dynamics.saved_queries WHERE connection_id=@connection AND name=@name
            """, db);
        cmd.Parameters.AddWithValue("connection", connectionId);
        cmd.Parameters.AddWithValue("name", name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<SavedQuerySummary> SaveAsync(Guid connectionId, SavedQueryWrite spec,
        string actor, CancellationToken ct)
    {
        var fields = JsonSerializer.Serialize(spec.Query, Json);
        var parameters = JsonSerializer.Serialize(spec.Parameters, Json);
        await using var db = await database.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.saved_queries
                (id,connection_id,name,entity_name,spec,parameter_schema,enabled)
            VALUES(@id,@connection,@name,@entity,CAST(@spec AS jsonb),CAST(@schema AS jsonb),@enabled)
            ON CONFLICT(connection_id,name) DO UPDATE SET
                entity_name=EXCLUDED.entity_name,spec=EXCLUDED.spec,
                parameter_schema=EXCLUDED.parameter_schema,enabled=false,updated_at=now()
            RETURNING id,updated_at
            """, db, tx);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("connection", connectionId);
        cmd.Parameters.AddWithValue("name", spec.Name);
        cmd.Parameters.AddWithValue("entity", spec.EntityName);
        cmd.Parameters.AddWithValue("spec", fields);
        cmd.Parameters.AddWithValue("schema", parameters);
        // Fail closed: even an admin must separately enable/re-enable a saved template.
        cmd.Parameters.AddWithValue("enabled", false);
        Guid id;
        DateTimeOffset changed;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            id = r.GetGuid(0);
            changed = r.GetFieldValue<DateTimeOffset>(1);
        }
        await AuditAsync(db, tx, connectionId, actor, "saved-query.upsert", ct);
        await tx.CommitAsync(ct);
        return new(id, spec.Name, spec.EntityName, spec.Query, spec.Parameters, false, changed);
    }

    public async Task<bool> SetEnabledAsync(Guid connectionId, string name, bool enabled,
        string actor, CancellationToken ct)
    {
        await using var db = await database.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE dynamics.saved_queries SET enabled=@enabled,updated_at=now()
            WHERE connection_id=@connection AND name=@name
            RETURNING id
            """, db, tx);
        cmd.Parameters.AddWithValue("connection", connectionId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("enabled", enabled);
        if (await cmd.ExecuteScalarAsync(ct) is null) return false;
        await AuditAsync(db, tx, connectionId, actor,
            enabled ? "saved-query.enable" : "saved-query.disable", ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static SavedQuerySummary Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2),
        JsonSerializer.Deserialize<ReadSpec>(r.GetString(3), Json)!,
        JsonSerializer.Deserialize<IReadOnlyList<ReadParamDefinition>>(r.GetString(4), Json)!,
        r.GetBoolean(5), r.GetFieldValue<DateTimeOffset>(6));

    private static async Task AuditAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid id, string actor, string action, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
            VALUES(@id,@connection,@actor,@action)
            """, db, tx);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("connection", id);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("action", action);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
