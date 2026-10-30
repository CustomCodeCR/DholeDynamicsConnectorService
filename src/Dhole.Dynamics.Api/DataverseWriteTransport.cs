using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;

namespace Dhole.Dynamics.Api;

public sealed record WriteCommand(string Operation, WritePolicy Policy, string Path,
    IReadOnlyDictionary<string, object?>? Payload, string? IfMatch = null, string? IfNoneMatch = null);

public sealed record WriteTransportOutcome(int HttpStatus, Guid? RecordId, string? ETag,
    string? Reference, int? RetryAfterSeconds, bool Ambiguous = false);

/// <summary>Fixed-host Dataverse writer. Exactly one dispatch; NEVER retries POST, PATCH or DELETE.</summary>
public sealed class DataverseWriteTransport(HttpClient http, IDataverseAccessTokenProvider tokens,
    ProductionWritePolicy? production = null)
{
    public async Task<WriteTransportOutcome> SendAsync(WriteCommand command, CancellationToken ct)
    {
        production?.Demand(command.Operation);
        var uri = Validate(command.Policy, command.Path, false);
        using var request = CreateRequest(command, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await tokens.AcquireAsync(command.Policy.Connection, ct));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var retry = response.Headers.RetryAfter?.Delta is TimeSpan span
            ? (int?)Math.Max(1, (int)Math.Ceiling(span.TotalSeconds)) : null;
        if (response.Headers.RetryAfter?.Date is DateTimeOffset until)
            retry = Math.Max(1, (int)Math.Ceiling((until-DateTimeOffset.UtcNow).TotalSeconds));
        if (!response.IsSuccessStatusCode)
        {
            // An HTTP 400 is a rejection, not an ambiguous successful POST.
            // Store only the vetted error code and, for StringLengthTooLong,
            // the column identifier and numeric MaxLength. Never persist raw
            // Dataverse messages, customer data, or request payloads.
            var safeReference = await ReadSafeErrorReferenceAsync(response, ct);
            return new((int)response.StatusCode, null, null,
                safeReference, retry);
        }
        await response.Content.LoadIntoBufferAsync(128 * 1024, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Guid? id = null;
        string? etag = response.Headers.ETag?.ToString();
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty(command.Policy.Table.PrimaryIdAttribute, out var primary)
                        && primary.ValueKind == JsonValueKind.String
                        && Guid.TryParse(primary.GetString(), out var parsed))
                        id = parsed;
                    if (doc.RootElement.TryGetProperty("@odata.etag", out var itemTag)
                        && itemTag.ValueKind == JsonValueKind.String)
                        etag ??= itemTag.GetString();
                }
            }
            catch (JsonException) { return new((int)response.StatusCode, null, null, null, retry, true); }
        }
        if (id is null && response.Headers.TryGetValues("OData-EntityId", out var ids))
        {
            var reference = ids.FirstOrDefault();
            var match = Regex.Match(reference ?? "", @"\(([0-9a-fA-F-]{36})\)$");
            if (match.Success && Guid.TryParse(match.Groups[1].Value, out var parsed)) id = parsed;
        }
        // For POST, missing a returned identifier means the write may have succeeded;
        // never claim definitively reconciled or reissue the create.
        var ambiguous = command.Operation == "create" && id is null;
        return new((int)response.StatusCode, id, etag, id?.ToString("D"), retry, ambiguous);
    }

    private static async Task<string?> ReadSafeErrorReferenceAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var bytes = new byte[8192];
            var count = await body.ReadAtLeastAsync(bytes.AsMemory(), bytes.Length,
                throwOnEndOfStream: false, cancellationToken: ct);
            // Don't parse truncated, oversized or empty error documents.
            if (count == 0 || count == bytes.Length) return null;
            return ExtractSafeErrorReference(bytes.AsMemory(0, count));
        }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException)
        {
            // Reading optional diagnostic metadata must not change the HTTP
            // rejection into an ambiguous write outcome.
            return null;
        }
    }

    public static string? ExtractSafeErrorCode(ReadOnlyMemory<byte> json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("code", out var codeNode) ||
            codeNode.ValueKind != JsonValueKind.String)
            return null;
        var code = codeNode.GetString();
        return code is { Length: > 0 and <= 64 } &&
            Regex.IsMatch(code, @"^(?:0x[0-9a-fA-F]{1,16}|[A-Za-z][A-Za-z0-9_.-]{0,63})$")
            ? code : null;
    }

    /// <summary>
    /// Preserve only safe, bounded diagnostics from a Dataverse text-limit
    /// rejection. The untrusted message is parsed but never retained.
    /// Historical reference values containing just the code stay supported.
    /// </summary>
    public static string? ExtractSafeErrorReference(ReadOnlyMemory<byte> json)
    {
        var code = ExtractSafeErrorCode(json);
        if (code is null) return null;
        var reference = "dataverse_code:" + code;
        if (!string.Equals(code, "0x80044331", StringComparison.OrdinalIgnoreCase))
            return reference;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("error", out var error) ||
            !error.TryGetProperty("message", out var messageNode) ||
            messageNode.ValueKind != JsonValueKind.String)
            return reference;

        // Dataverse's documented format identifies a safe logical column
        // name and numeric bound. Do not store other message content.
        var match = Regex.Match(messageNode.GetString() ?? "",
            @"length of the '([a-z][a-z0-9_]{0,79})' attribute of the 'quote' entity exceeded the maximum allowed length of '([0-9]{1,7})'",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        if (!match.Success ||
            !int.TryParse(match.Groups[2].Value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var max) ||
            max is < 1 or > 1048576)
            return reference;
        var field = match.Groups[1].Value.ToLowerInvariant();
        return reference + ";field:" + field + ";max:" +
            max.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed record QuoteDetailSnapshot(
        Guid RecordId, string Name, string ETag,
        decimal? Quantity = null, decimal? PricePerUnit = null);

    // Values obtained from Dataverse, not quote names, are the evidence that
    // the exact commercial line already matches Pricing. Missing data fails closed.
    public static bool MatchesPricingSale(
        IReadOnlyList<QuoteDetailSnapshot> lines, Guid lineId, decimal expectedSale) =>
        lineId != Guid.Empty && expectedSale >= 0 && lines.Count == 1 &&
        lines[0].RecordId == lineId && lines[0].Quantity is decimal quantity &&
        lines[0].PricePerUnit is decimal price &&
        Math.Abs(decimal.Round(quantity * price, 2,
            MidpointRounding.AwayFromZero) -
            decimal.Round(expectedSale, 2, MidpointRounding.AwayFromZero)) <= 0.01m;

    /// <summary>
    /// Read-only, bounded lookup for Pricing's automatically managed total line.
    /// Caller must have already passed the quotedetail read-field and API grants.
    /// The query path is composed only from approved table metadata and a GUID.
    /// No arbitrary OData request or rate-provided query is accepted.
    /// </summary>
    public async Task<IReadOnlyList<QuoteDetailSnapshot>> ReadQuoteDetailsAsync(
        WritePolicy detail, Guid quoteId, CancellationToken ct)
    {
        if (detail.Table.LogicalName != "quotedetail" || quoteId == Guid.Empty ||
            detail.Table.PrimaryIdAttribute != "quotedetailid" ||
            !DataverseMetadataDiscovery.SafeName(detail.Table.EntitySetName))
            throw new ReadRejected(403, "quote_detail_lookup_scope_denied");
        if (!DataverseUrlValidator.TryNormalize(detail.Connection.DataverseUrl, out var normalized))
            throw new ReadRejected(400, "invalid_dataverse_host");
        var relative = "/api/data/v9.2/" + detail.Table.EntitySetName +
            "?$select=quotedetailid,quotedetailname,quantity,priceperunit" +
            "&$filter=_quoteid_value%20eq%20" + quoteId.ToString("D") + "&$top=3";
        var uri = new Uri(new Uri(normalized), relative);
        if (uri.Scheme != "https" || uri.Port != 443 ||
            uri.Host != new Uri(normalized).Host || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0)
            throw new ReadRejected(400, "unsafe_quote_detail_lookup");
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await tokens.AcquireAsync(detail.Connection, ct));
        req.Headers.TryAddWithoutValidation("OData-Version", "4.0");
        using var response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            // This GET occurs after a confirmed Quote header. Preserve a safe
            // HTTP-specific diagnostic so operators can distinguish missing
            // Dataverse read permissions from throttling/availability.
            var reason = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "quote_detail_read_unauthorized",
                HttpStatusCode.Forbidden => "quote_detail_read_forbidden",
                HttpStatusCode.NotFound => "quote_detail_lookup_not_found",
                HttpStatusCode.TooManyRequests => "quote_detail_lookup_rate_limited",
                _ => "quote_detail_lookup_failed"
            };
            throw new ReadRejected(response.StatusCode == HttpStatusCode.TooManyRequests
                ? 429 : 502, reason);
        }
        await response.Content.LoadIntoBufferAsync(128 * 1024, ct);
        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("value", out var rows) ||
            rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 2 ||
            doc.RootElement.TryGetProperty("@odata.nextLink", out _))
            throw new ReadRejected(409, "quote_detail_ambiguous");
        var details = new List<QuoteDetailSnapshot>();
        foreach (var item in rows.EnumerateArray())
        {
            if (!item.TryGetProperty("quotedetailid", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(id.GetString(), out var detailId) || detailId == Guid.Empty ||
                !item.TryGetProperty("quotedetailname", out var title) ||
                title.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("@odata.etag", out var tag) ||
                tag.ValueKind != JsonValueKind.String)
                throw new ReadRejected(502, "quote_detail_incomplete");
            var etag = tag.GetString()!;
            GenericWriteService.RequireEtag(etag);
            decimal? quantity = item.TryGetProperty("quantity", out var q) &&
                q.ValueKind == JsonValueKind.Number && q.TryGetDecimal(out var qValue)
                ? qValue : null;
            decimal? price = item.TryGetProperty("priceperunit", out var p) &&
                p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var pValue)
                ? pValue : null;
            details.Add(new QuoteDetailSnapshot(detailId, title.GetString()!, etag,
                quantity, price));
        }
        return details;
    }

    public async Task<DateTimeOffset?> CreatedAtAsync(WritePolicy policy, Guid id, CancellationToken ct)
    {
        var path = "/api/data/v9.2/" + policy.Table.EntitySetName +
            "(" + id.ToString("D") + ")";
        var validated = Validate(policy, path, false);
        // Only the server-generated constant projection is appended after host/path validation.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(validated.AbsoluteUri + "?$select=createdon"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await tokens.AcquireAsync(policy.Connection, ct));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;
        await response.Content.LoadIntoBufferAsync(32 * 1024, ct);
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("createdon", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<WriteTransportOutcome> SendBatchAsync(IReadOnlyList<WriteCommand> commands, CancellationToken ct)
    {
        foreach(var command in commands)production?.Demand(command.Operation);
        if (commands.Count is < 1 or > 5 || commands.Any(c => c.Policy.Connection.Id != commands[0].Policy.Connection.Id))
            throw new ReadRejected(400, "invalid_batch");
        var policy = commands[0].Policy;
        var host = new Uri(DataverseUrlValidator.TryNormalize(policy.Connection.DataverseUrl, out var normalized)
            ? normalized : throw new ReadRejected(400, "invalid_dataverse_host"));
        var batchBoundary = "batch_" + Guid.NewGuid().ToString("N");
        var changeBoundary = "changeset_" + Guid.NewGuid().ToString("N");
        var content = new StringBuilder();
        content.Append("--").Append(batchBoundary).Append("\r\n")
            .Append("Content-Type: multipart/mixed; boundary=").Append(changeBoundary).Append("\r\n\r\n");
        for (var i = 0; i < commands.Count; i++)
        {
            var cmd = commands[i];
            Validate(policy, cmd.Path, false);
            var method = Verb(cmd.Operation);
            content.Append("--").Append(changeBoundary).Append("\r\n")
                .Append("Content-Type: application/http\r\n")
                .Append("Content-Transfer-Encoding: binary\r\n")
                .Append("Content-ID: ").Append(i + 1).Append("\r\n\r\n")
                .Append(method.Method).Append(' ').Append(cmd.Path).Append(" HTTP/1.1\r\n")
                .Append("Content-Type: application/json\r\n");
            if (cmd.IfMatch is not null) content.Append("If-Match: ").Append(cmd.IfMatch).Append("\r\n");
            if (cmd.IfNoneMatch is not null) content.Append("If-None-Match: ").Append(cmd.IfNoneMatch).Append("\r\n");
            content.Append("\r\n");
            if (cmd.Payload is not null)
                content.Append(JsonSerializer.Serialize(cmd.Payload));
            content.Append("\r\n");
        }
        content.Append("--").Append(changeBoundary).Append("--\r\n")
            .Append("--").Append(batchBoundary).Append("--\r\n");
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(host, "/api/data/v9.2/$batch"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await tokens.AcquireAsync(policy.Connection, ct));
        req.Content = new StringContent(content.ToString(), Encoding.UTF8);
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            "multipart/mixed; boundary=" + batchBoundary);
        using var response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return new((int)response.StatusCode, null, null, null, null);
        await response.Content.LoadIntoBufferAsync(256 * 1024, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        // $batch top-level HTTP 200 does NOT prove that each operation succeeded.
        var statuses = Regex.Matches(raw, @"HTTP/1\.[01]\s+([1-5][0-9]{2})")
            .Select(x => int.Parse(x.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (statuses.Length != commands.Count)
            return new((int)response.StatusCode, null, null, null, null, true);
        if (statuses.Any(code => code >= 500))
            return new(502, null, null, "batch may be partially applied", null, true);
        if (statuses.Any(code => code >= 400))
            return new(statuses.First(code => code >= 400), null, null,
                "batch rejected by Dataverse changeset", null);
        return new(200, null, null, "batch accepted: " + statuses.Length + " operations", null);
    }

    public static HttpRequestMessage CreateRequest(WriteCommand cmd, Uri uri)
    {
        var method = Verb(cmd.Operation);
        var request = new HttpRequestMessage(method, uri);
        if (cmd.IfMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", cmd.IfMatch);
        if (cmd.IfNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", cmd.IfNoneMatch);
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        request.Headers.TryAddWithoutValidation("OData-Version", "4.0");
        if (cmd.Payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(cmd.Payload), Encoding.UTF8, "application/json");
        return request;
    }

    public static HttpMethod Verb(string operation) => operation switch
    {
        "create" => HttpMethod.Post,
        "update" or "upsert" => HttpMethod.Patch,
        "delete" => HttpMethod.Delete,
        _ => throw new ReadRejected(400, "invalid_write_verb")
    };

    public static Uri Validate(WritePolicy policy, string path, bool batch)
    {
        if (!DataverseUrlValidator.TryNormalize(policy.Connection.DataverseUrl, out var normalized))
            throw new ReadRejected(400, "invalid_dataverse_host");
        if (!path.StartsWith("/api/data/v9.2/", StringComparison.Ordinal) ||
            path.Contains('#') || path.Contains('\r') || path.Contains('\n'))
            throw new ReadRejected(400, "invalid_write_path");
        var prefix = "/api/data/v9.2/" + policy.Table.EntitySetName;
        if (!batch && path != prefix && !path.StartsWith(prefix + "(", StringComparison.Ordinal))
            throw new ReadRejected(400, "write_entity_mismatch");
        var uri = new Uri(new Uri(normalized), path);
        if (uri.Scheme != "https" || uri.Host != new Uri(normalized).Host || uri.Port != 443 ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            throw new ReadRejected(400, "unsafe_write_url");
        return uri;
    }
}
