using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;
using Dhole.Dynamics.Infrastructure;

namespace Dhole.Dynamics.Api;

public sealed record WritePolicy(DynamicsConnection Connection, MetadataSnapshot Snapshot,
    MetadataTable Table, MetadataPolicies Permissions);

/// <summary>Explicit per-operation authorization with live metadata fingerprints and durable M2M grants.</summary>
public sealed class WriteAccessService(IConnectionRepository connections, MetadataRepository metadata,
    ClientAccessRepository clients)
{
    public async Task<WritePolicy> ResolveAsync(string connectionCode, string entity, CancellationToken ct)
    {
        if (!DataverseMetadataDiscovery.SafeName(entity) || string.IsNullOrWhiteSpace(connectionCode)
            || connectionCode.Length > 64) throw new ReadRejected(400, "invalid_write_target");
        var conn = await connections.FindAsync(connectionCode, ct)
            ?? throw new ReadRejected(403, "connection_denied");
        var snapshot = await metadata.LatestAsync(conn.Id, ct)
            ?? throw new ReadRejected(403, "metadata_missing");
        var table = snapshot.Catalog.Tables.FirstOrDefault(e => e.LogicalName == entity)
            ?? throw new ReadRejected(403, "table_denied");
        var policy = await metadata.PoliciesAsync(conn.Id, ct);
        if (!policy.Entities.Any(e => e.LogicalName == entity && e.Enabled))
            throw new ReadRejected(403, "table_denied");
        return new(conn, snapshot, table, policy);
    }

    public async Task PermitAsync(ClaimsPrincipal user, WritePolicy policy,
        string operation, IReadOnlyCollection<string> fields, CancellationToken ct)
    {
        var actual = operation switch
        {
            "create" or "update" or "upsert" or "delete" => operation,
            _ => throw new ReadRejected(400, "unsupported_write_operation")
        };
        if (!ScopeSecurity.HasScope(user, "dynamics.records." + actual))
            throw new ReadRejected(403, "write_scope_denied");
        if (fields.Count == 0 || fields.Count > 60 || fields.Any(field =>
            !DataverseMetadataDiscovery.SafeName(field) ||
            !policy.Table.Columns.Any(c => c.LogicalName == field) && field != policy.Table.PrimaryIdAttribute))
            throw new ReadRejected(403, "invalid_write_fields");
        if (operation != "delete")
        {
            var needsCreate = operation is "create" or "upsert";
            var needsUpdate = operation is "update" or "upsert";
            foreach (var name in fields)
            {
                var col = policy.Table.Columns.FirstOrDefault(c => c.LogicalName == name)
                    ?? throw new ReadRejected(403, "field_denied");
                var grant = policy.Permissions.Fields.FirstOrDefault(p =>
                    p.EntityName == policy.Table.LogicalName && p.FieldName == name);
                if (grant is null || !grant.Enabled ||
                    grant.MetadataFingerprint != MetadataRepository.Fingerprint(col) ||
                    needsCreate && (!grant.CanCreate || !col.CanCreate) ||
                    needsUpdate && (!grant.CanUpdate || !col.CanUpdate) ||
                    col.IsCalculated)
                    throw new ReadRejected(403, "field_denied");
            }
        }
        var issuer = user.FindFirst("connector:issuer")?.Value;
        var client = ScopeSecurity.ClientId(user);
        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(client))
            throw new ReadRejected(403, "client_denied");
        var decision = await clients.AuthorizeAsync(issuer, client, policy.Connection.Code,
            policy.Table.LogicalName, operation, fields, ct);
        if (decision == GrantDecision.QuotaExceeded) throw new ReadRejected(429, "write_quota_exceeded");
        if (decision != GrantDecision.Allowed) throw new ReadRejected(403, "write_grant_denied");
    }

    public async Task EnsureLookupAsync(ClaimsPrincipal user, WritePolicy source,
        MetadataRelationship relation, Guid targetId, CancellationToken ct)
    {
        if (targetId == Guid.Empty || relation.ReferencedEntity is null)
            throw new ReadRejected(400, "invalid_lookup");
        var target = await ResolveAsync(source.Connection.Code, relation.ReferencedEntity, ct);
        // A relationship target needs a separate read role and DB grant, not just a writable lookup.
        var actorId = user.FindFirst("connector:issuer")?.Value;
        var oauthClient = ScopeSecurity.ClientId(user);
        if (!ScopeSecurity.HasScope(user, "dynamics.records.read") ||
            string.IsNullOrEmpty(actorId) || string.IsNullOrEmpty(oauthClient))
            throw new ReadRejected(403, "lookup_target_denied");
        var identity = target.Table.Columns.FirstOrDefault(c => c.LogicalName == target.Table.PrimaryIdAttribute);
        var permitted = target.Permissions.Fields.FirstOrDefault(f =>
            f.EntityName == target.Table.LogicalName && f.FieldName == target.Table.PrimaryIdAttribute);
        if (identity is null || permitted is null || !permitted.Enabled || !permitted.CanRead ||
            !identity.CanRead || permitted.MetadataFingerprint != MetadataRepository.Fingerprint(identity))
            throw new ReadRejected(403, "lookup_target_fields_denied");
        var result = await clients.AuthorizeAsync(actorId, oauthClient, source.Connection.Code,
            target.Table.LogicalName, "read", [target.Table.PrimaryIdAttribute], ct);
        if (result == GrantDecision.QuotaExceeded) throw new ReadRejected(429, "write_quota_exceeded");
        if (result != GrantDecision.Allowed) throw new ReadRejected(403, "lookup_target_denied");
    }
}

