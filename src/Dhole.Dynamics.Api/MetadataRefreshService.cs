using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;

namespace Dhole.Dynamics.Api;

/// <summary>Refreshes a complete catalog or leaves last known good snapshot intact.</summary>
public sealed class MetadataRefreshService(IConnectionRepository connections,
    IMetadataDiscoveryClient discovery, MetadataRepository catalog,
    DataverseMetadataDiscovery detailDiscovery)
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
        return await catalog.SaveAsync(connection.Id, found, source, actor, ct,
            preserveDetails: true);
    }

    // Lightweight refresh for the UPSERT wizard. Keep all existing column
    // selections, lookup relationships and field-grant fingerprints unchanged.
    public async Task<IReadOnlyList<MetadataAlternateKey>> RefreshAlternateKeysAsync(
        string code, string entity, string actor, CancellationToken ct)
    {
        if (!DataverseMetadataDiscovery.SafeName(entity))
            throw new ArgumentException("Invalid Dataverse table.");
        var connection = await connections.FindAsync(code, ct)
            ?? throw new ReadRejected(404,"connection_not_found");
        var current = await catalog.LatestAsync(connection.Id, ct)
            ?? throw new ReadRejected(409,"metadata_missing");
        var table = current.Catalog.Tables.FirstOrDefault(t => t.LogicalName == entity)
            ?? throw new ReadRejected(404,"metadata_entity_missing");
        if (table.Columns.Count == 0)
            throw new ReadRejected(409,"metadata_fields_not_discovered");
        var keys = await detailDiscovery.DiscoverAlternateKeysAsync(
            connection, entity, table.Columns, ct);
        await catalog.SaveTableAsync(connection.Id,
            table with { AlternateKeys = keys }, actor, ct);
        return keys;
    }

    public async Task<MetadataRefreshResult?> RefreshTableAsync(string code, string entity,
        string actor, CancellationToken ct)
    {
        if (!DataverseMetadataDiscovery.SafeName(entity))
            throw new ArgumentException("Invalid Dataverse table.", nameof(entity));
        var connection = await connections.FindAsync(code, ct);
        if (connection is null) return null;
        var current = await catalog.LatestAsync(connection.Id, ct);
        if (current is null || current.Catalog.Tables.All(t => t.LogicalName != entity))
            throw new InvalidOperationException("Refresh table index before discovering fields.");
        var table = await detailDiscovery.DiscoverTableAsync(connection, entity, ct);
        return await catalog.SaveTableAsync(connection.Id, table, actor, ct);
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
