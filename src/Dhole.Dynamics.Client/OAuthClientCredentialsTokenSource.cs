using System.Net.Http.Json;
using System.Text.Json;

namespace Dhole.Dynamics.Client;

/// <summary>OAuth2 client-credentials token acquisition with in-memory expiration and no secret logging.</summary>
public sealed class OAuthClientCredentialsTokenSource : IDholeM2mTokenSource
{
    private readonly HttpClient _http;
    private readonly Uri _tokenEndpoint;
    private readonly string _clientId;
    private readonly Func<string> _readSecret;
    private readonly string _scope;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cached;
    private DateTimeOffset _expires;

    public OAuthClientCredentialsTokenSource(HttpClient http, Uri tokenEndpoint,
        string clientId, Func<string> readSecret, string scope)
    {
        if (tokenEndpoint.Scheme != Uri.UriSchemeHttps ||
            tokenEndpoint.UserInfo.Length > 0 ||
            tokenEndpoint.Query.Length > 0 || tokenEndpoint.Fragment.Length > 0 ||
            string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(scope) ||
            !scope.EndsWith("/.default", StringComparison.Ordinal))
            throw new ArgumentException("OAuth requires an HTTPS token endpoint and app-only scope.");
        _http = http;
        _tokenEndpoint = tokenEndpoint;
        _clientId = clientId;
        _readSecret = readSecret;
        _scope = scope;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_cached is not null && _expires > DateTimeOffset.UtcNow.AddMinutes(2))
            return _cached;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && _expires > DateTimeOffset.UtcNow.AddMinutes(2))
                return _cached;
            var secret = _readSecret();
            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException("M2M secret was not configured.");
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId, ["client_secret"] = secret,
                ["grant_type"] = "client_credentials", ["scope"] = _scope
            });
            using var response = await _http.PostAsync(_tokenEndpoint, form, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("OAuth token request denied.", null, response.StatusCode);
            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(
                cancellationToken: ct) ?? throw new InvalidOperationException("Invalid OAuth response.");
            var root = document.RootElement;
            if (!root.TryGetProperty("access_token", out var access) ||
                access.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(access.GetString()) ||
                !root.TryGetProperty("expires_in", out var expires) ||
                !expires.TryGetInt32(out var seconds) || seconds <= 0)
                throw new InvalidOperationException("Invalid OAuth response.");
            _cached = access.GetString();
            _expires = DateTimeOffset.UtcNow.AddSeconds(seconds);
            return _cached!;
        }
        finally { _gate.Release(); }
    }
}
