using System.Security.Claims;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Infrastructure;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class Phase03SecurityTests
{
    private static ClaimsPrincipal App(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "signed-token"));

    [Fact]
    public void ScopeFromSignedRoleAndFieldsAreBothRequired()
    {
        var user = App(new Claim("roles", "dynamics.quotes.create"));
        Assert.True(GrantRules.IsAllowed(user, "dynamics.quotes.create",
            ["name", "new_preestado"], ["name"]));
        Assert.False(GrantRules.IsAllowed(user, "dynamics.quotes.create",
            ["name"], ["name", "new_preestado"]));
        Assert.False(GrantRules.IsAllowed(user, "dynamics.quotes.read",
            ["name"], ["name"]));
        Assert.False(GrantRules.IsAllowed(user, "dynamics.quotes.create",
            ["*"], ["name"]));
    }

    [Fact]
    public void CookieOrUntrustedPermissionClaimsDoNotGrantMachineScopes()
    {
        var user = App(new Claim("permission", "dynamics.quotes.create"),
            new Claim("permissions", "dynamics.quotes.create"));
        Assert.False(ScopeSecurity.HasScope(user, "dynamics.quotes.create"));
        Assert.Null(ScopeSecurity.ClientId(user));
    }

    [Fact]
    public void OnlyAdminRoleGrantsAdministrativePrivilege()
    {
        var machine = App(new Claim("roles", "dynamics.quotes.create"));
        Assert.False(ScopeSecurity.HasAdminRole(machine, "dynamics.admin"));
        var admin = App(new Claim("roles", "dynamics.admin"));
        Assert.True(ScopeSecurity.HasAdminRole(admin, "dynamics.admin"));
        Assert.False(ScopeSecurity.HasAdminRole(admin, "dynamics.security.admin"));
    }

    [Fact]
    public void RejectWildcardGrantsAndUnsupportedEntitiesOrOperations()
    {
        Assert.False(GrantRules.ValidGrant("accounts", "read", ["name"], 10));
        Assert.False(GrantRules.ValidGrant("quotes", "delete", ["name"], 10));
        Assert.False(GrantRules.ValidGrant("quotes", "create", ["*"], 10));
        Assert.False(GrantRules.ValidGrant("quotes", "create", ["name"], 601));
        Assert.True(GrantRules.ValidGrant("quotes", "create",
            ["name", "customerid_account@odata.bind"], 15));
    }

    [Theory]
    [InlineData("https://x.crm.dynamics.com", true)]
    [InlineData("https://x.crm4.dynamics.com", true)]
    [InlineData("https://localhost.dynamics.com", false)]
    [InlineData("https://x.crm.dynamics.com/a", false)]
    [InlineData("https://x.crm.dynamics.com.attacker.test", false)]
    [InlineData("http://x.crm.dynamics.com", false)]
    [InlineData("https://x.crm.dynamics.com:8443", false)]
    public void DataverseHostsAreStrictlyScoped(string host, bool expected) =>
        Assert.Equal(expected, DataverseUrlValidator.TryNormalize(host, out _));
}
