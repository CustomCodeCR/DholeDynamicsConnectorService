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

    [Theory]
    [InlineData("scopes", "dynamics.security.admin", true)]
    [InlineData("role", "dynamics.security.admin", true)]
    [InlineData("scopes", "dynamics.admin", false)]
    [InlineData("scopes", "dynamics.security.admin.extra", false)]
    public void Admin_record_permission_accepts_only_exact_signed_scope_or_role(
        string claim, string value, bool expected)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(claim, value)], "DholeBearer"));
        Assert.Equal(expected, ScopeSecurity.HasAdminRole(principal, "dynamics.security.admin"));
    }

    [Fact]
    public void Admin_record_permission_accepts_authenticated_super_user()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "SuperUsuario")], "DholeBearer"));
        Assert.True(ScopeSecurity.HasAdminRole(principal, "dynamics.security.admin"));
    }

    [Fact]
    public void Admin_record_permission_rejects_anonymous_claim()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scopes", "dynamics.security.admin")]));
        Assert.False(ScopeSecurity.HasAdminRole(principal, "dynamics.security.admin"));
    }

}
