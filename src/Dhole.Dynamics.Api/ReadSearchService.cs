using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Executes only metadata-approved OData v9.2 resource reads on the connection's pinned HTTPS host.
/// No arbitrary query URLs, HTTP redirects, raw OData filters, $skip or unbounded streaming.
/// </summary>
public sealed class ReadSearchService(HttpClient http, IDataverseAccessTokenProvider tokens,
    ReadAccessRepository policy, ReadCursorProtector cursors)
{
    public async Task<ReadSearchResponse> SearchAsync(ClaimsPrincipal user, string entity,
        ReadSearchRequest request, CancellationToken ct, string? savedName = null, bool admin = false,
        SavedQueryApiKeyIdentity? apiKey = null)
    {
        var context = await policy.ResolveAsync(request.ConnectionCode, entity, ct);
        if (apiKey is not null && (apiKey.ConnectionId != context.Connection.Id ||
                apiKey.QueryName != savedName || admin))
            throw new ReadRejected(403,"integration_key_scope_denied");
        var issuer = apiKey is not null ? "integration-key" : admin
            ? "admin:oidc" : user.FindFirst("connector:issuer")?.Value ?? "";
        var client = apiKey is not null ? apiKey.Id.ToString("D") : admin
            ? ScopeSecurity.ActorId(user) : ScopeSecurity.ClientId(user) ?? "";
        if (admin && !ScopeSecurity.HasAdminRole(user, "dynamics.security.admin"))
            throw new ReadRejected(403, "admin_records_denied");
        var page = 1;
        string? next = null;
        ReadSpec query;

        if (!string.IsNullOrWhiteSpace(request.Cursor))
        {
            if (request.Query is not null) throw new ReadRejected(400, "cursor_with_query");
            var cursor = cursors.Open(request.Cursor);
            if (cursor.ConnectionId != context.Connection.Id || cursor.Issuer != issuer ||
                cursor.ClientId != client || cursor.Entity != entity ||
                cursor.MetadataVersion != context.Snapshot.Version || cursor.SavedName != savedName)
                throw new ReadRejected(409, "stale_or_foreign_cursor");
            query = cursor.Spec;
            next = cursor.NextLink;
            page = cursor.Page;
        }
        else
            query = request.Query ?? throw new ReadRejected(400, "query_required");

        // Re-compile and authorize on EVERY page, even for a previously authenticated cursor.
        // Caller provided strings are never used as OData filter fragments.
        var primary = ReadQueryCompiler.Compile(context.Table, query with { Expand = null }, context.Allowed);
        if (apiKey is not null)
        {
            // The key has already been validated against this single saved query
            // and billed under its quota. Never grant arbitrary searches or expands.
            if (query.Expand is { Count: > 0 } ||
                primary.RequiredFields.Count == 0 ||
                primary.RequiredFields.Any(f => !context.Allowed.Contains(f)))
                throw new ReadRejected(403,"integration_key_fields_denied");
        }
        else if (admin) PermitAdmin(user, context, primary.RequiredFields);
        else await policy.PermitAsync(user, context, primary.RequiredFields, ct);
        var expansion = apiKey is not null ? "" :
            await ExpandAsync(user, context, query.Expand, ct, admin);
        var relative = primary.RelativeUrl + expansion;
        var url = next ?? relative;
        var root = await RequestAsync(context, url, query.PageSize, ct);
        using (root)
        {
            if (!root.RootElement.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array
                || items.GetArrayLength() > query.PageSize)
                throw new ReadRejected(502, "invalid_dataverse_page");
            var records = items.EnumerateArray().Select(x =>
                ReadFormattedValues.AddChoiceLabels(x, context.Table, query.Select)).ToArray();
            long? count = root.RootElement.TryGetProperty("@odata.count", out var size) &&
                size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var parsed) ? parsed : null;
            var link = root.RootElement.TryGetProperty("@odata.nextLink", out var nextLink) &&
                nextLink.ValueKind == JsonValueKind.String ? nextLink.GetString() : null;
            string? cursorToken = null;
            if (link is not null && page < ReadQueryCompiler.MaxPages)
            {
                ValidateUrl(context, link, continuation: true);
                cursorToken = cursors.Seal(new ReadCursor(context.Connection.Id, issuer, client,
                    entity, context.Snapshot.Version, query, link, page + 1,
                    DateTimeOffset.UtcNow.AddMinutes(15), savedName));
            }
            return new ReadSearchResponse(entity, records, cursorToken, count, page,
                context.Snapshot.Version, link is not null && cursorToken is null);
        }
    }

    public async Task<ReadRecordResponse> ByIdAsync(ClaimsPrincipal user, string code, string entity,
        Guid id, IReadOnlyList<string> select, CancellationToken ct, bool admin = false)
    {
        if (id == Guid.Empty) throw new ReadRejected(400, "invalid_record_id");
        var context = await policy.ResolveAsync(code, entity, ct);
        var compiled = ReadQueryCompiler.Compile(context.Table, new ReadSpec(select), context.Allowed);
        if (admin) PermitAdmin(user, context, compiled.RequiredFields);
        else await policy.PermitAsync(user, context, compiled.RequiredFields, ct);
        var path = "/api/data/v9.2/" + context.Table.EntitySetName + "(" + id.ToString("D") + ")" +
            compiled.RelativeUrl[compiled.RelativeUrl.IndexOf('?', StringComparison.Ordinal)..];
        using var body = await RequestAsync(context, path, 1, ct, singleRecord: true);
        return Record(entity, body, context.Snapshot.Version, context.Table, select);
    }

    public async Task<ReadRecordResponse> ByKeyAsync(ClaimsPrincipal user, string code, string entity,
        IReadOnlyDictionary<string, JsonElement> values, IReadOnlyList<string> select, CancellationToken ct)
    {
        var context = await policy.ResolveAsync(code, entity, ct);
        var keys = context.Table.AlternateKeys ?? [];
        if (values.Count == 0 || values.Count > 3)
            throw new ReadRejected(400, "invalid_alternate_key");
        var key = keys.FirstOrDefault(k => k.Attributes.Count == values.Count &&
            k.Attributes.All(values.ContainsKey))
            ?? throw new ReadRejected(400, "unregistered_alternate_key");
        var compiled = ReadQueryCompiler.Compile(context.Table, new ReadSpec(select), context.Allowed);
        var required = compiled.RequiredFields.Concat(key.Attributes).Distinct(StringComparer.Ordinal).ToArray();
        await policy.PermitAsync(user, context, required, ct);

        var pieces = new List<string>();
        foreach (var field in key.Attributes)
        {
            var column = context.Table.Columns.First(c => c.LogicalName == field);
            var literal = ReadQueryCompiler.Literal(column, values[field]);
            pieces.Add(field + "=" + Uri.EscapeDataString(literal));
        }
        var path = "/api/data/v9.2/" + context.Table.EntitySetName + "(" + string.Join(",", pieces) + ")" +
            compiled.RelativeUrl[compiled.RelativeUrl.IndexOf('?', StringComparison.Ordinal)..];
        using var body = await RequestAsync(context, path, 1, ct, singleRecord: true);
        return Record(entity, body, context.Snapshot.Version, context.Table, select);
    }

    private static ReadRecordResponse Record(string entity, JsonDocument doc, int version,
        MetadataTable table, IReadOnlyList<string> selected)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new ReadRejected(502, "invalid_dataverse_record");
        var item = ReadFormattedValues.AddChoiceLabels(doc.RootElement, table, selected);
        var etag = item.TryGetProperty("@odata.etag", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;
        return new ReadRecordResponse(entity, item, etag, version);
    }

    private async Task<string> ExpandAsync(ClaimsPrincipal user, ReadPolicy main,
        IReadOnlyList<ReadExpand>? requested, CancellationToken ct, bool admin)
    {
        if (requested is not { Count: > 0 }) return "";
        if (requested.Count > 2 || requested.Select(e => e.Navigation).Distinct(StringComparer.Ordinal).Count() != requested.Count)
            throw new ReadRejected(400, "invalid_expand");
        var expansions = new List<string>();
        foreach (var expand in requested)
        {
            // Only directly declared single-valued many-to-one navigation is supported;
            // no recursive or collection expansions that can hide unbounded data.
            var relationship = main.Table.Relationships.FirstOrDefault(r =>
                r.Kind == "ManyToOneRelationships" && r.ReferencingNavigation == expand.Navigation);
            if (relationship?.ReferencedEntity is null || expand.Select is not { Count: > 0 and <= 12 } ||
                !DataverseMetadataDiscovery.SafeName(expand.Navigation))
                throw new ReadRejected(403, "expand_denied");
            var related = await policy.ResolveAsync(main.Connection.Code, relationship.ReferencedEntity, ct);
            if (expand.Select.Distinct(StringComparer.Ordinal).Count() != expand.Select.Count ||
                expand.Select.Any(f => !related.Allowed.Contains(f)))
                throw new ReadRejected(403, "expand_fields_denied");
            // A second independent per-entity grant is required for the related table.
            if (admin) PermitAdmin(user, related, expand.Select);
            else await policy.PermitAsync(user, related, expand.Select, ct);
            expansions.Add(expand.Navigation + "($select=" + string.Join(",", expand.Select) + ")");
        }
        return "&$expand=" + Uri.EscapeDataString(string.Join(",", expansions));
    }

    private static void PermitAdmin(System.Security.Claims.ClaimsPrincipal user, ReadPolicy context,
        IReadOnlyCollection<string> fields)
    {
        if (!ScopeSecurity.HasAdminRole(user, "dynamics.security.admin") ||
            fields.Count == 0 || fields.Any(f => !context.Allowed.Contains(f)))
            throw new ReadRejected(403, "admin_read_fields_denied");
    }

    private async Task<JsonDocument> RequestAsync(ReadPolicy context, string path, int size,
        CancellationToken ct, bool singleRecord = false)
    {
        var uri = ValidateUrl(context, path, continuation: path.StartsWith("https://", StringComparison.Ordinal));
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await tokens.AcquireAsync(context.Connection, ct));
        req.Headers.TryAddWithoutValidation("Prefer", "odata.maxpagesize=" + size +
            ", odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"");
        req.Headers.TryAddWithoutValidation("OData-Version", "4.0");
        using var response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (singleRecord && response.StatusCode == HttpStatusCode.NotFound)
            throw new ReadRejected(404, "record_not_found");
        if (!response.IsSuccessStatusCode)
            throw new ReadRejected(response.StatusCode == HttpStatusCode.TooManyRequests ? 429 : 502,
                "dataverse_read_failed");
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        try { return await JsonDocument.ParseAsync(stream, cancellationToken: ct); }
        catch (JsonException) { throw new ReadRejected(502, "invalid_dataverse_json"); }
    }

    public static Uri ValidateUrl(ReadPolicy context, string raw, bool continuation)
    {
        if (!DataverseUrlValidator.TryNormalize(context.Connection.DataverseUrl, out var host))
            throw new ReadRejected(400, "invalid_dataverse_host");
        Uri uri;
        if (raw.StartsWith("/", StringComparison.Ordinal))
            uri = new Uri(new Uri(host), raw);
        else if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
            uri = absolute;
        else throw new ReadRejected(400, "invalid_dataverse_url");
        var parent = new Uri(host);
        var path = "/api/data/v9.2/" + context.Table.EntitySetName;
        if (uri.Scheme != "https" || !string.Equals(uri.Host, parent.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ||
            (uri.AbsolutePath != path && !(uri.AbsolutePath.StartsWith(path + "(", StringComparison.Ordinal)
                && uri.AbsolutePath.EndsWith(")", StringComparison.Ordinal))) ||
            uri.ToString().Length > 16000 ||
            continuation && (uri.AbsolutePath != path ||
                !uri.Query.Contains("skiptoken=", StringComparison.OrdinalIgnoreCase)))
            throw new ReadRejected(400, "invalid_dataverse_url");
        return uri;
    }
}
