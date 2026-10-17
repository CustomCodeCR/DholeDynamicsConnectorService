using Dhole.Dynamics.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using Npgsql;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

/// <summary>Real PostgreSQL grants for two independent org connections and a durable quota.</summary>
[Collection("ConnectorApiHost")]
public sealed class Phase03GrantTests
{
    [Fact]
    public async Task ClientCannotAccessAnotherConnectionOrExceedQuota()
    {
        var cs = Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if (cs is null) return; // Only runs against the disposable CI database.
        await using var db = NpgsqlDataSource.Create(cs);
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var code1 = "ci-" + id1.ToString("N");
        var code2 = "ci-" + id2.ToString("N");
        const string issuer = "https://login.microsoftonline.com/example/v2.0";
        const string scope = "create";
        var repo = new ClientAccessRepository(db);
        var clientId = Guid.NewGuid().ToString("D");
        await using var conn = await db.OpenConnectionAsync();
        try
        {
            foreach (var (id, code) in new[] { (id1, code1), (id2, code2) })
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO dynamics.connections
                    (id,code,dataverse_url,tenant_id,client_id,secret_nonce,secret_ciphertext,secret_tag,secret_key_version)
                    VALUES(@id,@code,'https://ci.crm.dynamics.com',@tenant,@client,@nonce,@cipher,@tag,1)
                    """, conn);
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("code", code);
                insert.Parameters.AddWithValue("tenant", Guid.NewGuid());
                insert.Parameters.AddWithValue("client", Guid.NewGuid());
                insert.Parameters.AddWithValue("nonce", new byte[12]);
                insert.Parameters.AddWithValue("cipher", new byte[] { 1 });
                insert.Parameters.AddWithValue("tag", new byte[16]);
                await insert.ExecuteNonQueryAsync();
            }
            var client = await repo.RegisterAsync(new ClientRegistrationRequest(issuer, clientId, "CI isolated client"),
                "tests", CancellationToken.None);
            var grant = await repo.UpsertGrantAsync(
                new GrantRequest(client.Id, code1, "quotes", scope, ["name"], 2), "tests", CancellationToken.None);
            Assert.NotNull(grant);

            Assert.Equal(GrantDecision.Allowed, await repo.AuthorizeAsync(
                issuer, clientId, code1, "quotes", scope, ["name"], CancellationToken.None));
            Assert.Equal(GrantDecision.Missing, await repo.AuthorizeAsync(
                issuer, clientId, code2, "quotes", scope, ["name"], CancellationToken.None));
            Assert.Equal(GrantDecision.Missing, await repo.AuthorizeAsync(
                issuer, "unknown-client", code1, "quotes", scope, ["name"], CancellationToken.None));
            Assert.Equal(GrantDecision.Missing, await repo.AuthorizeAsync(
                issuer, clientId, code1, "quotes", "read", ["name"], CancellationToken.None));
            Assert.Equal(GrantDecision.Missing, await repo.AuthorizeAsync(
                issuer, clientId, code1, "quotes", scope, ["name", "description"], CancellationToken.None));
            Assert.Equal(GrantDecision.Allowed, await repo.AuthorizeAsync(
                issuer, clientId, code1, "quotes", scope, ["name"], CancellationToken.None));
            Assert.Equal(GrantDecision.QuotaExceeded, await repo.AuthorizeAsync(
                issuer, clientId, code1, "quotes", scope, ["name"], CancellationToken.None));

            await repo.RegisterAsync(new ClientRegistrationRequest(issuer, clientId, "CI revoked", false),
                "tests", CancellationToken.None);
            Assert.Equal(GrantDecision.Missing, await repo.AuthorizeAsync(
                issuer, clientId, code1, "quotes", scope, ["name"], CancellationToken.None));
        }
        finally
        {
            // Delete client (cascades quota/grants), then each test-only connection.
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM dynamics.api_clients WHERE issuer=@issuer AND oauth_client_id=@oauth;
                DELETE FROM dynamics.connections WHERE id IN (@id1,@id2)
                """, conn);
            cleanup.Parameters.AddWithValue("issuer", issuer);
            cleanup.Parameters.AddWithValue("oauth", clientId);
            cleanup.Parameters.AddWithValue("id1", id1);
            cleanup.Parameters.AddWithValue("id2", id2);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
    [Fact]
    public async Task SignedJwtRequiresItsOwnConnectionGrantAndIsSubjectToQuota()
    {
        var cs = Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if (cs is null) return;

        const string issuer = "https://login.microsoftonline.com/phase3-tests/v2.0";
        const string audience = "api://phase3-test-resource";
        var code = "jwt-" + Guid.NewGuid().ToString("N");
        var otherCode = "jwt-" + Guid.NewGuid().ToString("N");
        var connectionId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var clientId = Guid.NewGuid().ToString("D");
        var oldConn = Environment.GetEnvironmentVariable("ConnectionStrings__Dynamics");
        var oldEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var oldKey = Environment.GetEnvironmentVariable("DYNAMICS_ENCRYPTION_KEY_BASE64");

        Environment.SetEnvironmentVariable("ConnectionStrings__Dynamics", cs);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("DYNAMICS_ENCRYPTION_KEY_BASE64",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        await using var db = NpgsqlDataSource.Create(cs);
        await using var conn = await db.OpenConnectionAsync();
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        try
        {
            foreach (var (id, name) in new[] { (connectionId, code), (otherId, otherCode) })
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO dynamics.connections
                    (id, code, dataverse_url, tenant_id, client_id, secret_nonce,
                     secret_ciphertext, secret_tag, secret_key_version)
                    VALUES (@id,@code,'https://ci.crm.dynamics.com',@tenant,@client,@nonce,@cipher,@tag,1)
                    """, conn);
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("code", name);
                insert.Parameters.AddWithValue("tenant", Guid.NewGuid());
                insert.Parameters.AddWithValue("client", Guid.NewGuid());
                insert.Parameters.AddWithValue("nonce", new byte[12]);
                insert.Parameters.AddWithValue("cipher", new byte[] { 3 });
                insert.Parameters.AddWithValue("tag", new byte[16]);
                await insert.ExecuteNonQueryAsync();
            }

            var registry = new ClientAccessRepository(db);
            var app = await registry.RegisterAsync(
                new ClientRegistrationRequest(issuer, clientId, "Signed JWT test"), "ci", CancellationToken.None);
            await registry.UpsertGrantAsync(new GrantRequest(
                app.Id, code, "quotes", "read", ["quoteid"], 2), "ci", CancellationToken.None);

            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                {
                    services.PostConfigure<JwtBearerOptions>(ServiceRegistration.MachineBearer, options =>
                    {
                        // Test-only RSA issuer: no external OAuth authority or production keys.
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true, ValidIssuer = issuer,
                            ValidateAudience = true, ValidAudience = audience,
                            ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey,
                            ValidateLifetime = true, RequireExpirationTime = true,
                            ClockSkew = TimeSpan.FromSeconds(2)
                        };
                    });
                }));
            using var http = factory.CreateClient();

            string Signed(string id, string tokenAudience = audience, bool incorrectKey = false)
            {
                var claims = new[]
                {
                    new Claim("azp", id), new Claim("idtyp", "app"),
                    new Claim("roles", "dynamics.quotes.read")
                };
                using var wrongRsa = incorrectKey ? RSA.Create(2048) : null;
                var key = incorrectKey ? new RsaSecurityKey(wrongRsa!) : signingKey;
                var token = new JwtSecurityToken(issuer, tokenAudience, claims,
                    DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
                    new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
                return new JwtSecurityTokenHandler().WriteToken(token);
            }

            async Task<System.Net.HttpStatusCode> Get(string token, string destination, bool fullQuote = false)
            {
                var path = fullQuote ? "/api/v1/quotes/" : "/api/v1/quotes/links/";
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    path + Guid.NewGuid().ToString("D") + "?connectionCode=" + destination);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await http.SendAsync(request);
                return response.StatusCode;
            }

            Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
                await Get(Signed(clientId, incorrectKey: true), code));
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
                await Get(Signed(clientId, "api://wrong-resource"), code));
            Assert.Equal(System.Net.HttpStatusCode.Forbidden,
                await Get(Signed(clientId), otherCode));
            Assert.Equal(System.Net.HttpStatusCode.Forbidden,
                await Get(Signed(clientId), code, fullQuote: true));
            Assert.Equal(System.Net.HttpStatusCode.NotFound,
                await Get(Signed(clientId), code));
            Assert.Equal(System.Net.HttpStatusCode.NotFound,
                await Get(Signed(clientId), code));
            Assert.Equal(System.Net.HttpStatusCode.TooManyRequests,
                await Get(Signed(clientId), code));
        }
        finally
        {
            await using var delete = new NpgsqlCommand("""
                DELETE FROM dynamics.api_clients WHERE issuer=@issuer AND oauth_client_id=@oauth;
                DELETE FROM dynamics.connections WHERE id IN (@first,@second)
                """, conn);
            delete.Parameters.AddWithValue("issuer", issuer);
            delete.Parameters.AddWithValue("oauth", clientId);
            delete.Parameters.AddWithValue("first", connectionId);
            delete.Parameters.AddWithValue("second", otherId);
            await delete.ExecuteNonQueryAsync();
            Environment.SetEnvironmentVariable("ConnectionStrings__Dynamics", oldConn);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", oldEnvironment);
            Environment.SetEnvironmentVariable("DYNAMICS_ENCRYPTION_KEY_BASE64", oldKey);
        }
    }

}
