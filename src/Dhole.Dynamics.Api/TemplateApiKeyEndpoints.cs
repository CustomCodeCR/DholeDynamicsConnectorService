using System.Security.Claims;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

namespace Dhole.Dynamics.Api;

/// <summary>
/// API keys for a single active, immutable CRUD template. No raw OData,
/// anonymous reads, arbitrary fields, scripts, or OAuth tokens in URL.
/// </summary>
public static class TemplateApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapTemplateApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var admin=app.MapGroup("/api/admin/v1/connections/{code}/crud-keys")
            .WithGroupName("admin-v1")
            .RequireAuthorization("SecurityAdmin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        admin.MapGet("",List);
        admin.MapPost("",Issue);
        admin.MapDelete("/{id:guid}",Revoke);
        app.MapPost("/api/v1/keyed/templates/{name}/execute",Execute)
            .WithGroupName("v1").AllowAnonymous().RequireRateLimiting("AdminRate");
        app.MapGet("/api/v1/keyed/templates/{name}/schema",Schema)
            .WithGroupName("v1").AllowAnonymous().RequireRateLimiting("AdminRate");
        return app;
    }

    private static Task<IResult> List(string code,TemplateApiKeyRepository keys,
        HttpContext context,CancellationToken ct) =>
        Guard(context,async()=>Results.Ok(await keys.ListAsync(code,ct)));

    private static Task<IResult> Issue(string code,CrudIntegrationKeyRequest request,
        TemplateApiKeyRepository keys,ClaimsPrincipal actor,HttpContext context,CancellationToken ct) =>
        Guard(context,async()=>Results.Ok(await keys.IssueAsync(code,request,
            ScopeSecurity.ActorId(actor),ct)));

    private static Task<IResult> Revoke(string code,Guid id,
        TemplateApiKeyRepository keys,ClaimsPrincipal actor,HttpContext context,CancellationToken ct) =>
        Guard(context,async()=>await keys.RevokeAsync(code,id,ScopeSecurity.ActorId(actor),ct)
            ? Results.NoContent() : ApiProblems.Create(context,404,"crud_key_not_found",
                "CRUD integration key not found."));

    private static Task<IResult> Schema(string name,string connectionCode,
        TemplateApiKeyRepository keys,TemplateRepository templates,
        WriteAccessService writer,DataverseMetadataDiscovery discovery,
        HttpContext context,CancellationToken ct) =>
        Guard(context,async()=>
        {
            var key=await keys.ValidateAndChargeAsync(
                context.Request.Headers["X-Dhole-Api-Key"].ToString(),
                connectionCode,name,ct);
            var template=await templates.FindAsync(key.ConnectionId,name,ct)
                ?? throw new ReadRejected(404,"crud_template_not_found");
            if(template.ActiveVersion is not int active)
                throw new ReadRejected(403,"crud_template_inactive");
            var revision=template.History.FirstOrDefault(v=>v.Version==active)
                ?? throw new ReadRejected(403,"crud_template_revision_missing");
            if(revision.Definition.Operation!=key.Operation ||
               revision.Definition.EntityName!=key.EntityName)
                throw new ReadRejected(403,"crud_template_scope_denied");
            var policy=await writer.ResolveAsync(connectionCode,key.EntityName,ct);
            var schema = CrudTemplateSchemaBuilder.Build(name,connectionCode,
                active,revision.Definition,policy);
            var fields = new List<CrudSchemaField>(schema.Fields.Count);
            foreach (var field in schema.Fields)
            {
                if (field.DataverseType is "Picklist" or "State" or "Status")
                {
                    var choices = await discovery.DiscoverChoicesAsync(policy.Connection,
                        schema.EntityName, field.Target, field.DataverseType, ct);
                    if (choices.Count == 0)
                        throw new ReadRejected(409, "crud_choice_metadata_missing");
                    fields.Add(field with { Options = choices.Select(o => new CrudSchemaOption(
                        CrudTemplateSchemaBuilder.Label(o.Labels, o.Value.ToString()),
                        o.Value)).ToArray() });
                }
                else fields.Add(field);
            }
            return Results.Ok(schema with { Fields = fields });
        });

    private static Task<IResult> Execute(string name,TemplateInputs request,
        TemplateApiKeyRepository keys,TemplateOrchestrator orchestrator,
        ProductionWritePolicy gate,HttpContext context,CancellationToken ct) =>
        Guard(context,async()=>
        {
            if(request.ConnectionCode is null || request.Inputs is null)
                throw new ReadRejected(400,"invalid_crud_execution");
            var key=await keys.ValidateAndChargeAsync(
                context.Request.Headers["X-Dhole-Api-Key"].ToString(),
                request.ConnectionCode,name,ct);
            if(key.Operation is not ("create" or "update" or "upsert" or "delete"))
                throw new ReadRejected(403,"crud_operation_denied");
            // POST /execute cannot be recognized as HTTP DELETE by the gateway.
            // Explicitly enforce the *second*, independent delete approval.
            gate.Demand(key.Operation);
            if(key.Operation=="upsert" &&
                (request.Keys is null || request.Keys.Count!=key.KeyFields.Count ||
                 request.Keys.Keys.Any(f=>!key.KeyFields.Contains(f,StringComparer.Ordinal))))
                throw new ReadRejected(403,"crud_alternate_keys_denied");
            if(key.Operation!="upsert" && request.Keys is {Count: > 0})
                throw new ReadRejected(403,"crud_alternate_keys_denied");
            if(key.Operation is "update" or "delete" &&
                (request.RecordId is null || request.RecordId==Guid.Empty ||
                 string.IsNullOrWhiteSpace(request.ETag)))
                throw new ReadRejected(400,"record_id_and_etag_required");
            if(!WriteLedger.ValidKey(context.Request.Headers["Idempotency-Key"].ToString()))
                throw new ReadRejected(400,"idempotency_key_required");

            // Identity is fabricated ONLY after a cryptographic DB-key check,
            // active revision pin, quota charge, and scope validation. It then
            // passes through the ordinary enabled client/grant and write-ledger
            // checks, never an elevated admin role.
            var claims=new[]{
                new Claim("connector:issuer",TemplateApiKeyRepository.Issuer),
                new Claim("client_id",key.ClientId),
                new Claim("scope","dynamics.records."+key.Operation),
                new Claim("sub",key.Id.ToString("D"))
            };
            var principal=new ClaimsPrincipal(new ClaimsIdentity(claims,"DholeCrudApiKey"));
            var result=await orchestrator.ExecuteAsync(principal,name,request,
                context.Request.Headers["Idempotency-Key"].ToString(),ct);
            return result is WriteResult write
                ? Results.Json(write,statusCode:write.State=="ambiguous"?409:
                    write.State=="rejected"?write.HttpStatus??502:200)
                : Results.Ok(result);
        });

    private static async Task<IResult> Guard(HttpContext context,Func<Task<IResult>> action)
    {
        try{return await action();}
        catch(ReadRejected ex){return ApiProblems.Create(context,ex.Status,ex.Code,
            ex.Status==401?"Invalid or expired integration key.":"CRUD integration request rejected.");}
        catch(ArgumentException){return ApiProblems.Create(context,400,"invalid_crud_request","Invalid CRUD input.");}
        catch(HttpRequestException){return ApiProblems.Create(context,502,"dataverse_unavailable","Dataverse unavailable.");}
        catch(TaskCanceledException){return ApiProblems.Create(context,504,"dataverse_timeout","Dataverse timed out.");}
        catch(Microsoft.Identity.Client.MsalException){return ApiProblems.Create(context,502,"dataverse_identity","Dataverse token unavailable.");}
    }
}
