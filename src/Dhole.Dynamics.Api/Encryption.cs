using System.Security.Cryptography;
using System.Text;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Encrypts application client secrets using an externally supplied 256-bit master key.
/// Encryption keys MUST NOT be committed or stored in the integration database.
/// </summary>
public sealed class Encryption
{
    private readonly byte[] _key;
    public const int CurrentVersion = 1;

    public Encryption(IConfiguration config)
    {
        var encoded = config["DYNAMICS_ENCRYPTION_KEY_BASE64"];
        if (string.IsNullOrWhiteSpace(encoded))
            throw new InvalidOperationException("DYNAMICS_ENCRYPTION_KEY_BASE64 must be supplied via server secrets.");
        try { _key = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new InvalidOperationException("Encryption key must be Base64."); }
        if (_key.Length != 32)
            throw new InvalidOperationException("Encryption key must contain 32 random bytes.");
    }

    public (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(Guid connectionId, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AdditionalData(connectionId));
        CryptographicOperations.ZeroMemory(plaintext);
        return (nonce, ciphertext, tag);
    }

    public string Decrypt(DynamicsConnection connection)
    {
        if (connection.SecretKeyVersion != CurrentVersion)
            throw new InvalidOperationException("Secret requires key rotation or migration.");
        var plaintext = new byte[connection.SecretCiphertext.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(
            connection.SecretNonce, connection.SecretCiphertext, connection.SecretTag,
            plaintext, AdditionalData(connection.Id));
        var result = Encoding.UTF8.GetString(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        return result;
    }

    private static byte[] AdditionalData(Guid id) =>
        Encoding.UTF8.GetBytes($"dhole-dynamics:client-secret:{id:D}:v1");
}
