using System.Security.Claims;
using Dhole.Dynamics.Api;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class DholeSuperUserAuthorizationTests
{
    [Theory]
    [InlineData("roles", "SuperUsuario", true)]
    [InlineData("role", "SuperUser", true)]
    [InlineData("roles", "Pricing,SuperUsuario", true)]
    [InlineData("role", "administrador", false)]
    [InlineData("role", "SuperUsuarioExtra", false)]
    [InlineData("role", "dynamics.admin", false)]
    public void Requires_exact_superuser_role_in_authenticated_token(string claimType, string role, bool expected)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(claimType, role)], "DholeBearer"));
        Assert.Equal(expected, ScopeSecurity.IsDholeSuperUser(principal));
    }

    [Fact]
    public void Anonymous_principal_is_not_superuser()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("roles", "SuperUsuario")]));
        Assert.False(ScopeSecurity.IsDholeSuperUser(principal));
    }
}
