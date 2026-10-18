using System.Security.Claims;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Microsoft.AspNetCore.RateLimiting;

namespace Dhole.Dynamics.Api;

/// <summary>Admin-only immutable revisions; strictly M2M-only execution. No generic OData proxy.</summary>
public static class TemplateEndpoints
{
    public static IEndpointRouteBuilder MapTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var admin=app.MapGroup("/api/admin/v1/connections/{code}")
            .WithGroupName("admin-v1").RequireAuthorization("Admin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        admin.MapGet("/templates",List);
        admin.MapGet("/templates/{name}",Detail);
        admin.MapPost("/templates/{name}/versions",Create);
        admin.MapPost("/templates/{name}/versions/{version:int}/preview",Preview);
        admin.MapPut("/templates/{name}/active",Activate);
        admin.MapGet("/templates/{name}/export",Export);
        admin.MapPost("/templates/import",Import);
        admin.MapGet("/references/{space}/{entity}/{internalId}",GetReference);

        var security=app.MapGroup("/api/admin/v1/connections/{code}")
            .WithGroupName("admin-v1").RequireAuthorization("SecurityAdmin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        security.MapPut("/references",SetReference);

        app.MapPost("/api/v1/templates/{name}/execute",Execute)
            .WithGroupName("v1").RequireAuthorization("M2M");
        return app;
    }

    private static async Task<IResult> List(string code,ReadAccessRepository access,
        TemplateRepository repo,HttpContext http,CancellationToken ct) =>
        await Guard(http,async()=>{
            var conn=await access.ConnectionAsync(code,ct);
            return Results.Ok(await repo.ListAsync(conn.Id,ct));
        });

    private static async Task<IResult> Detail(string code,string name,ReadAccessRepository access,
        TemplateRepository repo,HttpContext http,CancellationToken ct) =>
        await Guard(http,async()=>{
            var conn=await access.ConnectionAsync(code,ct);
            return await repo.FindAsync(conn.Id,name,ct) is { } item
                ? Results.Ok(item) : ApiProblems.Create(http,404,"template_missing","Template not found.");
        });

    private static async Task<IResult> Create(string code,string name,IntegrationTemplate definition,
        TemplateOrchestrator engine,ClaimsPrincipal user,HttpContext http,CancellationToken ct) =>
        await Guard(http,async()=>Results.Ok(await engine.CreateDraftAsync(code,name,definition,
            ScopeSecurity.ActorId(user),ct)));

    private static async Task<IResult> Activate(string code,string name,TemplateActivation request,
        TemplateOrchestrator engine,ClaimsPrincipal user,HttpContext http,CancellationToken ct) =>
        await Guard(http,async()=>Results.Ok(await engine.ActivateAsync(code,name,request.Version,
            ScopeSecurity.ActorId(user),ct)));

    private static async Task<IResult> Preview(string code,string name,int version,
        IReadOnlyDictionary<string,JsonElement> body,TemplateOrchestrator engine,
        HttpContext http,CancellationToken ct) =>
        await Guard(http,async()=>Results.Ok(await engine.PreviewAsync(code,name,version,body,ct)));

    private static async Task<IResult> Export(string code,string name,int version,
        ReadAccessRepository access,TemplateRepository repo,HttpContext http,CancellationToken ct) =>
        await Guard(http,async()=>{
            var conn=await access.ConnectionAsync(code,ct);
            var template=await repo.FindAsync(conn.Id,name,ct)
                ?? throw new ReadRejected(404,"template_missing");
            var selected=template.History.SingleOrDefault(r=>r.Version==version)
                ?? throw new ReadRejected(404,"template_version_missing");
            // Only configuration - NEVER includes connection secrets, identities or stored reference GUIDs.
            return Results.Ok(new TemplateExport("dhole.template.v1",name,selected.Definition));
        });

    private static async Task<IResult> Import(string code,TemplateExport input,
        TemplateOrchestrator engine,ClaimsPrincipal actor,HttpContext http,CancellationToken ct) =>
        await Guard(http,async()=>{
            if(input.Format!="dhole.template.v1" || input.Definition is null)
                throw new ReadRejected(400,"unsupported_template_export");
            // Validation, brand new immutable revision, AND remains disabled until separately activated.
            return Results.Ok(await engine.CreateDraftAsync(code,input.Name,input.Definition,
                ScopeSecurity.ActorId(actor),ct));
        });

    private static async Task<IResult> SetReference(string code,ReferenceWrite input,
        ExternalReferenceRepository repository,ReadAccessRepository access,ClaimsPrincipal actor,
        HttpContext http,CancellationToken ct) =>
        await Guard(http,async()=>{
            var connection=await access.ConnectionAsync(code,ct);
            return Results.Ok(await repository.UpsertAsync(connection.Id,input,
                ScopeSecurity.ActorId(actor),ct));
        });

    private static async Task<IResult> GetReference(string code,string space,string entity,
        string internalId,ExternalReferenceRepository repository,ReadAccessRepository access,
        HttpContext http,CancellationToken ct) =>
        await Guard(http,async()=>{
            var connection=await access.ConnectionAsync(code,ct);
            var result=await repository.FindAsync(connection.Id,space,internalId,entity,ct);
            return result is not null?Results.Ok(result):
                ApiProblems.Create(http,404,"reference_missing","Reference not found.");
        });

    private static async Task<IResult> Execute(string name,TemplateInputs request,
        TemplateOrchestrator engine,ClaimsPrincipal actor,HttpContext http,CancellationToken ct) =>
        await Guard(http,async()=>{
            var result=await engine.ExecuteAsync(actor,name,request,
                http.Request.Headers["Idempotency-Key"].ToString(),ct);
            return result is WriteResult write
                ? Results.Json(write,statusCode:write.State=="ambiguous"?409:
                    write.State=="rejected"?write.HttpStatus??502:200)
                : Results.Ok(result);
        });

    private static async Task<IResult> Guard(HttpContext http,Func<Task<IResult>> execute)
    {
        try{return await execute();}
        catch(ReadRejected e){return ApiProblems.Create(http,e.Status,e.Code,"Template request rejected.");}
        catch(UnauthorizedAccessException){return ApiProblems.Create(http,403,"template_denied","Template field denied.");}
        catch(ArgumentException){return ApiProblems.Create(http,400,"invalid_template","Invalid template definition.");}
        catch(HttpRequestException){return ApiProblems.Create(http,502,"dataverse_unavailable","Dataverse is unavailable.");}
        catch(TaskCanceledException){return ApiProblems.Create(http,504,"dataverse_timeout","Dataverse request timed out.");}
        catch(Microsoft.Identity.Client.MsalException)
        {return ApiProblems.Create(http,502,"dataverse_identity","Dataverse identity unavailable.");}
    }
}
