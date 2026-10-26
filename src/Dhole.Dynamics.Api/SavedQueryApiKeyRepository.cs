using System.Security.Cryptography;
using System.Text;
using Dhole.Dynamics.Contracts;
using Npgsql;

namespace Dhole.Dynamics.Api;

public sealed record SavedQueryApiKeyRequest(string? QueryName, string Label,
    int ExpiresInDays = 30, int MaxRequestsPerMinute = 60,
    string? Scope = null, IReadOnlyList<string>? AllowedQueries = null);
public sealed record SavedQueryApiKeyAccessUpdate(string Scope,
    IReadOnlyList<string>? AllowedQueries = null);
public sealed record SavedQueryApiKeyInfo(Guid Id, string? QueryName, string Label, bool Enabled,
    DateTimeOffset ExpiresAt, int MaxRequestsPerMinute, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt, string Scope, IReadOnlyList<string>? AllowedQueries = null);
public sealed record SavedQueryApiKeyIssued(SavedQueryApiKeyInfo Key, string ApiKey);
public sealed record SavedQueryApiKeyIdentity(Guid Id, Guid ConnectionId, string? QueryName, string Scope);

/// <summary>
/// Dedicated read-only integration credentials. Secrets are revealed once, hashed at rest,
/// scoped to ONE connection and its enabled saved queries (or a legacy pinned query),
/// and revocable by Dhole admins.
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
                   k.max_requests_per_minute,k.created_at,k.last_used_at,k.key_scope,
                   COALESCE((SELECT array_agg(p.query_name ORDER BY p.query_name)
                      FROM dynamics.saved_query_key_queries p WHERE p.key_id=k.id),
                      ARRAY[]::varchar[])
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
        var scope = request.Scope ?? (string.IsNullOrWhiteSpace(request.QueryName) ? "all" : "single");
        if (scope is not ("all" or "single" or "selected") ||
            (scope == "single" && (request.QueryName is null ||
                !ReadQueryCompiler.ValidParam(request.QueryName))) ||
            (scope != "single" && !string.IsNullOrEmpty(request.QueryName)) ||
            (scope != "selected" && request.AllowedQueries is { Count: > 0 }) ||
            string.IsNullOrWhiteSpace(request.Label) || request.Label.Length > 120 ||
            request.ExpiresInDays is < 1 or > 90 ||
            request.MaxRequestsPerMinute is < 1 or > 120)
            throw new ReadRejected(400,"invalid_integration_key_request");
        var allowed = ValidateSelection(scope, request.AllowedQueries);
        var id=Guid.NewGuid();
        var secret=GenerateSecret(id);
        var digest=SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var expiresAt=DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays);
        await using var db=await source.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        if (scope == "selected")
            await RequireEnabledQueriesAsync(db,tx,code,allowed,ct);
        await using var insert=new NpgsqlCommand("""
            INSERT INTO dynamics.saved_query_api_keys
                (id,connection_id,saved_query_name,key_sha256,label,enabled,
                 expires_at,max_requests_per_minute,created_by,saved_query_updated_at,key_scope)
            SELECT @id,c.id,q.name,@hash,@label,true,@expires,@quota,@actor,q.updated_at,@scope
            FROM dynamics.connections c
            LEFT JOIN dynamics.saved_queries q ON q.connection_id=c.id
                AND q.name=@name AND q.enabled=true
            WHERE c.code=@code AND (@scope<>'single' OR q.id IS NOT NULL)
            RETURNING id,saved_query_name,label,enabled,expires_at,
                      max_requests_per_minute,created_at,last_used_at,key_scope
            """,db,tx);
        insert.Parameters.AddWithValue("id",id);
        insert.Parameters.AddWithValue("code",code);
        insert.Parameters.Add("name",NpgsqlTypes.NpgsqlDbType.Varchar).Value =
            scope == "single" ? request.QueryName! : DBNull.Value;
        insert.Parameters.AddWithValue("scope",scope);
        insert.Parameters.AddWithValue("label",request.Label.Trim());
        insert.Parameters.AddWithValue("hash",digest);
        insert.Parameters.AddWithValue("expires",expiresAt);
        insert.Parameters.AddWithValue("quota",request.MaxRequestsPerMinute);
        insert.Parameters.AddWithValue("actor",actor.Length<=128?actor:actor[..128]);
        SavedQueryApiKeyInfo key;
        await using(var reader=await insert.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct))
                throw new ReadRejected(409,scope == "single" ? "enabled_saved_query_required" : "connection_not_found");
            key=Read(reader) with { AllowedQueries=allowed };
        }
        if(scope=="selected")
            await SaveSelectedAsync(db,tx,id,allowed,ct);
        await AuditAsync(db,tx,code,actor,"integration-key.create",ct);
        await tx.CommitAsync(ct);
        return new(key,secret);
    }


    /// <summary>Change permitted named searches without replacing or revealing the secret.
    /// Revoked keys cannot be reactivated here; the connection scope never changes.</summary>
    public async Task<SavedQueryApiKeyInfo?> UpdateAccessAsync(string code,Guid keyId,
        SavedQueryApiKeyAccessUpdate request,string actor,CancellationToken ct)
    {
        if(request.Scope is not ("all" or "selected"))
            throw new ReadRejected(400,"invalid_integration_key_scope");
        var allowed=ValidateSelection(request.Scope,request.AllowedQueries);
        await using var db=await source.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        if(request.Scope=="selected")
            await RequireEnabledQueriesAsync(db,tx,code,allowed,ct);
        await using var update=new NpgsqlCommand("""
            UPDATE dynamics.saved_query_api_keys k
            SET key_scope=@scope,saved_query_name=NULL,saved_query_updated_at=NULL
            WHERE k.id=@key AND k.enabled=true AND k.expires_at>now()
              AND k.connection_id=(SELECT c.id FROM dynamics.connections c WHERE c.code=@code)
            RETURNING k.id,k.saved_query_name,k.label,k.enabled,k.expires_at,
                      k.max_requests_per_minute,k.created_at,k.last_used_at,k.key_scope
            """,db,tx);
        update.Parameters.AddWithValue("key",keyId);
        update.Parameters.AddWithValue("code",code);
        update.Parameters.AddWithValue("scope",request.Scope);
        SavedQueryApiKeyInfo? updated;
        await using(var read=await update.ExecuteReaderAsync(ct))
            updated=await read.ReadAsync(ct)?Read(read):null;
        if(updated is null)return null;
        await using(var clear=new NpgsqlCommand(
            "DELETE FROM dynamics.saved_query_key_queries WHERE key_id=@key",db,tx))
        {
            clear.Parameters.AddWithValue("key",keyId);
            await clear.ExecuteNonQueryAsync(ct);
        }
        if(request.Scope=="selected")
            await SaveSelectedAsync(db,tx,keyId,allowed,ct);
        await AuditAsync(db,tx,code,actor,"integration-key.scope.update",ct);
        await tx.CommitAsync(ct);
        return updated with { AllowedQueries=allowed };
    }

    private static string[] ValidateSelection(string scope,IReadOnlyList<string>? selected)
    {
        var names=(selected ?? []).ToArray();
        if((scope=="selected" && (names.Length is < 1 or > 200 ||
            names.Any(n=>!ReadQueryCompiler.ValidParam(n)) ||
            names.Distinct(StringComparer.Ordinal).Count()!=names.Length)) ||
           (scope!="selected" && names.Length>0))
            throw new ReadRejected(400,"invalid_integration_key_selection");
        return names;
    }

    private static async Task RequireEnabledQueriesAsync(NpgsqlConnection db,NpgsqlTransaction tx,
        string code,string[] names,CancellationToken ct)
    {
        await using var validate=new NpgsqlCommand("""
            SELECT count(DISTINCT q.name)
            FROM dynamics.saved_queries q
            JOIN dynamics.connections c ON c.id=q.connection_id
            WHERE c.code=@code AND q.enabled=true AND q.name=ANY(@names)
            """,db,tx);
        validate.Parameters.AddWithValue("code",code);
        validate.Parameters.AddWithValue("names",NpgsqlTypes.NpgsqlDbType.Array |
            NpgsqlTypes.NpgsqlDbType.Varchar).Value=names;
        if((long)(await validate.ExecuteScalarAsync(ct))! != names.Length)
            throw new ReadRejected(409,"enabled_saved_queries_required");
    }

    private static async Task SaveSelectedAsync(NpgsqlConnection db,NpgsqlTransaction tx,
        Guid id,string[] names,CancellationToken ct)
    {
        await using var insert=new NpgsqlCommand("""
            INSERT INTO dynamics.saved_query_key_queries(key_id,query_name)
            SELECT @key,unnest(@names::varchar[])
            """,db,tx);
        insert.Parameters.AddWithValue("key",id);
        insert.Parameters.Add("names",NpgsqlTypes.NpgsqlDbType.Array |
            NpgsqlTypes.NpgsqlDbType.Varchar).Value=names;
        await insert.ExecuteNonQueryAsync(ct);
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
            SELECT k.key_sha256,k.connection_id,k.max_requests_per_minute,
                   k.key_scope,k.saved_query_name
            FROM dynamics.saved_query_api_keys k
            JOIN dynamics.connections c ON c.id=k.connection_id
            JOIN dynamics.saved_queries q ON q.connection_id=k.connection_id
                AND q.name=@name AND q.enabled=true
            WHERE k.id=@key AND c.code=@code
              AND k.enabled=true AND k.expires_at>now()
              AND (k.key_scope='all' OR
                   (k.key_scope='single' AND k.saved_query_name=q.name
                    AND k.saved_query_updated_at=q.updated_at)
                   OR (k.key_scope='selected' AND EXISTS(
                       SELECT 1 FROM dynamics.saved_query_key_queries p
                       WHERE p.key_id=k.id AND p.query_name=q.name)))
            FOR UPDATE OF k
            """,db,tx);
        select.Parameters.AddWithValue("key",keyId);
        select.Parameters.AddWithValue("code",connectionCode);
        select.Parameters.AddWithValue("name",queryName);
        Guid connectionId;int quota;byte[] expected;string scope;string? pinnedQuery;
        await using(var reader=await select.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct))
                throw new ReadRejected(401,"integration_key_invalid");
            expected=reader.GetFieldValue<byte[]>(0);
            connectionId=reader.GetGuid(1);
            quota=reader.GetInt32(2);
            scope=reader.GetString(3);
            pinnedQuery=reader.IsDBNull(4)?null:reader.GetString(4);
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
        return new(keyId,connectionId,pinnedQuery,scope);
    }

    private static SavedQueryApiKeyInfo Read(NpgsqlDataReader r) =>
        new(r.GetGuid(0),r.IsDBNull(1)?null:r.GetString(1),r.GetString(2),r.GetBoolean(3),
            r.GetFieldValue<DateTimeOffset>(4),r.GetInt32(5),
            r.GetFieldValue<DateTimeOffset>(6),
            r.IsDBNull(7)?null:r.GetFieldValue<DateTimeOffset>(7),r.GetString(8),
            r.FieldCount>9 ? r.GetFieldValue<string[]>(9) : []);

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
