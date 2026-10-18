using System.Security.Claims;
using Dhole.Dynamics.Contracts;
using Microsoft.AspNetCore.RateLimiting;

namespace Dhole.Dynamics.Api;

/// <summary>Administrative control plane only. No browser-provided URL, script or serialized job commands.</summary>
public static class DurableJobEndpoints
{
    public static IEndpointRouteBuilder MapDurableJobEndpoints(this IEndpointRouteBuilder app)
    {
        var read=app.MapGroup("/api/admin/v1/operations")
            .RequireAuthorization("Admin").RequireRateLimiting("AdminRate")
            .WithGroupName("admin-v1");
        read.MapGet("/jobs",Jobs);
        read.MapGet("/jobs/{id:guid}",JobDetail);
        read.MapGet("/sync/{code}",SyncConfigs);
        read.MapGet("/sync/{code}/checkpoints",Checkpoints);
        read.MapGet("/stats",Stats);
        read.MapGet("/alerts",Alerts);

        var write=app.MapGroup("/api/admin/v1/operations")
            .RequireAuthorization("SecurityAdmin").RequireRateLimiting("AdminRate")
            .RequireCsrfForWrites().WithGroupName("admin-v1");
        write.MapPost("/jobs/{code}",Enqueue);
        write.MapPost("/jobs/{code}/{id:guid}/replay",Replay);
        write.MapPut("/sync/{code}",ConfigureSync);
        write.MapPut("/retention",SetRetention);
        write.MapPost("/alerts/{id:guid}/ack",Ack);
        read.MapGet("/retention",Retention);
        read.MapGet("/audit/export",ExportAudit).RequireAuthorization("SecurityAdmin");

        app.MapGet("/health/jobs",async (DurableJobRepository queue,IConfiguration settings,CancellationToken ct)=>
        {
            try
            {
                var stats=await queue.StatsAsync(ct);
                return Results.Ok(new {status=settings.GetValue("Jobs:Enabled",false)?"operational":"disabled",
                    stats.Pending,stats.Running,stats.Dead,stats.StaleLeases,
                    stats.UnacknowledgedAlerts});
            }
            catch(Exception){return Results.StatusCode(503);}
        }).AllowAnonymous();
        return app;
    }

    private static async Task<Guid> Connection(string code,
        ReadAccessRepository access,CancellationToken ct) =>
        (await access.ConnectionAsync(code,ct)).Id;

    private static Task<IResult> Jobs(string? connectionCode,int? limit,
        ReadAccessRepository access,DurableJobRepository jobs,HttpContext http,CancellationToken ct) =>
        Guard(http,async()=>Results.Ok(await jobs.ListAsync(
            string.IsNullOrWhiteSpace(connectionCode)?null:await Connection(connectionCode,access,ct),
            limit??50,ct)));

    private static Task<IResult> JobDetail(string code,Guid id,
        ReadAccessRepository access,DurableJobRepository jobs,HttpContext http,CancellationToken ct)=>
        Guard(http,async()=>await jobs.FindAsync(await Connection(code,access,ct),id,ct) is {} item
            ? Results.Ok(item)
            : ApiProblems.Create(http,404,"job_not_found","Job not found."));

    private static Task<IResult> Enqueue(string code,JobEnqueueRequest request,
        ReadAccessRepository access,DurableJobRepository jobs,ClaimsPrincipal actor,
        HttpContext http,CancellationToken ct) =>
        Guard(http,async()=>
        {
            if(request.Kind=="legacy_import" && !request.Confirm)
                throw new ReadRejected(400,"legacy_import_confirmation_required");
            var id=await Connection(code,access,ct);
            var result=await jobs.EnqueueAsync(id,request,ScopeSecurity.ActorId(actor),ct);
            return Results.Accepted("/api/admin/v1/operations/jobs/"+result!.Id,result);
        });