/// <summary>Builds Dataverse JSON from approved typed fields; never accepts raw navigation/property injection.</summary>
public static class WriteValueCompiler
{
    public static async Task<(Dictionary<string, object?> Payload, string[] Fields)> CompileAsync(
        WritePolicy policy, IReadOnlyDictionary<string, JsonElement>? values,
        Func<MetadataRelationship, Guid, CancellationToken, Task> authorizeLookup,
        CancellationToken ct)
    {
        if (values is null || values.Count is < 1 or > 50)
            throw new ReadRejected(400, "invalid_write_values");
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (key.EndsWith("@odata.bind", StringComparison.Ordinal))
            {
                var nav = key[..^"@odata.bind".Length];
                var rel = policy.Table.Relationships.FirstOrDefault(r =>
                    r.Kind == "ManyToOneRelationships" && r.ReferencingNavigation == nav
                    && r.ReferencingEntity == policy.Table.LogicalName);
                if (rel?.ReferencingAttribute is null || rel.ReferencedEntity is null ||
                    !System.Text.RegularExpressions.Regex.IsMatch(nav, @"^[a-zA-Z_][a-zA-Z0-9_]{0,127}$") || value.ValueKind != JsonValueKind.String)
                    throw new ReadRejected(400, "invalid_lookup_binding");
                var targetTable = policy.Snapshot.Catalog.Tables.FirstOrDefault(t => t.LogicalName == rel.ReferencedEntity)
                    ?? throw new ReadRejected(403, "lookup_target_missing");
                if (!Guid.TryParse(value.GetString(), out var relatedId) || relatedId == Guid.Empty)
                    throw new ReadRejected(400, "invalid_lookup_guid");
                await authorizeLookup(rel, relatedId, ct);
                fields.Add(rel.ReferencingAttribute);
                result[key] = "/" + targetTable.EntitySetName + "(" + relatedId.ToString("D") + ")";
                continue;
            }

            if (!DataverseMetadataDiscovery.SafeName(key))
                throw new ReadRejected(400, "invalid_field_name");
            var column = policy.Table.Columns.FirstOrDefault(c => c.LogicalName == key)
                ?? throw new ReadRejected(403, "unknown_write_field");
            // Reserved identity for Dhole's quote UPSERT. Never accept an
            // incident IDTRA, CSV list, random text, or an empty GUID here,
            // regardless of which approved CRUD operation writes the Quote.
            if (policy.Table.LogicalName == "quote" &&
                key == "new_idtraasignado" &&
                (value.ValueKind != JsonValueKind.String ||
                 !Guid.TryParse(value.GetString(), out var dholeRateId) ||
                 dholeRateId == Guid.Empty ||
                 value.GetString()!.Length != 36))
                throw new ReadRejected(400, "quote_rate_guid_required");
            if (column.IsCalculated || column.Type is "Virtual" or "PartyList" or "ManagedProperty")
                throw new ReadRejected(403, "unsupported_write_field");
            if (column.Type is "Lookup" or "Customer" or "Owner")
                throw new ReadRejected(400, "lookup_requires_odata_bind");
            fields.Add(key);
            result[key] = Value(column, value);
        }
        if (result.Count != values.Count || fields.Count == 0)
            throw new ReadRejected(400, "duplicate_write_field");
        return (result, fields.Order(StringComparer.Ordinal).ToArray());
    }

    private static object? Value(MetadataColumn col, JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Null) return null;
        switch (col.Type)
        {
            case "String": case "Memo": case "EntityName":
                if (v.ValueKind != JsonValueKind.String || v.GetString()?.Length > 4000)
                    throw new ReadRejected(400, "invalid_string");
                return v.GetString();
            case "Boolean":
                if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ReadRejected(400, "invalid_boolean");
                return v.GetBoolean();
            case "Integer": case "Picklist": case "State": case "Status":
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var number))
                    throw new ReadRejected(400, "invalid_choice_integer");
                if (col.Choices.Count > 0 && col.Choices.All(c => c.Value != number))
                    throw new ReadRejected(400, "unknown_choice");
                return number;
            case "BigInt":
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var large))
                    throw new ReadRejected(400, "invalid_integer");
                return large;
            case "Decimal": case "Double": case "Money":
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var decimalValue))
                    throw new ReadRejected(400, "invalid_decimal");
                if (col.Precision is int precision && precision >= 0 &&
                    GetScale(decimalValue) > precision)
                    throw new ReadRejected(400, "decimal_precision_exceeded");
                return decimalValue;
            case "DateTime":
                if (v.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var date) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(v.GetString()!,
                        @"(?:Z|[+-][0-9]{2}:[0-9]{2})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    throw new ReadRejected(400, "invalid_timestamp");
                return date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            case "Uniqueidentifier":
                if (v.ValueKind != JsonValueKind.String || !Guid.TryParse(v.GetString(), out var guid))
                    throw new ReadRejected(400, "invalid_guid");
                return guid.ToString("D");
            case "MultiSelectPicklist":
                if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 1 or > 20)
                    throw new ReadRejected(400, "invalid_multiselect");
                var list = new List<int>();
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var selected) ||
                        col.Choices.Count > 0 && col.Choices.All(c => c.Value != selected) ||
                        list.Contains(selected))
                        throw new ReadRejected(400, "invalid_multiselect_option");
                    list.Add(selected);
                }
                return string.Join(",", list);
            default:
                throw new ReadRejected(400, "unsupported_dataverse_type");
        }
    }

    private static int GetScale(decimal value) =>
        (decimal.GetBits(value)[3] >> 16) & 0xFF;
}
