using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Npgsql;

namespace Dhole.Dynamics.Api;

public sealed record WriteReservation(Guid Id, bool Created, string Status, WriteResult? Previous);

/// <summary>PostgreSQL unique key prevents duplicate sends by this connector, including racing API instances.</summary>
public sealed class WriteLedger(NpgsqlDataSource data)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static bool ValidKey(string? value) =>
        value is { Length: >= 16 and <= 128 } &&
        value.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');

    public async Task<WriteReservation> ReserveAsync(Guid connection, string issuer, string client, string key,
        string hash, string entity, string operation, int? batchCount, CancellationToken ct)
    {
        if (!ValidKey(key) || hash.Length != 64 || string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(client))
            throw new ReadRejected(400, "invalid_idempotency_key");
        await using var db = await data.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO dynamics.write_operations
              (id,connection_id,issuer,oauth_client_id,idempotency_key,request_sha256,
                entity_name,operation,status,batch_count)
            VALUES (@id,@connection,@issuer,@client,@key,@hash,@entity,@operation,'reserved',@count)
            ON CONFLICT(connection_id,issuer,oauth_client_id,idempotency_key) DO NOTHING
            RETURNING id
            """, db, tx);
        var newId = Guid.NewGuid();
        insert.Parameters.AddWithValue("id", newId);
        insert.Parameters.AddWithValue("connection", connection);
        insert.Parameters.AddWithValue("issuer", issuer);
        insert.Parameters.AddWithValue("client", client);
        insert.Parameters.AddWithValue("key", key);
        insert.Parameters.AddWithValue("hash", hash);
        insert.Parameters.AddWithValue("entity", entity);
        insert.Parameters.AddWithValue("operation", operation);
        insert.Parameters.AddWithValue("count", (object?)batchCount ?? DBNull.Value);
        var written = await insert.ExecuteScalarAsync(ct);
        if (written is Guid id)
        {
            await AuditAsync(db, tx, connection, client, "write.reserved", ct);
            await tx.CommitAsync(ct);
            return new(id, true, "reserved", null);
        }
        await using var existing = new NpgsqlCommand("""
            SELECT id,request_sha256,status,response::text FROM dynamics.write_operations
            WHERE connection_id=@connection AND issuer=@issuer AND oauth_client_id=@client
              AND idempotency_key=@key
            """, db, tx);
        existing.Parameters.AddWithValue("connection", connection);
        existing.Parameters.AddWithValue("issuer", issuer);
        existing.Parameters.AddWithValue("client", client);
        existing.Parameters.AddWithValue("key", key);
        await using var reader = await existing.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new ReadRejected(409, "idempotency_race");
        if (!string.Equals(reader.GetString(1), hash, StringComparison.Ordinal))
            throw new ReadRejected(409, "idempotency_payload_mismatch");
        var result = new WriteReservation(reader.GetGuid(0), false, reader.GetString(2),
            reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<WriteResult>(reader.GetString(3), Options));
        await reader.DisposeAsync();
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<WriteResult> CompleteAsync(Guid id, Guid connection, string client,
        string status, int? httpStatus, Guid? recordId, string? etag, string? reference,
        CancellationToken ct, int? retryAfterSeconds = null)
    {
        if (status is not ("succeeded" or "rejected" or "ambiguous"))
            throw new ArgumentException("Invalid operation status.");
        var result = new WriteResult(id, status, httpStatus, recordId, etag, reference, RetryAfterSeconds: retryAfterSeconds);
        await using var db = await data.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE dynamics.write_operations SET status=@status,http_status=@http,
              record_id=@record, response=CAST(@json AS jsonb),
              finished_at=now(),updated_at=now()
            WHERE id=@id AND connection_id=@connection AND status='reserved'
            RETURNING id
            """, db, tx);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("http", (object?)httpStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("record", (object?)recordId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("json", JsonSerializer.Serialize(result, Options));
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("connection", connection);
        if (await cmd.ExecuteScalarAsync(ct) is null)
            throw new ReadRejected(409, "write_operation_state_changed");
        await AuditAsync(db, tx, connection, client, "write." + status, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<WriteOperationDetail?> FindAsync(Guid connection, Guid id, CancellationToken ct,
        string? issuer = null, string? client = null)
    {
        await using var db = await data.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT id,entity_name,operation,status,http_status,record_id,started_at,finished_at,
                   response::text
            FROM dynamics.write_operations WHERE connection_id=@connection AND id=@id
              AND (@issuer IS NULL OR issuer=@issuer)
              AND (@client IS NULL OR oauth_client_id=@client)
            """, db);
        cmd.Parameters.AddWithValue("connection", connection);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("issuer", (object?)issuer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("client", (object?)client ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string? reference = null;
        if (!r.IsDBNull(8))
            reference = JsonSerializer.Deserialize<WriteResult>(r.GetString(8), Options)?.Reference;
        return new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.IsDBNull(4) ? null : r.GetInt32(4),
            r.IsDBNull(5) ? null : r.GetGuid(5),
            r.GetFieldValue<DateTimeOffset>(6),
            r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), reference);
    }

    public async Task<bool> ReconcileAsync(Guid conn, Guid id, string actor,
        WriteReconcileRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500
            || (!request.ConfirmedNotApplied && !Guid.TryParse(request.RecordId, out _))
            || request.ConfirmedNotApplied && request.RecordId is not null)
            throw new ReadRejected(400, "invalid_reconciliation");
        Guid? recordId = request.RecordId is null ? null : Guid.Parse(request.RecordId);
        await using var db = await data.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE dynamics.write_operations SET status='reconciled',record_id=@record,
                finished_at=now(),updated_at=now()
            WHERE id=@id AND connection_id=@conn AND status IN ('reserved','ambiguous')
            RETURNING id
            """, db, tx);
        cmd.Parameters.AddWithValue("record", (object?)recordId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("conn", conn);
        var found = await cmd.ExecuteScalarAsync(ct);
        if (found is null) return false;
        // Never automatically retry even after manual no-effect confirmation.
        await AuditAsync(db, tx, conn, actor, "write.reconciled.manual", ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task AuditAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid conn, string actor, string action, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
            VALUES(@id,@conn,@actor,@action)
            """, db, tx);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("conn", conn);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("action", action);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
