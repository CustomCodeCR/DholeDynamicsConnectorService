using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;

namespace Dhole.Dynamics.Api;

/// <summary>Refreshes a complete catalog or leaves last known good snapshot intact.</summary>
public sealed record AlternateKeyRefreshReport(
    IReadOnlyList<MetadataAlternateKey> AlternateKeys,
    IReadOnlyList<DataverseAlternateKeyState> KeyStatuses);

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
    public async Task<AlternateKeyRefreshReport> RefreshAlternateKeysAsync(
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
        var states = await detailDiscovery.DiscoverAlternateKeyStatesAsync(
            connection, entity, ct);

        // A key can be Active in Dataverse while its newly created column is
        // absent from our persisted metadata snapshot. Refreshing just the
        // keys previously discarded these valid keys forever. Discover the
        // current table only when an Active key references an unknown column,
        // and merge ONLY those columns: never reset existing approvals,
        // relationships, choices or field metadata fingerprints.
        var activeFields = states.Where(s => s.Status == "Active")
            .SelectMany(s => s.Attributes)
            .ToHashSet(StringComparer.Ordinal);
        var missing = activeFields.Where(name =>
            table.Columns.All(c => c.LogicalName != name)).ToHashSet(StringComparer.Ordinal);
        if (missing.Count > 0)
        {
            var discovered = await detailDiscovery.DiscoverTableAsync(connection, entity, ct);
            table = MergeNewActiveKeyColumns(table, discovered, missing);
        }

        var keys = DataverseMetadataDiscovery.ActiveAlternateKeys(states, table.Columns);
        await catalog.SaveTableAsync(connection.Id,
            table with { AlternateKeys = keys }, actor, ct);
        return new AlternateKeyRefreshReport(keys, states);
    }

    /// <summary>
    /// Include metadata for fields backing Active Dataverse keys without
    /// silently granting permissions or changing existing field definitions.
    /// </summary>
    public static MetadataTable MergeNewActiveKeyColumns(MetadataTable current,
        MetadataTable discovered, IReadOnlySet<string> missing)
    {
        if (current.LogicalName != discovered.LogicalName ||
            current.EntitySetName != discovered.EntitySetName ||
            current.PrimaryIdAttribute != discovered.PrimaryIdAttribute)
            throw new InvalidOperationException("Dataverse entity identity changed.");
        var added = discovered.Columns.Where(c => missing.Contains(c.LogicalName) &&
            current.Columns.All(existing => existing.LogicalName != c.LogicalName)).ToArray();
        if (added.Length != missing.Count)
            throw new InvalidOperationException("Active key columns are missing from live Dataverse metadata.");
        return current with { Columns = current.Columns.Concat(added).ToArray() };
    }

    /// <summary>
    /// Read a single choice field live from Dataverse when the existing catalog
    /// snapshot does not contain the required published choice. Never adds a
    /// new Dataverse option or grants permissions by itself.
    /// </summary>
    public async Task<bool> RefreshRequiredSingleChoiceAsync(string code, string entity,
        string field, int requiredValue, string actor, CancellationToken ct)
    {
        if (!DataverseMetadataDiscovery.SafeName(entity) ||
            !DataverseMetadataDiscovery.SafeName(field))
            throw new ArgumentException("Invalid Dataverse choice identifier.");
        var connection = await connections.FindAsync(code, ct)
            ?? throw new ReadRejected(404, "connection_not_found");
        var current = await catalog.LatestAsync(connection.Id, ct)
            ?? throw new ReadRejected(409, "metadata_missing");
        var table = current.Catalog.Tables.SingleOrDefault(t => t.LogicalName == entity)
            ?? throw new ReadRejected(409, "metadata_entity_missing");
        var column = table.Columns.SingleOrDefault(c => c.LogicalName == field);
        if (column is null || column.Type is not ("Picklist" or "Status" or "State"))
            return false;
        var choices = await detailDiscovery.DiscoverChoicesAsync(
            connection, entity, field, column.Type, ct);
        if (!choices.Any(choice => choice.Value == requiredValue))
            return false;
        // A one-field merge avoids deep-refreshing all quote attributes, which
        // would invalidate unrelated approved field fingerprints.
        var updated = MergeSingleChoiceField(table, field, choices);
        await catalog.SaveTableAsync(connection.Id, updated, actor, ct);
        return true;
    }

    public static MetadataTable MergeSingleChoiceField(MetadataTable table,
        string field, IReadOnlyList<MetadataChoice> choices)
    {
        if (table.Columns.Count(column => column.LogicalName == field) != 1)
            throw new InvalidOperationException("Choice metadata field was not found exactly once.");
        return table with
        {
            Columns = table.Columns.Select(column => column.LogicalName == field
                ? column with { Choices = choices }
                : column).ToArray()
        };
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
