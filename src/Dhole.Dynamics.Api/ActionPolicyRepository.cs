using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Npgsql;

namespace Dhole.Dynamics.Api;

public sealed record BoundActionPolicy(string EntityName,string ActionName,
    IReadOnlyList<TemplateInput> Parameters,bool Enabled);
public sealed record BoundActionWrite(string EntityName,string ActionName,
    IReadOnlyList<TemplateInput> Parameters,bool Enabled = false);

/// <summary>Explicit action allowlist; never inferred from client-requested URLs.</summary>
public sealed class ActionPolicyRepository(NpgsqlDataSource database)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public async Task<BoundActionPolicy?> FindAsync(Guid connection,string entity,string name,CancellationToken ct)
    {
        await using var db=await database.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT parameters::text,enabled FROM dynamics.allowed_actions
            WHERE connection_id=@conn AND entity_name=@entity AND action_name=@name
            """,db);
        cmd.Parameters.AddWithValue("conn",connection);
        cmd.Parameters.AddWithValue("entity",entity);
        cmd.Parameters.AddWithValue("name",name);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))return null;
        return new(entity,name,JsonSerializer.Deserialize<TemplateInput[]>(reader.GetString(0),Json)??[],
            reader.GetBoolean(1));
    }
    public async Task<IReadOnlyList<BoundActionPolicy>> ListAsync(Guid connection,CancellationToken ct)
    {
        var result=new List<BoundActionPolicy>();
        await using var db=await database.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT entity_name,action_name,parameters::text,enabled
            FROM dynamics.allowed_actions WHERE connection_id=@conn ORDER BY entity_name,action_name
            """,db);
        cmd.Parameters.AddWithValue("conn",connection);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
            result.Add(new(r.GetString(0),r.GetString(1),
                JsonSerializer.Deserialize<TemplateInput[]>(r.GetString(2),Json)??[],r.GetBoolean(3)));
        return result;
    }
    public async Task<BoundActionPolicy> SetAsync(Guid connection,BoundActionWrite value,
        string actor,CancellationToken ct)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(value.ActionName??"",
              @"^[A-Za-z][A-Za-z0-9_]{0,79}$") ||
            !Dhole.Dynamics.Infrastructure.DataverseMetadataDiscovery.SafeName(value.EntityName) ||
            value.Parameters is not {Count: <= 20} ||
            value.Parameters.Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=value.Parameters.Count ||
            value.Parameters.Any(p=>!ReadQueryCompiler.ValidParam(p.Name) || p.Type is not
                ("string" or "guid" or "integer" or "decimal" or "boolean" or "datetime") ||
                p.Pattern is not null || p.Default is not null))
            throw new ReadRejected(400,"invalid_bound_action_definition");
        // Schema changes revoke approval, requiring a separate explicit re-enable.
        await using var db=await database.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.allowed_actions(connection_id,entity_name,action_name,parameters,enabled)
            SELECT @conn,@entity,@name,CAST(@parameters AS jsonb),false
            FROM dynamics.metadata_allowed_entities
            WHERE connection_id=@conn AND logical_name=@entity AND enabled
            ON CONFLICT(connection_id,entity_name,action_name) DO UPDATE SET
                parameters=EXCLUDED.parameters,enabled=false,updated_at=now()
            RETURNING action_name
            """,db,tx);
        cmd.Parameters.AddWithValue("conn",connection);
        cmd.Parameters.AddWithValue("entity",value.EntityName);
        cmd.Parameters.AddWithValue("name",value.ActionName);
        cmd.Parameters.AddWithValue("parameters",JsonSerializer.Serialize(value.Parameters,Json));
        if(await cmd.ExecuteScalarAsync(ct) is null)
            throw new ReadRejected(403,"action_entity_not_enabled");
        await Audit(db,tx,connection,actor,"action.policy.saved",ct);
        await tx.CommitAsync(ct);
        return new(value.EntityName,value.ActionName,value.Parameters,false);
    }
    public async Task<BoundActionPolicy> EnableAsync(Guid connection,string entity,string name,
        bool enable,string actor,CancellationToken ct)
    {
        await using var db=await database.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            UPDATE dynamics.allowed_actions a SET enabled=@enabled,updated_at=now()
            WHERE connection_id=@conn AND entity_name=@entity AND action_name=@name
              AND EXISTS(SELECT 1 FROM dynamics.metadata_allowed_entities e
                WHERE e.connection_id=@conn AND e.logical_name=@entity AND e.enabled)
            RETURNING parameters::text
            """,db,tx);
        cmd.Parameters.AddWithValue("conn",connection);
        cmd.Parameters.AddWithValue("entity",entity);
        cmd.Parameters.AddWithValue("name",name);
        cmd.Parameters.AddWithValue("enabled",enable);
        var value=await cmd.ExecuteScalarAsync(ct) as string
            ?? throw new ReadRejected(404,"action_policy_missing");
        await Audit(db,tx,connection,actor,enable?"action.policy.enabled":"action.policy.disabled",ct);
        await tx.CommitAsync(ct);
        return new(entity,name,JsonSerializer.Deserialize<TemplateInput[]>(value,Json)??[],enable);
    }
    private static async Task Audit(NpgsqlConnection db,NpgsqlTransaction tx,Guid conn,
        string actor,string action,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
            VALUES(@id,@connection,@actor,@action)
            """,db,tx);
        cmd.Parameters.AddWithValue("id",Guid.NewGuid());cmd.Parameters.AddWithValue("connection",conn);
        cmd.Parameters.AddWithValue("actor",actor);cmd.Parameters.AddWithValue("action",action);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
