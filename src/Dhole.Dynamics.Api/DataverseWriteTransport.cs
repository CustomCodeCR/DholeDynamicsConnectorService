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
public sealed class DataverseWriteTransport(HttpClient http, IDataverseAccessTokenProvider tokens)
{
    public async Task<WriteTransportOutcome> SendAsync(WriteCommand command, CancellationToken ct)
    {
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
            return new((int)response.StatusCode, null, null, null, retry);
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
