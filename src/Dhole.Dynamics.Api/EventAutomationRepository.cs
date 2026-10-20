using Dhole.Dynamics.Application;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Dhole.Dynamics.Api;

public sealed record AutomationInputMapping(string InputName,string PricingField);
public sealed record AutomationRuleRequest(string Name,string TriggerCode,string TemplateName,
    IReadOnlyList<AutomationInputMapping> Mappings);
public sealed record AutomationRule(Guid Id,string Name,string TriggerCode,
    string TemplateName,IReadOnlyList<AutomationInputMapping> Mappings,
    bool Enabled,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt);

/// <summary>
/// Durable event-to-template configuration. Intentionally drafts only:
/// neither reading the rules nor saving one authorizes a Dataverse write.
/// </summary>
public sealed class EventAutomationRepository(NpgsqlDataSource database,
    IConnectionRepository connections,TemplateRepository templates)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public static readonly IReadOnlyDictionary<string,string> PricingFields =
        new Dictionary<string,string>(StringComparer.Ordinal)
        {
            ["rateId"]="guid",
            ["rateCode"]="string",
            ["rateName"]="string",
            ["clientName"]="string",
            ["quoNumber"]="string",
            ["idtraNumber"]="string",
            ["currencyCode"]="string",
            ["polName"]="string",
            ["podName"]="string",
            ["poeName"]="string",
            ["incotermCode"]="string",
            ["totalSaleAmount"]="decimal",
            ["totalCostAmount"]="decimal",
            ["validFrom"]="datetime",
            ["validTo"]="datetime",
            ["shipmentMode"]="string",
            ["executiveName"]="string"
        };

    public async Task<IReadOnlyList<AutomationRule>> ListAsync(string code,CancellationToken ct)
    {
        var result=new List<AutomationRule>();
        await using var db=await database.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT r.id,r.name,r.trigger_code,r.template_name,r.mapping::text,
                   r.enabled,r.created_at,r.updated_at
            FROM dynamics.event_automation_rules r JOIN dynamics.connections c
                 ON c.id=r.connection_id
            WHERE c.code=@code ORDER BY r.updated_at DESC LIMIT 100
            """,db);
        cmd.Parameters.AddWithValue("code",code);
        await using var rows=await cmd.ExecuteReaderAsync(ct);
        while(await rows.ReadAsync(ct))
            result.Add(FromRow(rows));
        return result;
    }

    public async Task<AutomationRule> SaveDraftAsync(string code,AutomationRuleRequest request,
        string actor,CancellationToken ct)
    {
        if(!Regex.IsMatch(request.Name??"",@"^[a-z][a-z0-9_-]{0,79}$",
               RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)) ||
           request.TriggerCode is not ("pricing.rate.accepted_by_client" or
               "pricing.rate.margin_approved") ||
           request.TemplateName is null || request.Mappings is not {Count: <= 30} ||
           request.Mappings.Any(m=>string.IsNullOrWhiteSpace(m.InputName) ||
               !PricingFields.ContainsKey(m.PricingField)) ||
           request.Mappings.Select(m=>m.InputName).Distinct(StringComparer.Ordinal).Count()
               !=request.Mappings.Count)
            throw new ReadRejected(400,"invalid_automation_blueprint");
        var conn=await connections.FindAsync(code,ct) ??
            throw new ReadRejected(404,"connection_not_found");
        var template=await templates.FindAsync(conn.Id,request.TemplateName,ct) ??
            throw new ReadRejected(409,"automation_template_not_found");
        var revision=template.ActiveVersion is int active
            ? template.History.FirstOrDefault(x=>x.Version==active)
            : template.History.OrderByDescending(x=>x.Version).FirstOrDefault();
        if(revision?.Definition is not {EntityName:"quote",Operation:"create"} definition)
            throw new ReadRejected(409,"automation_requires_quote_create_template");
        // Drafts may intentionally have unmapped mandatory inputs. They can never
        // run until a separate executor verifies the full mapping in staging.
        if(request.Mappings.Any(m=>definition.Inputs.All(x=>x.Name!=m.InputName)))
            throw new ReadRejected(400,"automation_unknown_input");
        foreach(var mapping in request.Mappings)
        {
            var input=definition.Inputs.First(i=>i.Name==mapping.InputName);
            if(PricingFields[mapping.PricingField]!=input.Type)
                throw new ReadRejected(400,"automation_input_type_mismatch");
            // Pricing GUIDs are not Dataverse GUIDs. Never mistake RateId for
            // a customer, currency or owner CRM reference during mapping.
            if(mapping.PricingField=="rateId" &&
               input.Name is not ("rateId" or "dholeRateId" or "rate_id" or "dhole_rate_id"))
                throw new ReadRejected(400,"cross_system_reference_required");
        }
        await using var db=await database.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.event_automation_rules
                (id,connection_id,name,trigger_code,template_name,mapping,enabled,created_by)
            VALUES(@id,@connection,@name,@trigger,@template,CAST(@mapping AS jsonb),false,@actor)
            ON CONFLICT(connection_id,name) DO UPDATE SET
                trigger_code=EXCLUDED.trigger_code,
                template_name=EXCLUDED.template_name,
                mapping=EXCLUDED.mapping,
                enabled=false,updated_at=now()
            RETURNING id,name,trigger_code,template_name,mapping::text,
                      enabled,created_at,updated_at
            """,db,tx);
        cmd.Parameters.AddWithValue("id",Guid.NewGuid());
        cmd.Parameters.AddWithValue("connection",conn.Id);
        cmd.Parameters.AddWithValue("name",request.Name);
        cmd.Parameters.AddWithValue("trigger",request.TriggerCode);
        cmd.Parameters.AddWithValue("template",request.TemplateName);
        cmd.Parameters.AddWithValue("mapping",JsonSerializer.Serialize(request.Mappings,Json));
        cmd.Parameters.AddWithValue("actor",actor);
        AutomationRule result;
        await using(var reader=await cmd.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct))
                throw new ReadRejected(409,"automation_save_failed");
            result=FromRow(reader);
        }
        await using var audit=new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
            VALUES(@id,@conn,@actor,'automation.draft.saved')
            """,db,tx);
        audit.Parameters.AddWithValue("id",Guid.NewGuid());
        audit.Parameters.AddWithValue("conn",conn.Id);
        audit.Parameters.AddWithValue("actor",actor);
        await audit.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private static AutomationRule FromRow(NpgsqlDataReader row)=>
        new(row.GetGuid(0),row.GetString(1),row.GetString(2),row.GetString(3),
            JsonSerializer.Deserialize<AutomationInputMapping[]>(row.GetString(4),Json) ?? [],
            row.GetBoolean(5),row.GetFieldValue<DateTimeOffset>(6),
            row.GetFieldValue<DateTimeOffset>(7));
}
