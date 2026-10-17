using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

public sealed class ApiSmokeTests
{
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "phase1-tests",
                ["Jwt:Audience"] = "phase1-tests",
                ["Jwt:SecretKey"] = new string('s', 64),
                ["ConnectionStrings:Dynamics"] = "Host=localhost;Database=notused;Username=tests;Password=notused",
                ["DYNAMICS_ENCRYPTION_KEY_BASE64"] = Convert.ToBase64String(new byte[32])
            });
        }));

    [Fact]
    public async Task LivenessAndCorrelationDoNotRequireDatabase()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/live");
        Assert.True(response.IsSuccessStatusCode);
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
    }

    [Fact]
    public async Task ReadinessFailsClosedWithoutDatabase()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousAdminEndpointIsProtected()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/admin/v1/connections");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
