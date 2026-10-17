using System.Collections.Concurrent;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Identity.Client;

namespace Dhole.Dynamics.Infrastructure;

/// <summary>Reuse MSAL confidential clients and their in-memory token caches per credential generation.</summary>
public interface IDataverseAccessTokenProvider
{
    Task<string> AcquireAsync(DynamicsConnection connection, CancellationToken ct);
}

public sealed class DataverseTokenProvider(ISecretProtector encryption) : IDataverseAccessTokenProvider
{
    private readonly MemoryCache _apps = new(new MemoryCacheOptions { SizeLimit = 128 });
    private readonly object _sync = new();

    public async Task<string> AcquireAsync(DynamicsConnection connection, CancellationToken ct)
    {
        if (!DataverseUrlValidator.TryNormalize(connection.DataverseUrl, out var host))
            throw new InvalidOperationException("Disallowed Dataverse URL.");
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            connection.SecretTag.Concat(connection.SecretCiphertext).ToArray()));
        var cacheKey = (connection.Id, connection.TenantId, connection.ClientId, fingerprint);
        if (!_apps.TryGetValue(cacheKey, out IConfidentialClientApplication? client))
        {
            lock (_sync)
            {
                if (!_apps.TryGetValue(cacheKey, out client))
                {
                    // MSAL owns an ephemeral managed credential copy; never log it.
                    var secret = encryption.Decrypt(connection);
                    client = ConfidentialClientApplicationBuilder.Create(connection.ClientId.ToString("D"))
                        .WithAuthority($"https://login.microsoftonline.com/{connection.TenantId:D}")
                        .WithClientSecret(secret).Build();
                    _apps.Set(cacheKey, client, new MemoryCacheEntryOptions()
                        .SetSize(1).SetSlidingExpiration(TimeSpan.FromMinutes(30)));
                }
            }
        }
        var result = await client!.AcquireTokenForClient([$"{host}/.default"]).ExecuteAsync(ct);
        return result.AccessToken;
    }
}
