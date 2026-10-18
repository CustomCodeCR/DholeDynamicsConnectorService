using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;

namespace Dhole.Dynamics.Api;

/// <summary>Refreshes a complete catalog or leaves last known good snapshot intact.</summary>
public sealed class MetadataRefreshService(IConnectionRepository connections,
    IMetadataDiscoveryClient discovery, MetadataRepository catalog)
{
    public async Task<MetadataRefreshResult?> RefreshAsync(string code, string source,
        string actor, CancellationToken ct)
    {
        if (source is not ("manual" or "scheduled"))
            throw new ArgumentException("Unrecognized refresh source.", nameof(source));
        var connection = await connections.FindAsync(code, ct);
        if (connection is null) return null;
        var found = await discovery.DiscoverAsync(connection, ct);
        if (found.Tables.Count == 0)
            throw new InvalidOperationException("Empty Dataverse catalog; previous snapshot preserved.");
        if (found.Tables.Select(t => t.LogicalName).Distinct(StringComparer.Ordinal).Count() != found.Tables.Count)
            throw new InvalidOperationException("Duplicate Dataverse entity definitions.");
        return await catalog.SaveAsync(connection.Id, found, source, actor, ct);
    }
}

public sealed class MetadataScheduler(IServiceScopeFactory scopeFactory,
    IConfiguration configuration, ILogger<MetadataScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // When the durable queue is enabled, its scheduler owns the same metadata
        // schedule rows, preventing duplicate and unleased provider calls.
        if (configuration.GetValue("Jobs:Enabled", false) ||
            !configuration.GetValue("Metadata:SchedulerEnabled", false)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var catalog = scope.ServiceProvider.GetRequiredService<MetadataRepository>();
                var service = scope.ServiceProvider.GetRequiredService<MetadataRefreshService>();
                foreach (var code in await catalog.ClaimDueAsync(stoppingToken))
                    try { await service.RefreshAsync(code, "scheduled", "system:metadata-scheduler", stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // Exceptions may contain provider details; never log secret or token values.
                        logger.LogWarning("Scheduled metadata refresh failed for code {Code}, error type {Error}",
                            code, ex.GetType().Name);
                    }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Metadata scheduler polling failed ({Type}).", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
