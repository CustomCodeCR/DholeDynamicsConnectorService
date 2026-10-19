using Dhole.Dynamics.Api;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class DholeGatewayRuntimeTests
{
    [Fact]
    public void Dhole_mode_requires_persistent_runtime_keys()
    {
        var config = new ConfigurationManager();
        config["DYNAMICS_AUTH_MODE"] = "Dhole";
        config["DYNAMICS_LOCAL_KEYS_DIR"] = "/wrong/path";
        Assert.Throws<InvalidOperationException>(() => DholeRuntimeKeys.Apply(config));
    }

    [Fact]
    public void Legacy_mode_keeps_existing_key_loading()
    {
        var config = new ConfigurationManager();
        DholeRuntimeKeys.Apply(config);
        Assert.Null(config["DYNAMICS_ENCRYPTION_KEY_BASE64"]);
    }
}
