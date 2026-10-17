using System.Security.Claims;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;

namespace Dhole.Dynamics.Api;

public sealed class ReadRejected(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public sealed record ReadPolicy(DynamicsConnection Connection, MetadataSnapshot Snapshot,
    MetadataTable Table, IReadOnlySet<string> Allowed);

/// <summary>Unifies metadata allowlists and signed client-scoped DB grants for each request/page.</summary>
public sealed class ReadAccessRepository(IConnectionRepository connections,
    MetadataRepository metadata, ClientAccessRepository clients)
{
    public async Task<ReadPolicy> ResolveAsync(string connectionCode, string entity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionCode) || connectionCode.Length > 64
            || !Dhole.Dynamics.Infrastructure.DataverseMetadataDiscovery.SafeName(entity))
            throw new ReadRejected(400, "invalid_query");
        var conn = await connections.FindAsync(connectionCode, ct)
            ?? throw new ReadRejected(403, "connection_denied");
        var current = await metadata.LatestAsync(conn.Id, ct)
            ?? throw new ReadRejected(403, "metadata_missing");
        var table = current.Catalog.Tables.FirstOrDefault(t => t.LogicalName == entity)
            ?? throw new ReadRejected(403, "entity_denied");
        var policies = await metadata.PoliciesAsync(conn.Id, ct);
        if (!policies.Entities.Any(p => p.LogicalName == entity && p.Enabled))
            throw new ReadRejected(403, "entity_denied");
        var approved = policies.Fields
            .Where(p => p.EntityName == entity && p.Enabled && p.CanRead)
            .Where(p => table.Columns.Any(c => c.LogicalName == p.FieldName && c.CanRead
                && p.MetadataFingerprint == MetadataRepository.Fingerprint(c)))
            .Select(p => p.FieldName).ToHashSet(StringComparer.Ordinal);
        if (approved.Count == 0) throw new ReadRejected(403, "fields_denied");
        return new(conn, current, table, approved);
    }

    public async Task PermitAsync(ClaimsPrincipal user, ReadPolicy policy,
        IReadOnlyCollection<string> requiredFields, CancellationToken ct)
    {
        if (!ScopeSecurity.HasScope(user, "dynamics.records.read"))
            throw new ReadRejected(403, "scope_denied");
        if (requiredFields.Count == 0 || requiredFields.Any(f => !policy.Allowed.Contains(f)))
            throw new ReadRejected(403, "fields_denied");
        var issuer = user.FindFirst("connector:issuer")?.Value;
        var client = ScopeSecurity.ClientId(user);
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(client))
            throw new ReadRejected(403, "client_denied");
        var result = await clients.AuthorizeAsync(issuer, client, policy.Connection.Code,
            policy.Table.LogicalName, "read", requiredFields, ct);
        switch (result)
        {
            case GrantDecision.Allowed: return;
            case GrantDecision.QuotaExceeded: throw new ReadRejected(429, "client_quota_exceeded");
            default: throw new ReadRejected(403, "client_denied");
        }
    }
}
