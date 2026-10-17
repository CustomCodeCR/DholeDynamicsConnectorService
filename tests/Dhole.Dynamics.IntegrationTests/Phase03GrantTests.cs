using Dhole.Dynamics.Api;
using Npgsql;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

/// <summary>Real PostgreSQL grants for two independent org connections and a durable quota.</summary>
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
}