    private static Task<IResult> Replay(string code,Guid id,JobReplayRequest request,
        ReadAccessRepository access,DurableJobRepository jobs,ClaimsPrincipal actor,
        HttpContext http,CancellationToken ct)=>
        Guard(http,async()=>
        {
            if(!request.Confirm)throw new ReadRejected(400,"job_replay_confirmation_required");
            return Results.Ok(await jobs.ReplayAsync(await Connection(code,access,ct),id,
                ScopeSecurity.ActorId(actor),request.Reason,ct));
        });

    private static Task<IResult> SyncConfigs(string code,ReadAccessRepository access,
        DurableJobRepository queue,HttpContext http,CancellationToken ct)=>
        Guard(http,async()=>Results.Ok(await queue.ConfigurationsAsync(
            await Connection(code,access,ct),ct)));

    private static Task<IResult> ConfigureSync(string code,SyncSettings input,
        ReadAccessRepository access,DurableJobRepository queue,ClaimsPrincipal actor,
        HttpContext http,CancellationToken ct) =>
        Guard(http,async()=>
        {
            var scope=await access.ResolveAsync(code,input.EntityName,ct);
            if(!scope.Allowed.Contains(scope.Table.PrimaryIdAttribute) ||
                (input.Mode=="watermark" || input.FallbackToWatermark) &&
                !scope.Allowed.Contains("modifiedon"))
                throw new ReadRejected(403,"sync_fields_denied");
            await queue.SetConfigAsync(scope.Connection.Id,input,ScopeSecurity.ActorId(actor),ct);
            return Results.NoContent();
        });

    private static Task<IResult> Checkpoints(string code,ReadAccessRepository access,
        DurableJobRepository queue,HttpContext http,CancellationToken ct)=>
        Guard(http,async()=>Results.Ok(await queue.CheckpointsAsync(
            await Connection(code,access,ct),ct)));

    private static Task<IResult> Stats(DurableJobRepository queue,HttpContext http,CancellationToken ct)=>
        Guard(http,async()=>Results.Ok(await queue.StatsAsync(ct)));

    private static Task<IResult> Alerts(string? connectionCode,ReadAccessRepository access,
        DurableJobRepository queue,HttpContext http,CancellationToken ct)=>
        Guard(http,async()=>Results.Ok(await queue.AlertsAsync(
            string.IsNullOrWhiteSpace(connectionCode)?null:await Connection(connectionCode,access,ct),ct)));

    private static Task<IResult> Ack(Guid id,DurableJobRepository queue,
        ClaimsPrincipal actor,HttpContext http,CancellationToken ct)=>
        Guard(http,async()=>
        {
            await queue.AckAsync(id,ScopeSecurity.ActorId(actor),ct);
            return Results.NoContent();
        });

    private static Task<IResult> Retention(DurableJobRepository queue,HttpContext http,CancellationToken ct)=>
        Guard(http,async()=>Results.Ok(await queue.RetentionAsync(ct)));

    private static Task<IResult> SetRetention(RetentionPolicy input,DurableJobRepository queue,
        ClaimsPrincipal actor,HttpContext http,CancellationToken ct)=>
        Guard(http,async()=>
        {
            await queue.SetRetentionAsync(input,ScopeSecurity.ActorId(actor),ct);
            return Results.NoContent();
        });

    private static Task<IResult> ExportAudit(string? connectionCode,ReadAccessRepository access,
        DurableJobRepository queue,HttpContext http,CancellationToken ct)=>
        Guard(http,async()=>
        {
            var id=string.IsNullOrWhiteSpace(connectionCode)?null:
                (Guid?)await Connection(connectionCode,access,ct);
            var data=await queue.ExportAuditAsync(id,ct);
            return Results.File(data,"text/csv","connector-audit-sanitized.csv");
        });

    private static async Task<IResult> Guard(HttpContext http,Func<Task<IResult>> execute)
    {
        try{return await execute();}
        catch(ReadRejected ex){return ApiProblems.Create(http,ex.Status,ex.Code,"Operation denied.");}
        catch(ArgumentException){return ApiProblems.Create(http,400,"invalid_job_request","Invalid job request.");}
    }
}
