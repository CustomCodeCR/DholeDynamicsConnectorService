using System.Security.Cryptography;
using Dhole.Dynamics.Domain;
using Dhole.Dynamics.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class KeyRotationTests
{
    private static Encryption Protector(string? oldKey, string? json = null, string? active = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DYNAMICS_ENCRYPTION_KEY_BASE64"] = oldKey,
            ["DYNAMICS_ENCRYPTION_KEYS_JSON"] = json,
            ["DYNAMICS_ENCRYPTION_ACTIVE_KEY_VERSION"] = active,
        }).Build();
        return new Encryption(config);
    }

    private static DynamicsConnection Connection(Guid id, byte[] nonce, byte[] ciphertext, byte[] tag, int version)
        => new(id, "test", "https://example.crm.dynamics.com", Guid.NewGuid(), Guid.NewGuid(),
            null, nonce, ciphertext, tag, version, DateTimeOffset.UtcNow);

    [Fact]
    public void V1RemainsReadableAfterNewActiveVersionIsIntroduced()
    {
        var v1Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var v2Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var id = Guid.NewGuid();
        using var legacy = Protector(v1Key);
        var oldEncrypted = legacy.Encrypt(id, "sensitive-legacy-secret");
        using var upgraded = Protector(v1Key, "{\"2\":\"" + v2Key + "\"}", "2");
        var old = Connection(id, oldEncrypted.Nonce, oldEncrypted.Ciphertext, oldEncrypted.Tag, 1);
        Assert.Equal("sensitive-legacy-secret", upgraded.Decrypt(old));
        var rewrapped = upgraded.Reencrypt(old);
        var current = Connection(id, rewrapped.Nonce, rewrapped.Ciphertext, rewrapped.Tag, 2);
        Assert.Equal("sensitive-legacy-secret", upgraded.Decrypt(current));
        Assert.NotEqual(Convert.ToHexString(oldEncrypted.Ciphertext), Convert.ToHexString(rewrapped.Ciphertext));
        Assert.Equal(2, upgraded.KeyVersion);
    }

    [Fact]
    public void CorruptedCiphertextCannotBeDecrypted()
    {
        var k = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var protector = Protector(k);
        var id = Guid.NewGuid();
        var ciphertext = protector.Encrypt(id, "cannot-tamper");
        ciphertext.Tag[0] ^= 0xff;
        Assert.Throws<CryptographicException>(() => protector.Decrypt(
            Connection(id, ciphertext.Nonce, ciphertext.Ciphertext, ciphertext.Tag, 1)));
    }

    [Fact]
    public void MissingOldKeyFailsClosed()
    {
        var k1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var k2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var id = Guid.NewGuid();
        using var old = Protector(k1);
        var encrypted = old.Encrypt(id, "test");
        using var current = Protector(null, "{\"2\":\"" + k2 + "\"}", "2");
        Assert.Throws<InvalidOperationException>(() => current.Decrypt(
            Connection(id, encrypted.Nonce, encrypted.Ciphertext, encrypted.Tag, 1)));
    }

    [Fact]
    public void DifferentConnectionIdIsRejectedByAssociatedData()
    {
        var k = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var enc = Protector(k);
        var encrypted = enc.Encrypt(Guid.NewGuid(), "test");
        Assert.Throws<CryptographicException>(() => enc.Decrypt(
            Connection(Guid.NewGuid(), encrypted.Nonce, encrypted.Ciphertext, encrypted.Tag, 1)));
    }

    [Theory]
    [InlineData("Host=localhost;Database=dhole_pricing;Username=app;Password=x", true, false)]
    [InlineData("Host=localhost;Database=dhole_dynamics_connector;Username=app;Password=x", true, true)]
    [InlineData("Host=localhost;Database=dhole_dynamics_connector_test;Username=app;Password=x", true, true)]
    [InlineData("Host=localhost;Database=dhole_dynamics_connectorevil;Username=app;Password=x", true, false)]
    public void DatabaseNamesAreIsolated(string connection, bool dedicated, bool valid)
    {
        if (valid) Assert.Equal(connection, DatabaseIsolation.Validate(connection, dedicated));
        else Assert.Throws<InvalidOperationException>(() => DatabaseIsolation.Validate(connection, dedicated));
    }
}
