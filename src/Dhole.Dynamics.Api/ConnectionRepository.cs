using Npgsql;

namespace Dhole.Dynamics.Api;

public sealed class ConnectionRepository(NpgsqlDataSource database, Encryption encryption)
{
    public async Task<DynamicsConnection?> FindAsync(string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT id, code, dataverse_url, tenant_id, client_id, default_currency_id,
                   secret_nonce, secret_ciphertext, secret_tag, secret_key_version, updated_at
            FROM dynamics.connections WHERE code = @code
            """, connection);
        command.Parameters.AddWithValue("code", code.Trim().ToLowerInvariant());
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return Read(r);
    }

    public async Task<IReadOnlyList<ConnectionSummary>> ListAsync(CancellationToken ct)
    {
        var result = new List<ConnectionSummary>();
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT id, code, dataverse_url, tenant_id, client_id, default_currency_id,
                   secret_nonce, secret_ciphertext, secret_tag, secret_key_version, updated_at
            FROM dynamics.connections ORDER BY code
            """, connection);
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(Read(r).ToSummary());
        return result;
    }

    public async Task<ConnectionSummary> UpsertAsync(
        ConnectionUpsertRequest request, string actor, CancellationToken ct)
    {
        var previous = await FindAsync(request.Code, ct);
        var id = previous?.Id ?? Guid.NewGuid();
        var encrypted = encryption.Encrypt(id, request.ClientSecret);

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO dynamics.connections
                (id, code, dataverse_url, tenant_id, client_id, default_currency_id,
                 secret_nonce, secret_ciphertext, secret_tag, secret_key_version)
            VALUES
                (@id, @code, @url, @tenant, @client, @currency, @nonce, @cipher, @tag, @version)
            ON CONFLICT (code) DO UPDATE SET
                dataverse_url = EXCLUDED.dataverse_url,
                tenant_id = EXCLUDED.tenant_id,
                client_id = EXCLUDED.client_id,
                default_currency_id = EXCLUDED.default_currency_id,
                secret_nonce = EXCLUDED.secret_nonce,
                secret_ciphertext = EXCLUDED.secret_ciphertext,
                secret_tag = EXCLUDED.secret_tag,
                secret_key_version = EXCLUDED.secret_key_version,
                updated_at = now()
            WHERE dynamics.connections.id = EXCLUDED.id
            RETURNING id
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("code", request.Code);
        command.Parameters.AddWithValue("url", request.DataverseUrl);
        command.Parameters.AddWithValue("tenant", request.TenantId);
        command.Parameters.AddWithValue("client", request.ClientId);
        command.Parameters.AddWithValue("currency", (object?)request.DefaultCurrencyId ?? DBNull.Value);
        command.Parameters.AddWithValue("nonce", encrypted.Nonce);
        command.Parameters.AddWithValue("cipher", encrypted.Ciphertext);
        command.Parameters.AddWithValue("tag", encrypted.Tag);
        command.Parameters.AddWithValue("version", Encryption.CurrentVersion);
        var persistedId = await command.ExecuteScalarAsync(ct);
        if (persistedId is null)
            throw new InvalidOperationException("Concurrent configuration update detected; retry.");

        await WriteAuditAsync(connection, id, actor, "connection.upsert", ct);
        return (await FindAsync(request.Code, ct))!.ToSummary();
    }

    public async Task<bool> RotateSecretAsync(
        string code, string secret, string actor, CancellationToken ct)
    {
        var previous = await FindAsync(code, ct);
        if (previous is null) return false;
        var encrypted = encryption.Encrypt(previous.Id, secret);
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE dynamics.connections SET
                secret_nonce = @nonce,
                secret_ciphertext = @cipher,
                secret_tag = @tag,
                secret_key_version = @version,
                updated_at = now()
            WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("id", previous.Id);
        command.Parameters.AddWithValue("nonce", encrypted.Nonce);
        command.Parameters.AddWithValue("cipher", encrypted.Ciphertext);
        command.Parameters.AddWithValue("tag", encrypted.Tag);
        command.Parameters.AddWithValue("version", Encryption.CurrentVersion);
        var changed = await command.ExecuteNonQueryAsync(ct);
        if (changed > 0) await WriteAuditAsync(connection, previous.Id, actor, "connection.secret.rotate", ct);
        return changed > 0;
    }

    private static async Task WriteAuditAsync(
        NpgsqlConnection connection, Guid id, string actor, string action, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events (id, connection_id, actor_id, action)
            VALUES (@id, @connection, @actor, @action)
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("connection", id);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("action", action);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static DynamicsConnection Read(NpgsqlDataReader r) =>
        new(
            r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4),
            r.IsDBNull(5) ? null : r.GetGuid(5),
            (byte[])r.GetValue(6), (byte[])r.GetValue(7), (byte[])r.GetValue(8),
            r.GetInt32(9), r.GetFieldValue<DateTimeOffset>(10));
}
