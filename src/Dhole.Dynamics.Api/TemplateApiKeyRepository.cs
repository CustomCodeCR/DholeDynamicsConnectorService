using System.Security.Cryptography;
using System.Text;
using Dhole.Dynamics.Contracts;
using Npgsql;

namespace Dhole.Dynamics.Api;

public sealed record CrudIntegrationKeyRequest(string TemplateName,string Label,
    int ExpiresInDays=30,int MaxRequestsPerMinute=30,
    IReadOnlyList<string>? KeyFields=null);
public sealed record CrudIntegrationKeyInfo(Guid Id,string TemplateName,string Operation,
    string EntityName,string Label,bool Enabled,DateTimeOffset ExpiresAt,
    int MaxRequestsPerMinute,DateTimeOffset CreatedAt,DateTimeOffset? LastUsedAt);
public sealed record CrudIntegrationKeyIssued(CrudIntegrationKeyInfo Key,string ApiKey);
public sealed record CrudIntegrationKeyIdentity(Guid Id,Guid ConnectionId,string ClientId,
    string TemplateName,string Operation,string EntityName,IReadOnlyList<string> KeyFields);

/// <summary>
/// One-key/one-version/one-operation CRUD API access. Each key gets its own
/// dedicated DB-backed api_client + narrowly scoped per-field grant. The
/// existing WriteAccessService and WriteLedger remain the source of authority.
/// </summary>
public sealed class TemplateApiKeyRepository(NpgsqlDataSource database,
    TemplateRepository templates,WriteAccessService writeAccess)
{
    internal const string Issuer="https://dhole.internal/template-keys";

    public static string GenerateSecret(Guid id) =>
        "dkw1_"+id.ToString("N")+"_"+Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');

    public static bool TryParseSecret(string? token,out Guid id)
    {
        id=Guid.Empty;
        return token is {Length:81} && token.StartsWith("dkw1_",StringComparison.Ordinal) &&
            token[37]=='_' && Guid.TryParseExact(token.AsSpan(5,32),"N",out id) &&
            token.AsSpan(38).ToString().All(c=>
                c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
                    or >= '0' and <= '9' or '-' or '_');
    }

    public async Task<IReadOnlyList<CrudIntegrationKeyInfo>> ListAsync(string code,CancellationToken ct)
    {
        var items=new List<CrudIntegrationKeyInfo>();
        await using var db=await database.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT k.id,t.name,k.operation,k.entity_name,k.label,k.enabled,k.expires_at,
                   k.max_requests_per_minute,k.created_at,k.last_used_at
            FROM dynamics.template_api_keys k
            JOIN dynamics.connections c ON c.id=k.connection_id
            JOIN dynamics.integration_templates t ON t.id=k.template_id
            WHERE c.code=@code ORDER BY k.created_at DESC LIMIT 200
            """,db);
        cmd.Parameters.AddWithValue("code",code);
        await using var rows=await cmd.ExecuteReaderAsync(ct);
        while(await rows.ReadAsync(ct))items.Add(Read(rows));
        return items;
    }

    public async Task<CrudIntegrationKeyIssued> IssueAsync(string code,
        CrudIntegrationKeyRequest request,string actor,CancellationToken ct)
    {
        if(request.TemplateName is null || request.Label is null ||
           request.Label.Trim().Length is < 2 or > 120 ||
           request.ExpiresInDays is < 1 or > 90 ||
           request.MaxRequestsPerMinute is < 1 or > 120)
            throw new ReadRejected(400,"invalid_crud_key_request");
        var policyTemplate=await ResolveActiveAsync(code,request.TemplateName,ct);
        var (summary,revision,policy)=policyTemplate;
        var definition=revision.Definition;
        if(definition.Operation is not ("create" or "update" or "upsert" or "delete"))
            throw new ReadRejected(400,"crud_template_required");

        // A token never grants arbitrary relationships, record fields or dynamic
        // field names. These must be explicitly designed as approved mappings.
        if(definition.Mappings.Any(m=>m.Target.Contains('@',StringComparison.Ordinal) ||
              m.Target.Contains('.',StringComparison.Ordinal)))
            throw new ReadRejected(403,"crud_lookup_mapping_requires_oauth");
        var fields=definition.Mappings.Select(m=>m.Target)
            .Distinct(StringComparer.Ordinal).ToList();
        var keys=(request.KeyFields??[]).Distinct(StringComparer.Ordinal).ToArray();
        if(definition.Operation=="upsert")
        {
            if(keys.Length==0 || !(policy.Table.AlternateKeys??[]).Any(k=>
                k.Attributes.Count==keys.Length && k.Attributes.All(keys.Contains)))
                throw new ReadRejected(400,"approved_alternate_key_required");
            fields.AddRange(keys.Where(x=>!fields.Contains(x,StringComparer.Ordinal)));
        }
        else if(keys.Length!=0)throw new ReadRejected(400,"unexpected_alternate_key");
        if(definition.Operation=="delete")fields.Add(policy.Table.PrimaryIdAttribute);
        if(fields.Count is < 1 or > 60)throw new ReadRejected(400,"invalid_crud_key_fields");
        var needsCreate=definition.Operation is "create" or "upsert";
        var needsUpdate=definition.Operation is "update" or "upsert";
        if(definition.Operation!="delete")
        foreach(var name in fields)
        {
            var col=policy.Table.Columns.FirstOrDefault(c=>c.LogicalName==name);
            var allowed=policy.Permissions.Fields.FirstOrDefault(p=>
                p.EntityName==definition.EntityName && p.FieldName==name);
            if(col is null || allowed is null || !allowed.Enabled || col.IsCalculated ||
               allowed.MetadataFingerprint!=MetadataRepository.Fingerprint(col) ||
               needsCreate && (!col.CanCreate || !allowed.CanCreate) ||
               needsUpdate && (!col.CanUpdate || !allowed.CanUpdate))
                throw new ReadRejected(403,"crud_field_denied");
        }

        var keyId=Guid.NewGuid();
        var clientId=Guid.NewGuid();
        var secret=GenerateSecret(keyId);
        var digest=SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var expires=DateTimeOffset.UtcNow.AddDays(request.ExpiresInDays);
        await using var db=await database.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using(var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.api_clients(id,issuer,oauth_client_id,display_name,enabled)
            VALUES(@id,@issuer,@client,@label,true)
            """,db,tx))
        {
            cmd.Parameters.AddWithValue("id",clientId);
            cmd.Parameters.AddWithValue("issuer",Issuer);
            cmd.Parameters.AddWithValue("client",keyId.ToString("D"));
            cmd.Parameters.AddWithValue("label",request.Label.Trim());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using(var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.api_grants
                (id,api_client_id,connection_id,entity_set,operation,allowed_fields,
                 max_requests_per_minute,enabled)
            VALUES(@id,@client,@conn,@entity,@op,@fields,@quota,true)
            """,db,tx))
        {
            cmd.Parameters.AddWithValue("id",Guid.NewGuid());
            cmd.Parameters.AddWithValue("client",clientId);
            cmd.Parameters.AddWithValue("conn",policy.Connection.Id);
            cmd.Parameters.AddWithValue("entity",definition.EntityName);
            cmd.Parameters.AddWithValue("op",definition.Operation);
            cmd.Parameters.AddWithValue("fields",fields.ToArray());
            cmd.Parameters.AddWithValue("quota",request.MaxRequestsPerMinute);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        CrudIntegrationKeyInfo issued;
        await using(var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.template_api_keys
                (id,connection_id,template_id,template_version,operation,entity_name,
                 allowed_fields,key_fields,client_id,key_sha256,label,expires_at,
                 max_requests_per_minute,created_by)
            SELECT @id,@conn,t.id,@version,@op,@entity,@fields,@keys,@client,@hash,
                   @label,@expires,@quota,@actor
            FROM dynamics.integration_templates t
            WHERE t.id=@template AND t.connection_id=@conn
              AND t.active_version=@version
            RETURNING id,@name,operation,entity_name,label,enabled,expires_at,
                      max_requests_per_minute,created_at,last_used_at
            """,db,tx))
        {
            cmd.Parameters.AddWithValue("id",keyId);
            cmd.Parameters.AddWithValue("conn",policy.Connection.Id);
            cmd.Parameters.AddWithValue("template",summary.Id);
            cmd.Parameters.AddWithValue("version",revision.Version);
            cmd.Parameters.AddWithValue("name",request.TemplateName);
            cmd.Parameters.AddWithValue("op",definition.Operation);
            cmd.Parameters.AddWithValue("entity",definition.EntityName);
            cmd.Parameters.AddWithValue("fields",fields.ToArray());
            cmd.Parameters.AddWithValue("keys",keys);
            cmd.Parameters.AddWithValue("client",clientId);
            cmd.Parameters.AddWithValue("hash",digest);
            cmd.Parameters.AddWithValue("label",request.Label.Trim());
            cmd.Parameters.AddWithValue("expires",expires);
            cmd.Parameters.AddWithValue("quota",request.MaxRequestsPerMinute);
            cmd.Parameters.AddWithValue("actor",actor.Length<=128?actor:actor[..128]);
            await using var row=await cmd.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct))throw new ReadRejected(409,"template_version_changed");
            issued=Read(row);
        }
        await AuditAsync(db,tx,policy.Connection.Id,actor,"template_key.created",ct);
        await tx.CommitAsync(ct);
        return new(issued,secret);
    }

    public async Task<CrudIntegrationKeyIdentity> ValidateAndChargeAsync(string? token,
        string connectionCode,string name,CancellationToken ct)
    {
        if(!TryParseSecret(token,out var id) || string.IsNullOrWhiteSpace(connectionCode) ||
           string.IsNullOrWhiteSpace(name))throw new ReadRejected(401,"crud_key_invalid");
        var provided=SHA256.HashData(Encoding.UTF8.GetBytes(token!));
        await using var db=await database.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT k.key_sha256,k.connection_id,k.operation,k.entity_name,
                   k.key_fields,k.max_requests_per_minute
            FROM dynamics.template_api_keys k
            JOIN dynamics.connections c ON c.id=k.connection_id
            JOIN dynamics.integration_templates t ON t.id=k.template_id
            JOIN dynamics.api_clients a ON a.id=k.client_id
            WHERE k.id=@id AND c.code=@code AND t.name=@name AND k.enabled=true
              AND k.expires_at>now() AND t.active_version=k.template_version
              AND a.enabled=true
            FOR UPDATE OF k
            """,db,tx);
        cmd.Parameters.AddWithValue("id",id);
        cmd.Parameters.AddWithValue("code",connectionCode);
        cmd.Parameters.AddWithValue("name",name);
        byte[] expected;Guid connection;string operation;string entity;string[] keys;int quota;
        await using(var rows=await cmd.ExecuteReaderAsync(ct))
        {
            if(!await rows.ReadAsync(ct))throw new ReadRejected(401,"crud_key_invalid");
            expected=rows.GetFieldValue<byte[]>(0);connection=rows.GetGuid(1);
            operation=rows.GetString(2);entity=rows.GetString(3);
            keys=rows.GetFieldValue<string[]>(4);quota=rows.GetInt32(5);
        }
        if(!CryptographicOperations.FixedTimeEquals(expected,provided))
            throw new ReadRejected(401,"crud_key_invalid");
        await using(var charge=new NpgsqlCommand("""
            INSERT INTO dynamics.template_api_key_quota(key_id,minute,requests)
            VALUES(@id,@minute,1)
            ON CONFLICT(key_id,minute) DO UPDATE
                SET requests=dynamics.template_api_key_quota.requests+1
            WHERE dynamics.template_api_key_quota.requests<@quota
            RETURNING requests
            """,db,tx))
        {
            charge.Parameters.AddWithValue("id",id);
            charge.Parameters.AddWithValue("minute",DateTimeOffset.UtcNow.ToUnixTimeSeconds()/60);
            charge.Parameters.AddWithValue("quota",quota);
            if(await charge.ExecuteScalarAsync(ct) is null)
                throw new ReadRejected(429,"crud_key_quota_exceeded");
        }
        await using(var used=new NpgsqlCommand("""
            UPDATE dynamics.template_api_keys SET last_used_at=now() WHERE id=@id
            """,db,tx))
        {used.Parameters.AddWithValue("id",id);await used.ExecuteNonQueryAsync(ct);}
        await tx.CommitAsync(ct);
        return new(id,connection,id.ToString("D"),name,operation,entity,keys);
    }

    public async Task<bool> RevokeAsync(string code,Guid id,string actor,CancellationToken ct)
    {
        await using var db=await database.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            UPDATE dynamics.template_api_keys k SET enabled=false
            FROM dynamics.connections c
            WHERE k.id=@id AND c.id=k.connection_id AND c.code=@code
            RETURNING k.connection_id
            """,db,tx);
        cmd.Parameters.AddWithValue("id",id);
        cmd.Parameters.AddWithValue("code",code);
        var found=await cmd.ExecuteScalarAsync(ct);
        if(found is not Guid conn)return false;
        await AuditAsync(db,tx,conn,actor,"template_key.revoked",ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task<(TemplateSummary,TemplateRevision,WritePolicy)> ResolveActiveAsync(
        string code,string name,CancellationToken ct)
    {
        if(name is not {Length: >= 1 and <= 80} ||
           !System.Text.RegularExpressions.Regex.IsMatch(name,@"^[A-Za-z][A-Za-z0-9_-]{0,79}$"))
            throw new ReadRejected(400,"invalid_template_name");
        await using var db=await database.OpenConnectionAsync(ct);
        Guid connection;
        await using(var cmd=new NpgsqlCommand(
            "SELECT id FROM dynamics.connections WHERE code=@code",db))
        {cmd.Parameters.AddWithValue("code",code);
         connection=await cmd.ExecuteScalarAsync(ct) is Guid value?value:
            throw new ReadRejected(404,"connection_missing");}
        var summary=await templates.FindAsync(connection,name,ct);
        if(summary?.ActiveVersion is not int version)
            throw new ReadRejected(409,"active_template_required");
        var revision=summary.History.FirstOrDefault(v=>v.Version==version) ??
            throw new ReadRejected(409,"template_version_missing");
        var policy=await writeAccess.ResolveAsync(code,revision.Definition.EntityName,ct);
        TemplateMapper.ValidateDefinition(revision.Definition,policy);
        return(summary,revision,policy);
    }

    private static CrudIntegrationKeyInfo Read(NpgsqlDataReader row)=>
        new(row.GetGuid(0),row.GetString(1),row.GetString(2),row.GetString(3),
            row.GetString(4),row.GetBoolean(5),row.GetFieldValue<DateTimeOffset>(6),
            row.GetInt32(7),row.GetFieldValue<DateTimeOffset>(8),
            row.IsDBNull(9)?null:row.GetFieldValue<DateTimeOffset>(9));

    private static async Task AuditAsync(NpgsqlConnection db,NpgsqlTransaction tx,
        Guid connection,string actor,string action,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
            VALUES(@id,@connection,@actor,@action)
            """,db,tx);
        cmd.Parameters.AddWithValue("id",Guid.NewGuid());
        cmd.Parameters.AddWithValue("connection",connection);
        cmd.Parameters.AddWithValue("actor",actor);
        cmd.Parameters.AddWithValue("action",action);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
