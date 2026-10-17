using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

public sealed class ApiSmokeTests
{
    private static WebApplicationFactory<Program> Factory()
    {
        // Set before WebApplication.CreateBuilder runs. WebApplicationFactory's
        // ConfigureAppConfiguration callback happens too late for top-level required settings.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("ConnectionStrings__Dynamics",
            "Host=127.0.0.1;Port=1;Timeout=1;Database=dhole_dynamics_connector_test;Username=tests;Password=notused");
        Environment.SetEnvironmentVariable("DYNAMICS_ENCRYPTION_KEY_BASE64",
            Convert.ToBase64String(new byte[32]));
        return new WebApplicationFactory<Program>();
    }

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

    [Fact]
    public async Task AnonymousM2MEndpointIsProtected()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/quotes/" + Guid.NewGuid() + "?connectionCode=test");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MachineTokenCannotLogIntoAdminWhenProviderNotConfigured()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/auth/login");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
