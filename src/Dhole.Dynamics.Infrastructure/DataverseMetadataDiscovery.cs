using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;

namespace Dhole.Dynamics.Infrastructure;

/// <summary>
/// Dataverse v9.2 metadata reader. Fixed resource paths and pinned HTTPS host; not an HTTP proxy.
/// No source metadata is trusted to create a request URL without a strict logical-name check.
/// </summary>
public sealed class DataverseMetadataDiscovery(HttpClient http, IDataverseAccessTokenProvider tokens)
    : IMetadataDiscoveryClient
{
    private const string ApiPath = "/api/data/v9.2/EntityDefinitions";
    private const int MaxEntities = 8000;
    private const int MaxColumns = 2000;
    private const int MaxPages = 100;

    // Fast index only. Expensive field/choice/relationship discovery is performed on demand
    // for one selected table so a large Dataverse tenant cannot repeatedly time out.
    public async Task<MetadataCatalog> DiscoverAsync(DynamicsConnection connection, CancellationToken ct)
    {
        if (!DataverseUrlValidator.TryNormalize(connection.DataverseUrl, out var host))
            throw new InvalidOperationException("Untrusted Dataverse host.");
        var token = await tokens.AcquireAsync(connection, ct);
        var list = await CollectionAsync(host, token, ApiPath +
            "?$select=LogicalName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute,IsCustomEntity,DisplayName", ct);
        if (list.Count > MaxEntities)
            throw new InvalidOperationException("Dataverse metadata entity limit exceeded.");
        var tables = new List<MetadataTable>();
        foreach (var table in list)
        {
            var name = Str(table, "LogicalName");
            var set = Str(table, "EntitySetName");
            var primary = Str(table, "PrimaryIdAttribute");
            if (!SafeName(name) || !SafeName(set) || !SafeName(primary)) continue;
            tables.Add(new MetadataTable(name!, set!, primary!, Str(table, "PrimaryNameAttribute"),
                Flag(table, "IsCustomEntity"), Labels(table, "DisplayName"), [], [], []));
        }
        tables.Sort((a, b) => StringComparer.Ordinal.Compare(a.LogicalName, b.LogicalName));
        return new MetadataCatalog(tables);
    }

    // Default scan needs only the entity and Attributes collection: no N+1 type-cast requests.
    // Extended option/relationship metadata is opt-in because large quote tables can
    // exceed worker time budgets when every attribute makes a separate Dataverse request.
    // One-table deep discovery, restricted to a logical name previously discovered in
    // the immutable catalog. No external URL, arbitrary OData expression or wildcard.
    public async Task<MetadataTable> DiscoverTableAsync(DynamicsConnection connection,
        string entity, CancellationToken ct, bool includeExtended = false)
    {
        if (!SafeName(entity))
            throw new ArgumentException("Invalid Dataverse table name.", nameof(entity));
        if (!DataverseUrlValidator.TryNormalize(connection.DataverseUrl, out var host))
            throw new InvalidOperationException("Untrusted Dataverse host.");
        var token = await tokens.AcquireAsync(connection, ct);
        using var response = await GetAsync(host, token,
            ApiPath + "(LogicalName='" + entity +
            "')?$select=LogicalName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute,IsCustomEntity,DisplayName", ct);
        return await BuildTableAsync(host, token, response.RootElement, ct, includeExtended);
    }

    private async Task<MetadataTable> BuildTableAsync(string host, string token,
        JsonElement table, CancellationToken ct, bool includeExtended)
    {
            var name = Str(table, "LogicalName");
            var set = Str(table, "EntitySetName");
            var primary = Str(table, "PrimaryIdAttribute");
            if (!SafeName(name) || !SafeName(set) || !SafeName(primary))
                throw new InvalidOperationException("Entity is not accessible to Dataverse metadata discovery.");
            var basePath = ApiPath + "(LogicalName='" + name + "')";
            var attributes = await CollectionAsync(host, token, basePath + "/Attributes" +
                "?$select=LogicalName,AttributeType,IsValidForRead,IsValidForCreate,IsValidForUpdate,IsLogical,SourceType,RequiredLevel,DisplayName", ct);
            if (attributes.Count > MaxColumns) throw new InvalidOperationException("Metadata field limit exceeded.");
            var columns = new List<MetadataColumn>();
            foreach (var item in attributes)
            {
                var fieldName = Str(item, "LogicalName");
                if (!SafeName(fieldName)) continue;
                var type = Str(item, "AttributeType") ?? "Unknown";
                var logical = Flag(item, "IsLogical");
                var calculated = logical || (Int(item, "SourceType") is int kind && kind != 0);
                var opts = new List<MetadataChoice>();
                int? precision = null;
                int? currencyPrecisionSource = null;
                var targets = new List<string>();
                string? defaultValue = null;
                var kindName = type switch
                {
                    "Picklist" => "PicklistAttributeMetadata",
                    "MultiSelectPicklist" => "MultiSelectPicklistAttributeMetadata",
                    "State" => "StateAttributeMetadata",
                    "Status" => "StatusAttributeMetadata",
                    "Boolean" => "BooleanAttributeMetadata",
                    "Decimal" => "DecimalAttributeMetadata",
                    "Double" => "DoubleAttributeMetadata",
                    "Money" => "MoneyAttributeMetadata",
                    "Integer" => "IntegerAttributeMetadata",
                    "String" => "StringAttributeMetadata",
                    "DateTime" => "DateTimeAttributeMetadata",
                    "Lookup" => "LookupAttributeMetadata",
                    "Customer" => "CustomerAttributeMetadata",
                    "Owner" => "OwnerAttributeMetadata",
                    _ => null
                };
                if (includeExtended && kindName is not null)
                {
                    // Derived types expose properties absent from AttributeMetadata.
                    var query = type is "Picklist" or "MultiSelectPicklist" or "State" or "Status" or "Boolean"
                        ? "?$expand=OptionSet,GlobalOptionSet"
                        : "?$select=LogicalName" + (type is "Decimal" or "Double" or "Money" ? ",Precision" : "")
                            + (type is "Lookup" or "Customer" or "Owner" ? ",Targets" : "")
                            + (type is "Money" ? ",PrecisionSource" : "");
                    using var details = await GetAsync(host, token,
                        basePath + "/Attributes(LogicalName='" + fieldName + "')" +
                        "/Microsoft.Dynamics.CRM." + kindName + query, ct);
                    var derived = details.RootElement;
                    precision = Int(derived, "Precision");
                    currencyPrecisionSource = Int(derived, "PrecisionSource");
                    if (TryGet(derived, "Targets", out var lookupTargets) && lookupTargets.ValueKind == JsonValueKind.Array)
                        targets.AddRange(lookupTargets.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String)
                            .Select(n => n.GetString()!).Where(SafeName));
                    defaultValue = Raw(derived, "DefaultFormValue") ?? Raw(derived, "DefaultValue");
                    if (TryGet(derived, "OptionSet", out var optionSet) || TryGet(derived, "GlobalOptionSet", out optionSet))
                    {
                        if (TryGet(optionSet, "Options", out var options) && options.ValueKind == JsonValueKind.Array)
                            foreach (var option in options.EnumerateArray())
                                if (Int(option, "Value") is int value)
                                    opts.Add(new MetadataChoice(value, Labels(option, "Label")));
                        // Boolean options use TrueOption and FalseOption rather than Options.
                        foreach (var valueName in new[] { "TrueOption", "FalseOption" })
                            if (TryGet(optionSet, valueName, out var option) && Int(option, "Value") is int value)
                                opts.Add(new MetadataChoice(value, Labels(option, "Label")));
                    }
                }
                if (TryGet(item, "Targets", out var names) && names.ValueKind == JsonValueKind.Array)
                    targets.AddRange(names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String)
                        .Select(n => n.GetString()!).Where(SafeName));
                var read = Flag(item, "IsValidForRead");
                var create = Flag(item, "IsValidForCreate") && !calculated;
                var update = Flag(item, "IsValidForUpdate") && !calculated;
                columns.Add(new MetadataColumn(fieldName, type, read, create, update, calculated,
                    NestedString(item, "RequiredLevel", "Value"), precision, defaultValue, targets,
                    Labels(item, "DisplayName"), opts, currencyPrecisionSource));
            }
            // Dataverse returns lookup identifiers as read-only OData shadow properties
            // (_<logical lookup name>_value). They are not AttributeMetadata entries.
            // Materialize ONLY shadows derived from real readable Lookup/Customer/Owner attributes.
            // They require an independent metadata-field allowlist and per-client read grant.
            foreach (var lookup in columns.Where(c => c.CanRead &&
                c.Type is "Lookup" or "Customer" or "Owner").ToArray())
            {
                var virtualName = "_" + lookup.LogicalName + "_value";
                if (virtualName.Length > 128 || columns.Any(c => c.LogicalName == virtualName))
                    continue;
                columns.Add(new MetadataColumn(virtualName, "Uniqueidentifier",
                    true, false, false, true, lookup.RequiredLevel, null, null,
                    lookup.LookupTargets, lookup.Labels, [], null));
            }
            if (columns.Count > MaxColumns)
                throw new InvalidOperationException("Metadata lookup projections exceed field limit.");
            if (columns.Count == 0)
                throw new InvalidOperationException("Dataverse did not expose any readable metadata fields; previous snapshot preserved.");
            var relations = new List<MetadataRelationship>();
            if (includeExtended)
            foreach (var relKind in new[] { "OneToManyRelationships", "ManyToOneRelationships", "ManyToManyRelationships" })
            {
                var relationships = await CollectionAsync(host, token, basePath + "/" + relKind, ct);
                foreach (var rel in relationships)
                {
                    var schema = Str(rel, "SchemaName");
                    if (schema is null) continue;
                    relations.Add(new MetadataRelationship(schema, relKind,
                        Str(rel, "ReferencedEntity") ?? Str(rel, "Entity1LogicalName"),
                        Str(rel, "ReferencingEntity") ?? Str(rel, "Entity2LogicalName"),
                        Str(rel, "ReferencedAttribute"), Str(rel, "ReferencingAttribute"),
                        Str(rel, "ReferencingEntityNavigationPropertyName"),
                        Str(rel, "ReferencedEntityNavigationPropertyName")));
                }
            }
            var keys = new List<MetadataAlternateKey>();
            if (includeExtended)
            foreach (var key in await CollectionAsync(host, token, basePath + "/Keys?$select=LogicalName,KeyAttributes", ct))
            {
                var keyName = Str(key, "LogicalName");
                if (keyName is null || !SafeName(keyName) ||
                    !TryGet(key, "KeyAttributes", out var keyFields) ||
                    keyFields.ValueKind != JsonValueKind.Array) continue;
                var attrs = keyFields.EnumerateArray()
                    .Where(f => f.ValueKind == JsonValueKind.String)
                    .Select(f => f.GetString()!).ToArray();
                if (attrs.Length > 0 && attrs.Length <= 3 &&
                    attrs.All(f => SafeName(f) && columns.Any(c => c.LogicalName == f)))
                    keys.Add(new MetadataAlternateKey(keyName, attrs));
            }
            keys.Sort((a, b) => StringComparer.Ordinal.Compare(a.LogicalName, b.LogicalName));
            columns.Sort((a, b) => StringComparer.Ordinal.Compare(a.LogicalName, b.LogicalName));
            relations.Sort((a, b) => StringComparer.Ordinal.Compare(a.SchemaName, b.SchemaName));
            return new MetadataTable(name!, set!, primary!, Str(table, "PrimaryNameAttribute"),
                Flag(table, "IsCustomEntity"), Labels(table, "DisplayName"), columns, relations, keys);
    }

    /// <summary>
    /// Fetch real option values only for an explicitly authorized field. This avoids
    /// a costly per-attribute extended scan of large Dataverse tables.
    /// </summary>
    public async Task<IReadOnlyList<MetadataChoice>> DiscoverChoicesAsync(
        DynamicsConnection connection, string entity, string field, string type, CancellationToken ct)
    {
        if (!SafeName(entity) || !SafeName(field))
            throw new ArgumentException("Invalid Dataverse choice field.");
        var kind = type switch
        {
            "Picklist" => "PicklistAttributeMetadata",
            "State" => "StateAttributeMetadata",
            "Status" => "StatusAttributeMetadata",
            _ => throw new ArgumentException("Field does not have single-choice metadata.")
        };
        if (!DataverseUrlValidator.TryNormalize(connection.DataverseUrl, out var host))
            throw new InvalidOperationException("Untrusted Dataverse host.");
        var token = await tokens.AcquireAsync(connection, ct);
        var relative = ApiPath + "(LogicalName='" + entity + "')" +
            "/Attributes(LogicalName='" + field + "')" +
            "/Microsoft.Dynamics.CRM." + kind +
            (type == "Picklist" ? "?$expand=OptionSet,GlobalOptionSet" : "?$expand=OptionSet");
        using var metadata = await GetAsync(host, token, relative, ct);
        var root = metadata.RootElement;
        if (!TryGet(root, "OptionSet", out var set) ||
            !TryGet(set, "Options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            if (!TryGet(root, "GlobalOptionSet", out set) ||
                !TryGet(set, "Options", out options) || options.ValueKind != JsonValueKind.Array)
                return [];
        }
        var choices = new Dictionary<int, MetadataChoice>();
        foreach (var item in options.EnumerateArray())
            if (Int(item, "Value") is int value)
                choices[value] = new MetadataChoice(value, Labels(item, "Label"));
        return choices.Values.OrderBy(c => c.Value).ToArray();
    }

    private async Task<List<JsonElement>> CollectionAsync(string host, string token, string relative, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var next = relative;
        for (var page = 0; next is not null; page++)
        {
            if (page >= MaxPages) throw new InvalidOperationException("Dataverse metadata pagination limit exceeded.");
            using var doc = await GetAsync(host, token, next, ct);
            var root = doc.RootElement;
            if (!TryGet(root, "value", out var records) || records.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Dataverse metadata collection is invalid.");
            foreach (var record in records.EnumerateArray()) items.Add(record.Clone());
            if (items.Count > 100000)
                throw new InvalidOperationException("Dataverse metadata collection limit exceeded.");
            next = Str(root, "@odata.nextLink");
        }
        return items;
    }

    private async Task<JsonDocument> GetAsync(string host, string token, string relative, CancellationToken ct)
    {
        // Root-relative paths must be resolved against the authorized HTTPS host.
        // On Unix, Uri.TryCreate("/api/...", Absolute) can otherwise produce file://.
        Uri url;
        if (relative.StartsWith("/", StringComparison.Ordinal))
            url = new Uri(new Uri(host), relative);
        else if (Uri.TryCreate(relative, UriKind.Absolute, out var absolute))
            url = absolute;
        else throw new InvalidOperationException("Unsupported Dataverse metadata URL.");
        var expected = new Uri(host);
        if (url.Scheme != "https" || url.Host != expected.Host || url.Port != 443
            || url.UserInfo.Length > 0 || url.Fragment.Length > 0
            || !url.AbsolutePath.StartsWith(ApiPath, StringComparison.Ordinal))
            throw new InvalidOperationException("Untrusted Dataverse metadata continuation link.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("OData-Version", "4.0");
        request.Headers.TryAddWithoutValidation("OData-MaxVersion", "4.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    public static bool SafeName(string? name) =>
        name is { Length: > 0 and <= 128 } &&
        name[0] is >= 'a' and <= 'z' &&
        name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value)
            && value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined;
    }

    private static string? Str(JsonElement e, string name) =>
        TryGet(e, name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;

    private static bool Flag(JsonElement e, string name) =>
        TryGet(e, name, out var x) && x.ValueKind == JsonValueKind.True;

    private static int? Int(JsonElement e, string name) =>
        TryGet(e, name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var i) ? i : null;

    private static string? Raw(JsonElement e, string name) =>
        TryGet(e, name, out var x) ? x.ToString() : null;

    private static string? NestedString(JsonElement e, string parent, string child) =>
        TryGet(e, parent, out var x) ? Str(x, child) : null;

    private static IReadOnlyDictionary<int, string> Labels(JsonElement item, string name)
    {
        var result = new Dictionary<int, string>();
        if (!TryGet(item, name, out var labels)) return result;
        if (TryGet(labels, "LocalizedLabels", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var label in list.EnumerateArray())
                if (Int(label, "LanguageCode") is int code && Str(label, "Label") is { } text)
                    result[code] = text;
        return result;
    }
}
