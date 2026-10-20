using System.Security.Cryptography;
using System.Text;
using Dhole.Dynamics.Contracts;
using Npgsql;

namespace Dhole.Dynamics.Api;

public sealed record SavedQueryApiKeyRequest(string QueryName, string Label,
    int ExpiresInDays = 30, int MaxRequestsPerMinute = 60);
public sealed record SavedQueryApiKeyInfo(Guid Id, string QueryName, string Label, bool Enabled,
    DateTimeOffset ExpiresAt, int MaxRequestsPerMinute, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt);
public sealed record SavedQueryApiKeyIssued(SavedQueryApiKeyInfo Key, string ApiKey);
public sealed record SavedQueryApiKeyIdentity(Guid Id, Guid ConnectionId, string QueryName);

/// <summary>
/// Dedicated read-only integration credentials. Secrets are revealed once, hashed at rest,
/// scoped to ONE connection and ONE enabled saved query, and revocable by Dhole admins.
/// This is deliberately separate from the Entra machine identity used for CRUD.
/// </summary>
public sealed class SavedQueryApiKeyRepository(NpgsqlDataSource source)
{
    public static string GenerateSecret(Guid id)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+','-').Replace('/','_');
        return "dki1_" + id.ToString("N") + "_" + secret;
    }

    public static bool TryParseId(string? value, out Guid id)
    {
        id = Guid.Empty;
        if (value is not { Length: 81 } || !value.StartsWith("dki1_",StringComparison.Ordinal))
            return false;
        if (value[37] != '_' || !Guid.TryParseExact(value.AsSpan(5,32), "N", out id))
            return false;
        var secret = value.AsSpan(38);
        return secret.Length == 43 && secret.ToString().All(c =>
            c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');
    }

    public async Task<IReadOnlyList<SavedQueryApiKeyInfo>> ListAsync(string connectionCode,
        CancellationToken ct)
    {
        var result = new List<SavedQueryApiKeyInfo>();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT k.id,k.saved_query_name,k.label,k.enabled,k.expires_at,
                   k.max_requests_per_minute,k.created_at,k.last_used_at
            FROM dynamics.saved_query_api_keys k
            JOIN dynamics.connections c ON c.id=k.connection_id
            WHERE c.code=@code ORDER BY k.created_at DESC LIMIT 200
            """,db);
        cmd.Parameters.AddWithValue("code",connectionCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
            result.Add(Read(reader));
        return result;
    }

    public async Task<SavedQueryApiKeyIssued> IssueAsync(string code,
        SavedQueryApiKeyRequest request,string actor,CancellationToken ct)
    {
        if (!ReadQueryCompiler.ValidParam(request.QueryName) ||
            string.IsNullOrWhiteSpace(request.Label) || request.Label.Length > 120 ||
            request.ExpiresInDays is < 1 or > 90 ||
            request.MaxRequestsPerMinute is < 1 or > 120)
            throw new ReadRejected(400,"invalid_integration_key_request");
        var id=Guid.NewGuid();
        var secret=GenerateSecret(id);
        var digest=SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var expiresAt=DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays);
        await using var db=await source.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var insert=new NpgsqlCommand("""
            INSERT INTO dynamics.saved_query_api_keys
                (id,connection_id,saved_query_name,key_sha256,label,enabled,
                 expires_at,max_requests_per_minute,created_by,saved_query_updated_at)
            SELECT @id,c.id,q.name,@hash,@label,true,@expires,@quota,@actor,q.updated_at
            FROM dynamics.connections c
            JOIN dynamics.saved_queries q ON q.connection_id=c.id
            WHERE c.code=@code AND q.name=@name AND q.enabled=true
            RETURNING id,saved_query_name,label,enabled,expires_at,
                      max_requests_per_minute,created_at,last_used_at
            """,db,tx);
        insert.Parameters.AddWithValue("id",id);
        insert.Parameters.AddWithValue("code",code);
        insert.Parameters.AddWithValue("name",request.QueryName);
        insert.Parameters.AddWithValue("label",request.Label.Trim());
        insert.Parameters.AddWithValue("hash",digest);
        insert.Parameters.AddWithValue("expires",expiresAt);
        insert.Parameters.AddWithValue("quota",request.MaxRequestsPerMinute);
        insert.Parameters.AddWithValue("actor",actor.Length<=128?actor:actor[..128]);
        SavedQueryApiKeyInfo key;
        await using(var reader=await insert.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct))
                throw new ReadRejected(409,"enabled_saved_query_required");
            key=Read(reader);
        }
        await AuditAsync(db,tx,code,actor,"integration-key.create",ct);
        await tx.CommitAsync(ct);
        return new(key,secret);
    }

    public async Task<bool> RevokeAsync(string code,Guid keyId,string actor,CancellationToken ct)
    {
        await using var db=await source.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            UPDATE dynamics.saved_query_api_keys SET enabled=false
            WHERE id=@key AND connection_id=(SELECT id FROM dynamics.connections WHERE code=@code)
            RETURNING id
            """,db,tx);
        cmd.Parameters.AddWithValue("key",keyId);
        cmd.Parameters.AddWithValue("code",code);
        if(await cmd.ExecuteScalarAsync(ct) is null)return false;
        await AuditAsync(db,tx,code,actor,"integration-key.revoke",ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<SavedQueryApiKeyIdentity> ValidateAndChargeAsync(
        string? secret,string connectionCode,string queryName,CancellationToken ct)
    {
        if(!TryParseId(secret,out var keyId) || string.IsNullOrWhiteSpace(connectionCode)
            || !ReadQueryCompiler.ValidParam(queryName))
            throw new ReadRejected(401,"integration_key_invalid");
        var submitted=SHA256.HashData(Encoding.UTF8.GetBytes(secret!));
        await using var db=await source.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var select=new NpgsqlCommand("""
            SELECT k.key_sha256,k.connection_id,k.max_requests_per_minute
            FROM dynamics.saved_query_api_keys k
            JOIN dynamics.connections c ON c.id=k.connection_id
            JOIN dynamics.saved_queries q ON q.connection_id=k.connection_id
                AND q.name=k.saved_query_name AND q.updated_at=k.saved_query_updated_at
            WHERE k.id=@key AND c.code=@code AND k.saved_query_name=@name
              AND k.enabled=true AND k.expires_at>now() AND q.enabled=true
            FOR UPDATE OF k
            """,db,tx);
        select.Parameters.AddWithValue("key",keyId);
        select.Parameters.AddWithValue("code",connectionCode);
        select.Parameters.AddWithValue("name",queryName);
        Guid connectionId;int quota;byte[] expected;
        await using(var reader=await select.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct))
                throw new ReadRejected(401,"integration_key_invalid");
            expected=reader.GetFieldValue<byte[]>(0);
            connectionId=reader.GetGuid(1);
            quota=reader.GetInt32(2);
        }
        if(expected.Length!=submitted.Length ||
            !CryptographicOperations.FixedTimeEquals(submitted,expected))
            throw new ReadRejected(401,"integration_key_invalid");
        var window=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60;
        await using var charge=new NpgsqlCommand("""
            INSERT INTO dynamics.saved_query_api_key_quota (key_id,window_minute,request_count)
            VALUES(@id,@minute,1)
            ON CONFLICT(key_id,window_minute) DO UPDATE
            SET request_count=dynamics.saved_query_api_key_quota.request_count+1
            WHERE dynamics.saved_query_api_key_quota.request_count<@quota
            RETURNING request_count
            """,db,tx);
        charge.Parameters.AddWithValue("id",keyId);
        charge.Parameters.AddWithValue("minute",window);
        charge.Parameters.AddWithValue("quota",quota);
        if(await charge.ExecuteScalarAsync(ct) is null)
            throw new ReadRejected(429,"integration_key_quota");
        await using var used=new NpgsqlCommand("""
            UPDATE dynamics.saved_query_api_keys SET last_used_at=now() WHERE id=@id
            """,db,tx);
        used.Parameters.AddWithValue("id",keyId);
        await used.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return new(keyId,connectionId,queryName);
    }

    private static SavedQueryApiKeyInfo Read(NpgsqlDataReader r) =>
        new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetBoolean(3),
            r.GetFieldValue<DateTimeOffset>(4),r.GetInt32(5),
            r.GetFieldValue<DateTimeOffset>(6),
            r.IsDBNull(7)?null:r.GetFieldValue<DateTimeOffset>(7));

    private static async Task AuditAsync(NpgsqlConnection db,NpgsqlTransaction tx,string code,
        string actor,string action,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
            SELECT @id,id,@actor,@action FROM dynamics.connections WHERE code=@code
            """,db,tx);
        cmd.Parameters.AddWithValue("id",Guid.NewGuid());
        cmd.Parameters.AddWithValue("code",code);
        cmd.Parameters.AddWithValue("actor",actor);
        cmd.Parameters.AddWithValue("action",action);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
