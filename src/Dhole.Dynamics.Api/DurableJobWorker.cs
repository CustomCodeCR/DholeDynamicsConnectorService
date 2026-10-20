using Dhole.Dynamics.Workers;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Microsoft.Identity.Client;
using Npgsql;
using System.Text.Json;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Opt-in PostgreSQL worker. It is safe to run multiple instances; leases and fenced commits
/// prevent two workers from persisting one page. No unsafe writes are retried.
/// </summary>
public sealed class DurableJobWorker(IServiceScopeFactory scopes,IConfiguration config,
    ILogger<DurableJobWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!config.GetValue("Jobs:Enabled",false))return;
        var worker=Guid.NewGuid();
        var metadataOnly=config.GetValue("Jobs:MetadataOnly",false);
        var lastMaintenance=DateTimeOffset.MinValue;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();
                var queue=scope.ServiceProvider.GetRequiredService<DurableJobRepository>();
                await queue.ScheduleDueAsync(ct,metadataOnly);
                if(DateTimeOffset.UtcNow-lastMaintenance>TimeSpan.FromHours(1))
                {
                    await queue.PurgeAsync(ct);
                    lastMaintenance=DateTimeOffset.UtcNow;
                }
                var claim=await queue.ClaimAsync(worker,ct,metadataOnly);
                if(claim is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3),ct);
                    continue;
                }
                using var operation=CancellationTokenSource.CreateLinkedTokenSource(ct);
                var keepAlive=KeepAliveAsync(queue,claim,operation,ct);
                try{await ProcessAsync(scope.ServiceProvider,queue,claim,operation.Token);}
                finally
                {
                    operation.Cancel();
                    await keepAlive;
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                // Never include provider messages, request payloads, tokens, full URL or SQL query.
                log.LogWarning("Job worker cycle failed: {Kind}",ex.GetType().Name);
                try{await Task.Delay(TimeSpan.FromSeconds(5),ct);}
                catch(OperationCanceledException){break;}
            }
        }
    }

    private static async Task KeepAliveAsync(DurableJobRepository jobs,QueueJob claim,
        CancellationTokenSource operation,CancellationToken stop)
    {
        try
        {
            while(!operation.IsCancellationRequested && !stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(25),operation.Token);
                if(!await jobs.RenewAsync(claim,operation.Token))
                {
                    operation.Cancel();
                    break;
                }
            }
        }
        catch(OperationCanceledException){}
    }

    private async Task ProcessAsync(IServiceProvider services,DurableJobRepository queue,
        QueueJob job,CancellationToken ct)
    {
        try
        {
            switch(job.Kind)
            {
                case "delta_sync":
                    await SyncAsync(services,queue,job,ct);
                    return;
                case "metadata_refresh":
                    var connection=await queue.ConnectionCodeAsync(job.ConnectionId,ct);
                    var refreshService=services.GetRequiredService<MetadataRefreshService>();
                    var refreshed=string.IsNullOrEmpty(job.ResourceKey)
                        ? await refreshService.RefreshAsync(connection,"scheduled","system:durable-job",ct)
                        : await refreshService.RefreshTableAsync(connection,job.ResourceKey,"system:durable-job",ct);
                    if(refreshed is null)throw new ReadRejected(404,"connection_not_found");
                    break;
                case "legacy_preview":
                    var previewCode=await queue.ConnectionCodeAsync(job.ConnectionId,ct);
                    await services.GetRequiredService<LegacyMigrationService>()
                        .PreviewAsync(previewCode,ct);
                    break;
                case "legacy_import":
                    var importCode=await queue.ConnectionCodeAsync(job.ConnectionId,ct);
                    // Import is config-only, staged disabled and idempotent by audited Phase 09 contract.
                    await services.GetRequiredService<LegacyMigrationService>()
                        .ImportAsync(importCode,true,"system:durable-import",ct);
                    break;
                case "reconcile_scan":
                    // Alert only. NEVER repeat an ambiguous Dataverse write or mark it reconciled.
                    await queue.ReconciliationScanAsync(job,ct);
                    break;
                default:throw new ReadRejected(400,"unsupported_job_kind");
            }
            await queue.FinishAsync(job,ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(ReadRejected ex) when(ex.Code=="job_lease_lost"){return;}
        catch(Exception ex)
        {
            var (transient,code,after)=Classify(ex);
            try{await queue.FailAsync(job,transient,code,after,CancellationToken.None);}
            catch(ReadRejected e) when(e.Code=="job_lease_lost"){}
            log.LogWarning("Durable job failed kind={Kind} code={Code} attempts={Attempts}",
                job.Kind,code,job.Attempts);
        }
    }

    private async Task SyncAsync(IServiceProvider services,DurableJobRepository jobs,
        QueueJob job,CancellationToken ct)
    {
        var settings=await jobs.GetConfigAsync(job.ConnectionId,job.ResourceKey,ct);
        if(settings is not {Enabled:true})
            throw new ReadRejected(403,"sync_not_enabled");
        var code=await jobs.ConnectionCodeAsync(job.ConnectionId,ct);
        var policy=await services.GetRequiredService<ReadAccessRepository>()
            .ResolveAsync(code,job.ResourceKey,ct);
        var (storedMode,next,delta,watermark,started)=
            await jobs.CursorAsync(job.ConnectionId,job.ResourceKey,ct);
        var mode=storedMode;
        if(mode=="watermark" && !policy.Allowed.Contains("modifiedon") ||
            !policy.Allowed.Contains(policy.Table.PrimaryIdAttribute))
            throw new ReadRejected(403,"sync_fields_denied");
        var now=started??DateTimeOffset.UtcNow.AddMinutes(-1);
        var client=services.GetRequiredService<DataverseDeltaReader>();
        SyncPage page;
        try{page=await client.ReadAsync(policy,mode,next,delta,watermark,ct);}
        catch(SyncRequestFailure ex) when(mode=="delta" && settings.FallbackToWatermark &&
            next is null && (ex.StatusCode is 400 or 501))
        {
            if(!policy.Allowed.Contains("modifiedon"))
                throw new ReadRejected(403,"watermark_fallback_field_denied");
            await jobs.UseWatermarkAsync(job,ct);
            mode="watermark";
            (storedMode,next,delta,watermark,started)=
                await jobs.CursorAsync(job.ConnectionId,job.ResourceKey,ct);
            now=DateTimeOffset.UtcNow.AddMinutes(-1);
            page=await client.ReadAsync(policy,mode,null,null,watermark,ct);
        }
        if(page.UnsupportedDelta)
        {
            if(mode!="delta" || !settings.FallbackToWatermark || next is not null)
                throw new ReadRejected(502,"delta_tracking_not_supported");
            if(!policy.Allowed.Contains("modifiedon"))
                throw new ReadRejected(403,"watermark_fallback_field_denied");
            await jobs.UseWatermarkAsync(job,ct);
            mode="watermark";
            (storedMode,next,delta,watermark,started)=
                await jobs.CursorAsync(job.ConnectionId,job.ResourceKey,ct);
            now=DateTimeOffset.UtcNow.AddMinutes(-1);
            page=await client.ReadAsync(policy,mode,null,null,watermark,ct);
        }
        await jobs.SavePageAsync(job,mode,page,now,ct);
    }

    public static (bool Retry,string Code,int? After) Classify(Exception ex) => ex switch
    {
        SyncRequestFailure x when JobRetryPolicy.Retryable(x.StatusCode) =>
            (true,"dataverse_transient",x.RetrySeconds),
        SyncRequestFailure => (false,"dataverse_request_denied",null),
        HttpRequestException http when http.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden => (false,"dataverse_permission_denied",null),
        HttpRequestException http when http.StatusCode.HasValue =>
            (JobRetryPolicy.Retryable((int)http.StatusCode.Value),
                JobRetryPolicy.Retryable((int)http.StatusCode.Value) ? "dataverse_transient" : "dataverse_request_denied",null),
        HttpRequestException => (true,"network_transient",null),
        TaskCanceledException => (true,"upstream_timeout",null),
        MsalException => (true,"token_acquisition_unavailable",null),
        NpgsqlException => (true,"database_unavailable",null),
        ReadRejected rejected when rejected.Code=="job_lease_lost" =>
            (false,"job_lease_lost",null),
        ReadRejected => (false,"job_policy_denied",null),
        JsonException => (false,"invalid_provider_page",null),
        _ => (false,"job_failed",null)
    };
}
