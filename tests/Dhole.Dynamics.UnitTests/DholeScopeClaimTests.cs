using System.Security.Claims;
using Dhole.Dynamics.Api;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class DholeScopeClaimTests
{
    [Theory]
    [InlineData("scopes", "pricing.rate.view dynamics.security.admin", true)]
    [InlineData("scopes", "pricing.rate.view,dynamics.admin", true)]
    [InlineData("scp", "dynamics.admin", true)]
    [InlineData("scope", "dynamics.admin", true)]
    [InlineData("role", "dynamics.admin", true)]
    [InlineData("scopes", "dynamics.administrator", false)]
    [InlineData("scopes", "dynamics.admin.extra", false)]
    [InlineData("scopes", "pricing.rate.view", false)]
    public void Dhole_scopes_require_exact_permission(string claim, string value, bool allowed)
    {
        var identity = new ClaimsIdentity([new Claim(claim, value)], "DholeBearer");
        var user = new ClaimsPrincipal(identity);
        Assert.Equal(allowed, ScopeSecurity.HasScope(user, "dynamics.admin")
            || ScopeSecurity.HasScope(user, "dynamics.security.admin"));
    }

    [Fact]
    public void Anonymous_claims_do_not_grant_access()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scopes", "dynamics.admin")]));
        Assert.False(ScopeSecurity.HasScope(user, "dynamics.admin"));
    }
}
