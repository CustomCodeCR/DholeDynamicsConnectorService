using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Npgsql;

namespace Dhole.Dynamics.Api;

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
        string actor,CancellationToken ct,IReadOnlyList<string>? managedPricingFields=null)
    {
        await using var db=await database.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            UPDATE dynamics.integration_templates t
            SET active_version=@version,updated_at=now()
            WHERE connection_id=@connection AND name=@name
              AND (CAST(@version AS integer) IS NULL OR EXISTS(
                SELECT 1 FROM dynamics.template_versions v
                WHERE v.template_id=t.id AND v.version=@version))
            RETURNING id
            """,db,tx);
        cmd.Parameters.AddWithValue("connection",connection);
        cmd.Parameters.AddWithValue("name",name);
        cmd.Parameters.AddWithValue("version",(object?)version??DBNull.Value);
        if(await cmd.ExecuteScalarAsync(ct) is not Guid templateId)return false;
        // SecurityAdmin approved this published definition and every field in
        // managedPricingFields passed live metadata and policy validation.
        // Re-pin ONLY currently enabled Dhole_System keys. This is the SAME
        // transaction as the active template pointer, preventing a window in
        // which a previously configured key becomes invalid on activation.
        if (name == "crear_oferta" && version is int revision &&
            managedPricingFields is { Count: > 0 })
        {
            await using var migrate = new NpgsqlCommand("""
                UPDATE dynamics.template_api_keys k
                   SET template_version=@revision,allowed_fields=@fields
                FROM dynamics.api_clients client
                WHERE k.template_id=@template AND k.connection_id=@connection
                  AND k.client_id=client.id AND client.enabled=true
                  AND k.enabled=true AND k.expires_at>now()
                  AND k.entity_name='quote' AND k.operation='create'
                  AND lower(k.label)='dhole_system'
                RETURNING k.client_id
                """,db,tx);
            migrate.Parameters.AddWithValue("revision",revision);
            migrate.Parameters.AddWithValue("fields",managedPricingFields.ToArray());
            migrate.Parameters.AddWithValue("template",templateId);
            migrate.Parameters.AddWithValue("connection",connection);
            var clients = new List<Guid>();
            await using(var rows = await migrate.ExecuteReaderAsync(ct))
                while(await rows.ReadAsync(ct)) clients.Add(rows.GetGuid(0));
            foreach (var clientId in clients)
            {
                await using var grant = new NpgsqlCommand("""
                    UPDATE dynamics.api_grants
                       SET allowed_fields=ARRAY(
                           SELECT DISTINCT unnest(allowed_fields || @fields))
                    WHERE api_client_id=@client AND connection_id=@connection
                      AND entity_set='quote' AND operation='create'
                      AND enabled=true
                    """,db,tx);
                grant.Parameters.AddWithValue("client",clientId);
                grant.Parameters.AddWithValue("connection",connection);
                grant.Parameters.AddWithValue("fields",managedPricingFields.ToArray());
                if (await grant.ExecuteNonQueryAsync(ct) != 1)
                    throw new ReadRejected(409,"managed_quote_create_grant_missing");
            }
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
