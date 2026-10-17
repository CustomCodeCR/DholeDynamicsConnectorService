using Dhole.Dynamics.Infrastructure;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class UrlValidationTests
{
    [Theory]
    [InlineData("https://example.crm.dynamics.com", true)]
    [InlineData("https://example.crm.dynamics.com/", true)]
    [InlineData("http://example.crm.dynamics.com", false)]
    [InlineData("https://example.crm.dynamics.com.attacker.org", false)]
    [InlineData("https://example.crm.dynamics.com:8443", false)]
    [InlineData("https://user@example.crm.dynamics.com", false)]
    [InlineData("https://example.crm.dynamics.com?callback=x", false)]
    public void OnlyKnownHttpsDataverseUrlsAreAllowed(string value, bool expected)
    {
        var accepted = DataverseUrlValidator.TryNormalize(value, out var url);
        Assert.Equal(expected, accepted);
        if (accepted) Assert.StartsWith("https://", url);
    }
}
