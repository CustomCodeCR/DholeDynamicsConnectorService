using Npgsql;

namespace Dhole.Dynamics.Api;

/// <summary>Database-enforced registration, connection policies and atomic per-grant quotas.</summary>
public sealed class ClientAccessRepository(NpgsqlDataSource source)
{
    public async Task<IReadOnlyList<ClientRecord>> ListClientsAsync(CancellationToken ct)
    {
        var clients = new List<ClientRecord>();
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT id, issuer, oauth_client_id, display_name, enabled
            FROM dynamics.api_clients ORDER BY display_name LIMIT 500
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            clients.Add(new ClientRecord(reader.GetGuid(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetBoolean(4)));
        return clients;
    }

    public async Task<IReadOnlyList<GrantRecord>> ListGrantsAsync(CancellationToken ct)
    {
        var grants = new List<GrantRecord>();
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT id, api_client_id, connection_id, entity_set, operation,
                   allowed_fields, max_requests_per_minute, enabled
            FROM dynamics.api_grants ORDER BY created_at DESC LIMIT 1000
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            grants.Add(new GrantRecord(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetString(3), reader.GetString(4), reader.GetFieldValue<string[]>(5),
                reader.GetInt32(6), reader.GetBoolean(7)));
        return grants;
    }

    public async Task<ClientRecord> RegisterAsync(ClientRegistrationRequest request, string actor, CancellationToken ct)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.api_clients(id,issuer,oauth_client_id,display_name,enabled)
            VALUES (@id,@issuer,@oauth,@name,@enabled)
            ON CONFLICT(issuer,oauth_client_id) DO UPDATE SET
                display_name=EXCLUDED.display_name, enabled=EXCLUDED.enabled, updated_at=now()
            RETURNING id
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("issuer", request.Issuer);
        cmd.Parameters.AddWithValue("oauth", request.ClientId);
        cmd.Parameters.AddWithValue("name", request.DisplayName);
        cmd.Parameters.AddWithValue("enabled", request.Enabled);
        var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        await AuditAsync(conn, tx, actor, "m2m.client.upsert", null, ct);
        await tx.CommitAsync(ct);
        return new(id, request.Issuer, request.ClientId, request.DisplayName, request.Enabled);
    }

    public async Task<GrantRecord?> UpsertGrantAsync(GrantRequest request, string actor, CancellationToken ct)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.api_grants
                (id,api_client_id,connection_id,entity_set,operation,allowed_fields,max_requests_per_minute,enabled)
            SELECT @id,@client,c.id,@entity,@operation,@fields,@quota,@enabled
            FROM dynamics.connections c WHERE c.code=@code
              AND EXISTS (SELECT 1 FROM dynamics.api_clients a WHERE a.id=@client)
            ON CONFLICT(api_client_id,connection_id,entity_set,operation) DO UPDATE SET
                allowed_fields=EXCLUDED.allowed_fields,
                max_requests_per_minute=EXCLUDED.max_requests_per_minute,
                enabled=EXCLUDED.enabled
            RETURNING id, connection_id
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("client", request.ClientId);
        cmd.Parameters.AddWithValue("code", request.ConnectionCode.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("entity", request.EntitySet);
        cmd.Parameters.AddWithValue("operation", request.Operation);
        cmd.Parameters.AddWithValue("fields", request.AllowedFields);
        cmd.Parameters.AddWithValue("quota", request.MaxRequestsPerMinute);
        cmd.Parameters.AddWithValue("enabled", request.Enabled);
        Guid grantId, connId;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            grantId = reader.GetGuid(0);
            connId = reader.GetGuid(1);
        }
        await AuditAsync(conn, tx, actor, "m2m.grant.upsert", connId, ct);
        await tx.CommitAsync(ct);
        return new(grantId, request.ClientId, connId, request.EntitySet, request.Operation,
            request.AllowedFields, request.MaxRequestsPerMinute, request.Enabled);
    }

    public async Task<GrantDecision> AuthorizeAsync(string issuer, string oauthClientId, string connectionCode,
        string entitySet, string operation, IReadOnlyCollection<string> fields, CancellationToken ct)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var select = new NpgsqlCommand("""
            SELECT g.id, g.allowed_fields, g.max_requests_per_minute
            FROM dynamics.api_grants g
            JOIN dynamics.api_clients a ON a.id=g.api_client_id
            JOIN dynamics.connections c ON c.id=g.connection_id
            WHERE a.enabled AND g.enabled AND a.issuer=@issuer
              AND a.oauth_client_id=@oauth AND c.code=@code
              AND g.entity_set=@entity AND g.operation=@operation
            FOR UPDATE OF g
            """, conn, tx);
        select.Parameters.AddWithValue("issuer", issuer);
        select.Parameters.AddWithValue("oauth", oauthClientId);
        select.Parameters.AddWithValue("code", connectionCode.Trim().ToLowerInvariant());
        select.Parameters.AddWithValue("entity", entitySet);
        select.Parameters.AddWithValue("operation", operation);
        Guid grantId;
        int quota;
        string[] allowed;
        await using (var r = await select.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return GrantDecision.Missing;
            grantId = r.GetGuid(0);
            allowed = r.GetFieldValue<string[]>(1);
            quota = r.GetInt32(2);
        }
        if (!fields.All(f => allowed.Contains(f, StringComparer.Ordinal)))
            return GrantDecision.Missing;

        // Durable shared quota across all API instances (not merely an in-memory rate limiter).
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        await using var acquire = new NpgsqlCommand("""
            INSERT INTO dynamics.api_quota_windows(grant_id,window_minute,request_count)
            VALUES (@grant,@minute,1)
            ON CONFLICT(grant_id,window_minute)
            DO UPDATE SET request_count = dynamics.api_quota_windows.request_count + 1
            WHERE dynamics.api_quota_windows.request_count < @quota
            RETURNING request_count
            """, conn, tx);
        acquire.Parameters.AddWithValue("grant", grantId);
        acquire.Parameters.AddWithValue("minute", minute);
        acquire.Parameters.AddWithValue("quota", quota);
        var acquired = await acquire.ExecuteScalarAsync(ct);
        if (acquired is null) return GrantDecision.QuotaExceeded;
        await tx.CommitAsync(ct);
        return GrantDecision.Allowed;
    }

    private static async Task AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        string actor, string action, Guid? connectionId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,actor_id,action,connection_id)
            VALUES (@id,@actor,@action,@connection)
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("connection", (object?)connectionId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
