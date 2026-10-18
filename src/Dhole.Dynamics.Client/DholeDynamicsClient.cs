using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dhole.Dynamics.Contracts;

namespace Dhole.Dynamics.Client;

public interface IDholeM2mTokenSource
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);
}

public sealed record DholeDynamicsClientOptions(Uri BaseAddress, TimeSpan RequestTimeout)
{
    public static DholeDynamicsClientOptions Create(string baseAddress, TimeSpan? timeout = null) =>
        new(new Uri(baseAddress, UriKind.Absolute), timeout ?? TimeSpan.FromSeconds(25));

    public void Validate()
    {
        if (BaseAddress.Scheme != Uri.UriSchemeHttps || BaseAddress.UserInfo.Length > 0 ||
            BaseAddress.Query.Length > 0 || BaseAddress.Fragment.Length > 0 ||
            BaseAddress.AbsolutePath != "/" || RequestTimeout < TimeSpan.FromSeconds(1) ||
            RequestTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("The connector must use a single HTTPS origin and a bounded timeout.");
    }
}

/// <summary>
/// Typed, independently authenticated HTTPS client for Pricing / DholeSystem.
/// Retries bounded safe GETs only. Never retries POST after transport failure or 5xx.
/// </summary>
public sealed class DholeDynamicsClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly IDholeM2mTokenSource _tokens;
    private readonly DholeDynamicsClientOptions _options;

    public DholeDynamicsClient(HttpClient http, IDholeM2mTokenSource tokens,
        DholeDynamicsClientOptions options)
    {
        options.Validate();
        _http = http;
        _tokens = tokens;
        _options = options;
    }

    public async Task<DholeQuoteTemplateResult> CreateConfirmedQuoteAsync(
        string connectionCode, Guid dholeRateId, IReadOnlyDictionary<string, JsonElement> inputs,
        Guid correlationId, CancellationToken ct)
    {
        if (dholeRateId == Guid.Empty || inputs is null || inputs.Count is < 3 or > 30)
            throw new ArgumentException("Rate ID and required template inputs must be provided.");
        var key = "dhole-rate-" + dholeRateId.ToString("N");
        var request = new DholeQuoteTemplateRequest(Connection(connectionCode), dholeRateId, true,
            inputs.Where(p => p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/dhole/quotes",
            request, key, correlationId, false, ct);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
        {
            var value = await response.Content.ReadFromJsonAsync<DholeQuoteTemplateResult>(Json, ct);
            if (value is not null && (response.IsSuccessStatusCode ||
                value.Status == "requires_reconciliation"))
                return value;
        }
        throw Error(response);
    }

    public async Task<DholeQuoteTemplateResult?> FindQuoteByRateAsync(string connectionCode,
        Guid rateId, Guid correlationId, CancellationToken ct)
    {
        if (rateId == Guid.Empty) throw new ArgumentException("Rate ID cannot be empty.");
        using var response = await SendAsync(HttpMethod.Get,
            "/api/v1/dhole/quotes/links/" + rateId.ToString("D") +
            "?connectionCode=" + Uri.EscapeDataString(Connection(connectionCode)),
            null, null, correlationId, true, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw Error(response);
        return await response.Content.ReadFromJsonAsync<DholeQuoteTemplateResult>(Json, ct);
    }

    public async Task<JsonElement> GetQuoteAsync(string connectionCode, Guid quoteId,
        Guid correlationId, CancellationToken ct)
    {
        if (quoteId == Guid.Empty) throw new ArgumentException("Quote ID cannot be empty.");
        using var response = await SendAsync(HttpMethod.Get,
            "/api/v1/records/quote/" + quoteId.ToString("D") +
            "?connectionCode=" + Uri.EscapeDataString(Connection(connectionCode)) +
            "&select=quoteid%2Cquotenumber%2Cname",
            null, null, correlationId, true, ct);
        if (!response.IsSuccessStatusCode) throw Error(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
    }

    public async Task<WriteOperationDetail?> GetOperationAsync(string connectionCode,
        Guid operationId, Guid correlationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Operation ID cannot be empty.");
        using var response = await SendAsync(HttpMethod.Get,
            "/api/v1/write-operations/" + operationId.ToString("D") +
            "?connectionCode=" + Uri.EscapeDataString(Connection(connectionCode)),
            null, null, correlationId, true, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw Error(response);
        return await response.Content.ReadFromJsonAsync<WriteOperationDetail>(Json, ct);
    }

    private static string Connection(string code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            !Regex.IsMatch(code, @"^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid connection code.");
        return code;
    }

    private static HttpRequestException Error(HttpResponseMessage response) =>
        new("Connector request failed with HTTP " + (int)response.StatusCode,
            null, response.StatusCode);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path,
        object? content, string? key, Guid correlationId, bool safeGet, CancellationToken ct)
    {
        if (correlationId == Guid.Empty) correlationId = Guid.NewGuid();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.RequestTimeout);
        var token = await _tokens.GetAccessTokenAsync(deadline.Token);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Machine token provider returned no token.");

        // The caller controls neither the host nor an arbitrary remote URL.
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, new Uri(_options.BaseAddress, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId.ToString("D"));
            if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            if (content is not null) request.Content = JsonContent.Create(content, options: Json);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token);
            if (!safeGet || attempt >= 2 ||
                response.StatusCode is not (HttpStatusCode.TooManyRequests or
                    HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or
                    HttpStatusCode.GatewayTimeout))
                return response;

            // Retry-After is advisory and capped; never block indefinitely on an upstream hint.
            var duration = response.Headers.RetryAfter?.Delta ??
                TimeSpan.FromMilliseconds(200 * (1 << attempt) + Random.Shared.Next(0, 90));
            if (response.Headers.RetryAfter?.Date is DateTimeOffset until)
                duration = until - DateTimeOffset.UtcNow;
            duration = TimeSpan.FromMilliseconds(Math.Clamp(duration.TotalMilliseconds, 50, 1500));
            response.Dispose();
            await Task.Delay(duration, deadline.Token);
        }
    }
}
