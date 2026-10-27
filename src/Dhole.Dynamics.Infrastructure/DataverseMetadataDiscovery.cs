using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;

namespace Dhole.Dynamics.Infrastructure;

// Keep all index states visible to administrators. Only Active is usable for
// record UPSERT. Dataverse creates the unique index asynchronously.
public sealed class DataverseKeyCreationRejected(int remoteStatus, string reason) : Exception(
    "Dataverse rejected alternate key metadata; upstream status " + remoteStatus +
    "; sanitized reason " + reason)
{
    public int RemoteStatus { get; } = remoteStatus;
    // Fixed, allowlisted reason. Never return provider response text.
    public string Reason { get; } = reason;
}

public sealed record DataverseAlternateKeyState(
    string LogicalName, IReadOnlyList<string> Attributes, string Status);

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
            // Keys are cheap metadata and are required even in a non-extended
            // refresh. Previously includeExtended=false always returned [],
            // making the UPSERT selector empty despite existing Dataverse keys.
            var keys = await DiscoverAlternateKeysWithTokenAsync(
                host, token, name!, columns, ct);
            columns.Sort((a, b) => StringComparer.Ordinal.Compare(a.LogicalName, b.LogicalName));
            relations.Sort((a, b) => StringComparer.Ordinal.Compare(a.SchemaName, b.SchemaName));
            return new MetadataTable(name!, set!, primary!, Str(table, "PrimaryNameAttribute"),
                Flag(table, "IsCustomEntity"), Labels(table, "DisplayName"), columns, relations, keys);
    }

    /// <summary>
    /// Creates only the UNIQUE metadata index on an EXISTING Quote field.
    /// Duplicate clicks/retries first inspect real Dataverse metadata, so a
    /// previously accepted but slow response does not create a second key.
    /// Response headers are bounded to finish BEFORE the public proxy timeout.
    /// </summary>
    public async Task<DataverseAlternateKeyState> CreateQuoteAlternateKeyAsync(
        DynamicsConnection connection, string field, CancellationToken ct)
    {
        if (field != "new_idtraasignado")
            throw new ArgumentException("The Quote identity is reserved for the Dhole Rate GUID.",
                nameof(field));
        if (!DataverseUrlValidator.TryNormalize(connection.DataverseUrl, out var host))
            throw new InvalidOperationException("Untrusted Dataverse host.");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(22));
        try
        {
            var token = await tokens.AcquireAsync(connection, bounded.Token);
            var existing = await DiscoverKeyStatesWithTokenAsync(
                host, token, "quote", bounded.Token);
            var sameColumn = existing.Where(key => key.Attributes.Count == 1 &&
                key.Attributes[0] == field).ToArray();
            if (sameColumn.Length > 1)
                throw new InvalidOperationException(
                    "Dataverse has more than one key on the Dhole identity field.");
            if (sameColumn.Length == 1)
                return sameColumn[0]; // Active / Pending / InProgress / Failed, never duplicate

            // Live metadata preflight: a writable String field may still be too
            // large for Dataverse's unique-key index (900 UTF-16 bytes).
            var fieldPath = ApiPath + "(LogicalName='quote')" +
                "/Attributes(LogicalName='" + field + "')" +
                "/Microsoft.Dynamics.CRM.StringAttributeMetadata" +
                "?$select=LogicalName,MaxLength,IsSecured,DisplayName";
            JsonElement fieldMetadata;
            try
            {
                using var details = await GetAsync(host, token, fieldPath, bounded.Token);
                fieldMetadata = details.RootElement.Clone();
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new DataverseKeyCreationRejected(400, "attribute");
            }
            var incompatibility = QuoteKeyTextCompatibility(fieldMetadata);
            if (incompatibility is not null)
                throw new DataverseKeyCreationRejected(400, incompatibility);
            var preferredLcid = PreferredKeyLabelLanguage(fieldMetadata);

            // Key metadata has its own SchemaName; it is not a new field.
            var endpoint = new Uri(new Uri(host),
                ApiPath + "(LogicalName='quote')/Keys");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("OData-Version", "4.0");
            request.Headers.TryAddWithoutValidation("OData-MaxVersion", "4.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // Dataverse requires a user-facing DisplayName for key metadata.
            // A key is an index on this existing column, never a new column.
            var metadata = BuildDholeQuoteKeyMetadata(field, preferredLcid);
            request.Content = new StringContent(JsonSerializer.Serialize(metadata),
                System.Text.Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, bounded.Token);
            if (!response.IsSuccessStatusCode)
            {
                // Read only a small amount of the provider error and reduce it
                // to an allowlisted category. Never expose untrusted details,
                // quotes, tenant URLs, GUIDs or credentials to the UI.
                var category = "unknown";
                try
                {
                    await using var errorStream = await response.Content.ReadAsStreamAsync(
                        bounded.Token);
                    var errorBuffer = new byte[8192];
                    var size = await errorStream.ReadAsync(errorBuffer, bounded.Token);
                    category = ClassifyKeyCreationError(
                        System.Text.Encoding.UTF8.GetString(errorBuffer, 0, size));
                }
                catch (JsonException) { }
                throw new DataverseKeyCreationRejected((int)response.StatusCode, category);
            }
            return new DataverseAlternateKeyState("new_DholeKey_" + field,
                [field], "Pending");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Remote metadata change may have completed after the timeout.
            // The caller must refresh status rather than blindly POST again.
            throw new TimeoutException(
                "Dataverse did not confirm key creation within 22 seconds.");
        }
    }

    /// <summary>Fixed Dataverse metadata payload. No user-controlled schema names.</summary>
    /// <summary>Validate live String metadata before attempting a unique index.</summary>
    public static string? QuoteKeyTextCompatibility(JsonElement metadata)
    {
        if (Str(metadata, "LogicalName") != "new_idtraasignado")
            return "attribute";
        if (Flag(metadata, "IsSecured"))
            return "secured_field";
        var maxLength = Int(metadata, "MaxLength");
        if (maxLength is null or <= 0)
            return "attribute";
        // Dataverse supports at most 900 key bytes, and Strings are Unicode.
        return maxLength > 450 ? "key_size" : null;
    }

    private static int PreferredKeyLabelLanguage(JsonElement metadata)
    {
        if (TryGet(metadata, "DisplayName", out var label) &&
            TryGet(label, "LocalizedLabels", out var locales) &&
            locales.ValueKind == JsonValueKind.Array)
        {
            foreach (var locale in locales.EnumerateArray())
            {
                if (Int(locale, "LanguageCode") is int lcid and > 0)
                    return lcid;
            }
        }
        return 1033;
    }

    public static Dictionary<string, object> BuildDholeQuoteKeyMetadata(string field,
        int languageCode = 1033)
    {
        if (field != "new_idtraasignado")
            throw new ArgumentException("Only the permanent Dhole RateId column is allowed.",
                nameof(field));
        if (languageCode <= 0)
            throw new ArgumentOutOfRangeException(nameof(languageCode));
        return new Dictionary<string, object>
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.EntityKeyMetadata",
            ["SchemaName"] = "new_DholeKey_" + field,
            ["DisplayName"] = new Dictionary<string, object>
            {
                ["@odata.type"] = "Microsoft.Dynamics.CRM.Label",
                ["LocalizedLabels"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["@odata.type"] = "Microsoft.Dynamics.CRM.LocalizedLabel",
                        ["Label"] = "Dhole Rate GUID",
                        ["LanguageCode"] = languageCode
                    }
                }
            },
            ["KeyAttributes"] = new[] { field }
        };
    }

    /// <summary>Convert provider error text to a small set of safe metadata reasons.</summary>
    public static string ClassifyKeyCreationError(string? errorJson)
    {
        if (string.IsNullOrWhiteSpace(errorJson) || errorJson.Length > 8192)
            return "unknown";
        try
        {
            using var doc = JsonDocument.Parse(errorJson);
            if (!doc.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("message", out var msg) ||
                msg.ValueKind != JsonValueKind.String)
                return "unknown";
            var message = msg.GetString() ?? "";
            if (message.Contains("DisplayName", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("LocalizedLabel", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("LanguageCode", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("language is not enabled", StringComparison.OrdinalIgnoreCase))
                return "display_name";
            if (message.Contains("privilege", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("access denied", StringComparison.OrdinalIgnoreCase))
                return "permission";
            if (message.Contains("secured", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("field level security", StringComparison.OrdinalIgnoreCase))
                return "secured_field";
            if (message.Contains("MaxLength", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("900 bytes", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("key size", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("maximum key length", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("maximum length for a key", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("too long", StringComparison.OrdinalIgnoreCase))
                return "key_size";
            if (message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                return "existing_or_duplicate";
            if (message.Contains("SchemaName", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("publisher", StringComparison.OrdinalIgnoreCase))
                return "schema_name";
            if (message.Contains("new_idtraasignado", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("attribute", StringComparison.OrdinalIgnoreCase))
                return "attribute";
        }
        catch (JsonException) { }
        return "unknown";
    }

    public async Task<IReadOnlyList<DataverseAlternateKeyState>> DiscoverAlternateKeyStatesAsync(
        DynamicsConnection connection, string entity, CancellationToken ct)
    {
        if (!SafeName(entity))
            throw new ArgumentException("Invalid Dataverse table name.", nameof(entity));
        if (!DataverseUrlValidator.TryNormalize(connection.DataverseUrl, out var host))
            throw new InvalidOperationException("Untrusted Dataverse host.");
        var token = await tokens.AcquireAsync(connection, ct);
        return await DiscoverKeyStatesWithTokenAsync(host, token, entity, ct);
    }

    private async Task<IReadOnlyList<DataverseAlternateKeyState>> DiscoverKeyStatesWithTokenAsync(
        string host,string token,string entity,CancellationToken ct)
    {
        var path = ApiPath + "(LogicalName='" + entity + "')" +
            "/Keys?$select=LogicalName,KeyAttributes,EntityKeyIndexStatus";
        var rows = await CollectionAsync(host, token, path, ct);
        return ParseAlternateKeyStates(rows);
    }

    /// <summary>Active-only return for the write-security metadata catalog.</summary>
    public async Task<IReadOnlyList<MetadataAlternateKey>> DiscoverAlternateKeysAsync(
        DynamicsConnection connection, string entity,
        IReadOnlyList<MetadataColumn> columns, CancellationToken ct)
    {
        var states = await DiscoverAlternateKeyStatesAsync(connection, entity, ct);
        return ActiveAlternateKeys(states, columns);
    }

    private async Task<IReadOnlyList<MetadataAlternateKey>> DiscoverAlternateKeysWithTokenAsync(
        string host, string token, string entity,
        IReadOnlyList<MetadataColumn> columns, CancellationToken ct)
    {
        return ActiveAlternateKeys(
            await DiscoverKeyStatesWithTokenAsync(host, token, entity, ct), columns);
    }

    public static IReadOnlyList<DataverseAlternateKeyState> ParseAlternateKeyStates(
        IReadOnlyList<JsonElement> rows)
    {
        var keys = new List<DataverseAlternateKeyState>();
        foreach(var row in rows)
        {
            var name=Str(row,"LogicalName");
            if(!SafeName(name) ||
                !TryGet(row,"KeyAttributes",out var fields) ||
                fields.ValueKind!=JsonValueKind.Array) continue;
            var attrs=fields.EnumerateArray()
                .Where(v=>v.ValueKind==JsonValueKind.String)
                .Select(v=>v.GetString()!).ToArray();
            if(attrs.Length is < 1 or > 16 ||
                attrs.Distinct(StringComparer.Ordinal).Count()!=attrs.Length ||
                attrs.Any(x=>!SafeName(x))) continue;
            var numeric=Int(row,"EntityKeyIndexStatus");
            var raw=Str(row,"EntityKeyIndexStatus");
            var status=numeric switch
            {
                0=>"Pending",1=>"InProgress",2=>"Active",3=>"Failed",_=>raw switch
                {
                    "Pending" or "InProgress" or "Active" or "Failed"=>raw,
                    _=>"Unknown"
                }
            };
            keys.Add(new DataverseAlternateKeyState(name!,attrs,status));
        }
        return keys.OrderBy(k=>k.LogicalName,StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<MetadataAlternateKey> ActiveAlternateKeys(
        IReadOnlyList<DataverseAlternateKeyState> states,
        IReadOnlyList<MetadataColumn> columns) =>
        states.Where(k=>k.Status=="Active" && k.Attributes.Count is >= 1 and <= 3 &&
                k.Attributes.All(field=>columns.Any(c=>c.LogicalName==field)))
              .Select(k=>new MetadataAlternateKey(k.LogicalName,k.Attributes))
              .ToArray();

    public static IReadOnlyList<MetadataAlternateKey> ParseActiveAlternateKeys(
        IReadOnlyList<JsonElement> rows, IReadOnlyList<MetadataColumn> columns) =>
        ActiveAlternateKeys(ParseAlternateKeyStates(rows),columns);

    /// <summary>
    /// Fetch real option values only for an explicitly authorized field. This avoids
    /// a costly per-attribute extended scan of large Dataverse tables.
    /// </summary>
    /// <summary>
    /// Discover only ManyToOne navigation names. This is intentionally separate
    /// from extended attribute discovery (which would issue hundreds of calls on quote).
    /// Relationship names must come from Dataverse metadata, never be guessed.
    /// </summary>
    public async Task<IReadOnlyList<MetadataRelationship>> DiscoverLookupRelationshipsAsync(
        DynamicsConnection connection, string entity, CancellationToken ct)
    {
        if (!SafeName(entity)) throw new ArgumentException("Invalid table.", nameof(entity));
        if (!DataverseUrlValidator.TryNormalize(connection.DataverseUrl, out var host))
            throw new InvalidOperationException("Untrusted Dataverse host.");
        var token = await tokens.AcquireAsync(connection, ct);
        var path = ApiPath + "(LogicalName='" + entity + "')" +
            "/ManyToOneRelationships?$select=SchemaName,ReferencedEntity,ReferencingEntity," +
            "ReferencedAttribute,ReferencingAttribute,ReferencingEntityNavigationPropertyName," +
            "ReferencedEntityNavigationPropertyName";
        var rows = await CollectionAsync(host, token, path, ct);
        return rows.Select(row => new MetadataRelationship(
                Str(row, "SchemaName") ?? "",
                "ManyToOneRelationships",
                Str(row, "ReferencedEntity"), Str(row, "ReferencingEntity"),
                Str(row, "ReferencedAttribute"), Str(row, "ReferencingAttribute"),
                Str(row, "ReferencingEntityNavigationPropertyName"),
                Str(row, "ReferencedEntityNavigationPropertyName")))
            .Where(rel => rel.ReferencingNavigation is { Length: > 0 and <= 128 } nav &&
                (char.IsAsciiLetter(nav[0]) || nav[0] == '_') &&
                nav.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') &&
                SafeName(rel.ReferencingAttribute) && SafeName(rel.ReferencedEntity) &&
                rel.ReferencingEntity == entity)
            .OrderBy(rel => rel.ReferencingAttribute, StringComparer.Ordinal)
            .ThenBy(rel => rel.ReferencedEntity, StringComparer.Ordinal)
            .ToArray();
    }

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

    /// <summary>
    /// Read actual tenant string limits for a single approved Dataverse table,
    /// without changing snapshot fingerprints or field grants. Called before
    /// Pricing's first quote POST, never after a possible write.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> DiscoverTextMaxLengthsAsync(
        DynamicsConnection connection, string entity, CancellationToken ct)
    {
        if (!SafeName(entity))
            throw new ArgumentException("Invalid Dataverse table name.", nameof(entity));
        if (!DataverseUrlValidator.TryNormalize(connection.DataverseUrl, out var host))
            throw new InvalidOperationException("Untrusted Dataverse host.");
        var token = await tokens.AcquireAsync(connection, ct);
        var prefix = ApiPath + "(LogicalName='" + entity + "')" +
            "/Attributes/Microsoft.Dynamics.CRM.";
        var lengths = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cast in new[] { "StringAttributeMetadata", "MemoAttributeMetadata" })
        {
            var rows = await CollectionAsync(host, token, prefix + cast +
                "?$select=LogicalName,MaxLength", ct);
            if (rows.Count > MaxColumns)
                throw new InvalidOperationException("Dataverse text metadata field limit exceeded.");
            foreach (var row in rows)
            {
                var field = Str(row, "LogicalName");
                var limit = Int(row, "MaxLength");
                if (SafeName(field) && limit is > 0 and <= 1048576)
                    lengths[field!] = limit.Value;
            }
        }
        if (lengths.Count == 0)
            throw new InvalidOperationException("Dataverse did not return quote text limits.");
        return lengths;
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
