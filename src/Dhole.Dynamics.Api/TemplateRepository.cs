using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Npgsql;

namespace Dhole.Dynamics.Api;

// Only previously authorized CREATE fields can be reused on a new revision.
// This is not a request to add fields, renew expiration or reactivate a key.
public sealed record TemplateActivationKeyCompatibility(
    string EntityName,
    string[] RequiredFields);

/// <summary>Immutable revisions + atomic active pointer. No caches, so activation takes effect immediately.</summary>
public sealed class TemplateRepository(NpgsqlDataSource database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<TemplateSummary>> ListAsync(Guid connection, CancellationToken ct)
    {
        var names = new List<(Guid Id,string Name,int? Active)>();
        await using var db = await database.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            SELECT id,name,active_version FROM dynamics.integration_templates
            WHERE connection_id=@connection ORDER BY name LIMIT 200
            """,db))
        {
            cmd.Parameters.AddWithValue("connection",connection);
            await using var reader=await cmd.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))
                names.Add((reader.GetGuid(0),reader.GetString(1),
                    reader.IsDBNull(2)?null:reader.GetInt32(2)));
        }
        var result = new List<TemplateSummary>();
        foreach(var (id,name,active) in names)
            result.Add(new(id,name,active,await HistoryAsync(db,id,ct)));
        return result;
    }

    public async Task<TemplateSummary?> FindAsync(Guid connection,string name,CancellationToken ct)
    {
        await using var db=await database.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT id,active_version FROM dynamics.integration_templates
            WHERE connection_id=@conn AND name=@name
            """,db);
        cmd.Parameters.AddWithValue("conn",connection);
        cmd.Parameters.AddWithValue("name",name);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct))return null;
        var id=r.GetGuid(0);
        int? active=r.IsDBNull(1)?null:r.GetInt32(1);
        await r.DisposeAsync();
        return new(id,name,active,await HistoryAsync(db,id,ct));
    }

    public async Task<TemplateSummary> NewRevisionAsync(Guid connection,string name,
        IntegrationTemplate definition,int metadataVersion,string actor,CancellationToken ct)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(name??"",
            @"^[A-Za-z][A-Za-z0-9_-]{0,79}$")||metadataVersion<1)
            throw new ReadRejected(400,"invalid_template_name");
        var body=JsonSerializer.Serialize(definition,Json);
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        await using var db=await database.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using (var lockCmd=new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext(@id), 7007)",db,tx))
        {
            lockCmd.Parameters.AddWithValue("id",connection.ToString("D")+":"+name);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        Guid id;
        await using(var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.integration_templates(id,connection_id,name)
            VALUES(@id,@connection,@name)
            ON CONFLICT(connection_id,name) DO UPDATE SET updated_at=now()
            RETURNING id
            """,db,tx))
        {
            cmd.Parameters.AddWithValue("id",Guid.NewGuid());
            cmd.Parameters.AddWithValue("connection",connection);
            cmd.Parameters.AddWithValue("name",name);
            id=(Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
        int version;
        await using(var cmd=new NpgsqlCommand("""
            SELECT coalesce(max(version),0)+1 FROM dynamics.template_versions WHERE template_id=@id
            """,db,tx))
        {
            cmd.Parameters.AddWithValue("id",id);
            version=Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        await using(var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.template_versions(template_id,version,definition,definition_sha256,metadata_version,created_by)
            VALUES(@id,@v,CAST(@body AS jsonb),@hash,@metadata,@actor)
            """,db,tx))
        {
            cmd.Parameters.AddWithValue("id",id);
            cmd.Parameters.AddWithValue("v",version);
            cmd.Parameters.AddWithValue("body",body);
            cmd.Parameters.AddWithValue("hash",hash);
            cmd.Parameters.AddWithValue("metadata",metadataVersion);
            cmd.Parameters.AddWithValue("actor",actor);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(db,tx,connection,actor,"template.revision.created",ct);
        await tx.CommitAsync(ct);
        return (await FindAsync(connection,name,ct))!;
    }

    public async Task<bool> SetActiveAsync(Guid connection,string name,int? version,
        string actor,CancellationToken ct,
        TemplateActivationKeyCompatibility? compatibleKeys = null)
    {
        await using var db=await database.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);

        // Serialize template activations with key-version synchronization. A key
        // cannot observe the new active pointer with the previous pin when this
        // transaction commits. Do not revive keys from older, inactive revisions.
        Guid templateId;
        int? previousVersion;
        await using(var previous=new NpgsqlCommand("""
            SELECT id,active_version FROM dynamics.integration_templates
            WHERE connection_id=@connection AND name=@name
            FOR UPDATE
            """,db,tx))
        {
            previous.Parameters.AddWithValue("connection",connection);
            previous.Parameters.AddWithValue("name",name);
            await using var reader=await previous.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct))return false;
            templateId=reader.GetGuid(0);
            previousVersion=reader.IsDBNull(1)?null:reader.GetInt32(1);
        }

        await using(var cmd=new NpgsqlCommand("""
            UPDATE dynamics.integration_templates t
            SET active_version=@version,updated_at=now()
            WHERE id=@id AND connection_id=@connection
              AND (CAST(@version AS integer) IS NULL OR EXISTS(
                SELECT 1 FROM dynamics.template_versions v
                WHERE v.template_id=t.id AND v.version=@version))
            RETURNING id
            """,db,tx))
        {
            cmd.Parameters.AddWithValue("id",templateId);
            cmd.Parameters.AddWithValue("connection",connection);
            cmd.Parameters.AddWithValue("version",(object?)version??DBNull.Value);
            if(await cmd.ExecuteScalarAsync(ct) is null)return false;
        }

        if(version is int nextVersion && previousVersion is int oldVersion
            && nextVersion!=oldVersion && compatibleKeys is not null
            && compatibleKeys.RequiredFields.Length>0)
        {
            // Fail closed: a still-enabled, unexpired key may follow its CURRENT
            // active CREATE template only if both its pinned field allowlist and
            // its active client write grant already contain every new field.
            // Do not touch disabled/revoked clients, keys, quota or expiry.
            // If operation, entity or fields change incompatibly, the key stays
            // pinned to the old version until an explicit SecurityAdmin rebind.
            await using var rebind=new NpgsqlCommand("""
                UPDATE dynamics.template_api_keys k
                   SET template_version=@next
                  FROM dynamics.api_clients a, dynamics.api_grants g
                 WHERE k.template_id=@template AND k.connection_id=@connection
                   AND k.template_version=@previous
                   AND k.operation='create' AND k.entity_name=@entity
                   AND k.enabled=true AND k.expires_at>now()
                   AND a.id=k.client_id AND a.enabled=true
                   AND g.api_client_id=k.client_id
                   AND g.connection_id=k.connection_id
                   AND g.entity_set=k.entity_name AND g.operation='create'
                   AND g.enabled=true
                   AND k.allowed_fields @> @required
                   AND g.allowed_fields @> @required
                """,db,tx);
            rebind.Parameters.AddWithValue("template",templateId);
            rebind.Parameters.AddWithValue("connection",connection);
            rebind.Parameters.AddWithValue("previous",oldVersion);
            rebind.Parameters.AddWithValue("next",nextVersion);
            rebind.Parameters.AddWithValue("entity",compatibleKeys.EntityName);
            rebind.Parameters.AddWithValue("required",compatibleKeys.RequiredFields);
            if(await rebind.ExecuteNonQueryAsync(ct)>0)
                await AuditAsync(db,tx,connection,actor,
                    "template_key.compatible_version_rebound",ct);
        }

        await AuditAsync(db,tx,connection,actor,
            version is null?"template.deactivated":"template.activated",ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task<IReadOnlyList<TemplateRevision>> HistoryAsync(NpgsqlConnection db,
        Guid id,CancellationToken ct)
    {
        var items=new List<TemplateRevision>();
        await using var cmd=new NpgsqlCommand("""
            SELECT version,definition::text,metadata_version,created_at,definition_sha256
            FROM dynamics.template_versions WHERE template_id=@id ORDER BY version DESC LIMIT 50
            """,db);
        cmd.Parameters.AddWithValue("id",id);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
            items.Add(new(r.GetInt32(0),
                JsonSerializer.Deserialize<IntegrationTemplate>(r.GetString(1),Json)!,
                r.GetInt32(2),r.GetFieldValue<DateTimeOffset>(3),r.GetString(4)));
        return items;
    }

    private static async Task AuditAsync(NpgsqlConnection db,NpgsqlTransaction tx,Guid connection,
        string actor,string action,CancellationToken ct)
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
