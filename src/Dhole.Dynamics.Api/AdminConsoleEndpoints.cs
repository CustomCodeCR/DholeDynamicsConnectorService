using System.Security.Claims;
using System.Text.Json;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace Dhole.Dynamics.Api;

/// <summary>Cookie OIDC administration only. Machine endpoints remain separately JWT protected.</summary>
public static class AdminConsoleEndpoints
{
    public sealed record AdminRecordWrite(string ConnectionCode, IReadOnlyDictionary<string,JsonElement>? Values,
        bool Confirm = false, string? Reason = null);

    public static IEndpointRouteBuilder MapAdminConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        var admin=app.MapGroup("/api/admin/v1/console").WithGroupName("admin-v1")
            .RequireAuthorization("Admin").RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        admin.MapGet("/overview",Overview);
        admin.MapGet("/jobs",Jobs);
        admin.MapGet("/audit",Audit);
        admin.MapGet("/observations",Observations);
        admin.MapGet("/schedules",Schedules);
        admin.MapGet("/diagnostics",Diagnostics);

        var privileged=admin.MapGroup("").RequireAuthorization("SecurityAdmin");
        privileged.MapPost("/records/{entity}/search",Search);
        privileged.MapGet("/records/{entity}/{id:guid}",Detail);
        privileged.MapPost("/records/{entity}",Create);
        privileged.MapPatch("/records/{entity}/{id:guid}",Patch);
        privileged.MapDelete("/records/{entity}/{id:guid}",Delete);
        return app;
    }

    private static Task<IResult> Overview(AdminConsoleRepository repository,HttpContext ctx,CancellationToken ct) =>
        Guard(ctx,async()=>Results.Ok(await repository.OverviewAsync(ct)));
    private static Task<IResult> Jobs(string? connectionCode,int? limit,
        AdminConsoleRepository repository,IConnectionRepository connections,HttpContext ctx,CancellationToken ct) =>
        Guard(ctx,async()=>Results.Ok(await repository.JobsAsync(
            await ConnectionId(connectionCode,connections,ct),limit??50,ct)));
    private static Task<IResult> Audit(string? connectionCode,int? limit,
        AdminConsoleRepository repository,IConnectionRepository connections,HttpContext ctx,CancellationToken ct) =>
        Guard(ctx,async()=>Results.Ok(await repository.AuditAsync(
            await ConnectionId(connectionCode,connections,ct),limit??50,ct)));
    private static Task<IResult> Observations(int? limit,
        AdminConsoleRepository repository,HttpContext ctx,CancellationToken ct) =>
        Guard(ctx,async()=>Results.Ok(await repository.ObservationsAsync(limit??50,ct)));
    private static Task<IResult> Schedules(string? connectionCode,
        AdminConsoleRepository repository,IConnectionRepository connections,HttpContext ctx,CancellationToken ct) =>
        Guard(ctx,async()=>Results.Ok(await repository.SchedulesAsync(
            await ConnectionId(connectionCode,connections,ct),ct)));
    private static async Task<Guid?> ConnectionId(string? code,IConnectionRepository connections,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(code))return null;
        var selected=await connections.FindAsync(code,ct)??throw new ReadRejected(404,"connection_not_found");
        return selected.Id;
    }
    private static IResult Diagnostics(IConfiguration config,IHostEnvironment env) =>
        Results.Ok(new {
            environment=env.EnvironmentName,
            m2mConfigured=!string.IsNullOrWhiteSpace(config["M2m:Authority"]),
            oidcConfigured=!string.IsNullOrWhiteSpace(config["AdminOidc:Authority"]),
            externalOrigin=config["Admin:PublicOrigin"],
            metadataSchedulerEnabled=config.GetValue<bool>("Metadata:SchedulerEnabled"),
            backupStatus="not-tracked",
            secretsExposed=false
        });

    private static Task<IResult> Search(string entity,ReadSearchRequest request,
        ReadSearchService service,ClaimsPrincipal actor,HttpContext http,CancellationToken ct) =>
        Guard(http,async()=>Results.Ok(await service.SearchAsync(actor,entity,request,ct,admin:true)));
    private static Task<IResult> Detail(string entity,Guid id,string connectionCode,string select,
        ReadSearchService service,ClaimsPrincipal actor,HttpContext http,CancellationToken ct) =>
        Guard(http,async()=>Results.Ok(await service.ByIdAsync(actor,connectionCode,entity,id,
            select.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries),ct,admin:true)));
    private static Task<IResult> Create(string entity,AdminRecordWrite input,AdminRecordService service,
        ClaimsPrincipal actor,HttpContext http,CancellationToken ct) =>
        Guard(http,async()=>Respond(http,await service.ExecuteAsync(actor,input.ConnectionCode,
            entity,"create",null,input.Values,null,false,null,Key(http),ct)));
    private static Task<IResult> Patch(string entity,Guid id,AdminRecordWrite input,AdminRecordService service,
        ClaimsPrincipal actor,HttpContext http,CancellationToken ct) =>
        Guard(http,async()=>Respond(http,await service.ExecuteAsync(actor,input.ConnectionCode,
            entity,"update",id,input.Values,http.Request.Headers.IfMatch.ToString(),false,null,Key(http),ct)));
    private static Task<IResult> Delete(string entity,Guid id,[FromBody] AdminRecordWrite input,AdminRecordService service,
        ClaimsPrincipal actor,HttpContext http,CancellationToken ct) =>
        Guard(http,async()=>Respond(http,await service.ExecuteAsync(actor,input.ConnectionCode,
            entity,"delete",id,null,http.Request.Headers.IfMatch.ToString(),
            input.Confirm,input.Reason,Key(http),ct)));
    private static string Key(HttpContext http)
    {
        var key=http.Request.Headers["Idempotency-Key"].ToString();
        if(!WriteLedger.ValidKey(key))throw new ReadRejected(400,"idempotency_key_required");
        return key;
    }
    private static IResult Respond(HttpContext http,WriteResult result)
    {
        if(result.RetryAfterSeconds is int seconds)
            http.Response.Headers.RetryAfter=seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Json(result,statusCode:result.State=="succeeded"?(result.HttpStatus==201?201:200)
            :result.State=="ambiguous"?409
            :result.HttpStatus is >= 400 and <= 499?result.HttpStatus.Value:502);
    }
    private static async Task<IResult> Guard(HttpContext http,Func<Task<IResult>> action)
    {
        try{return await action();}
        catch(ReadRejected x){return ApiProblems.Create(http,x.Status,x.Code,"Operation not permitted.");}
        catch(UnauthorizedAccessException){return ApiProblems.Create(http,403,"field_denied","Field not enabled.");}
        catch(ArgumentException){return ApiProblems.Create(http,400,"invalid_console_request","Invalid request.");}
        catch(HttpRequestException){return ApiProblems.Create(http,502,"dataverse_unavailable","Dataverse unavailable.");}
        catch(TaskCanceledException){return ApiProblems.Create(http,504,"dataverse_timeout","Dataverse timeout.");}
        catch(Microsoft.Identity.Client.MsalException)
        {return ApiProblems.Create(http,502,"dataverse_identity","Dataverse identity unavailable.");}
    }
}
