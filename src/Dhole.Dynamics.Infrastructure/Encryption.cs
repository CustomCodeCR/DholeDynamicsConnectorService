using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Domain;
using Microsoft.Extensions.Configuration;

namespace Dhole.Dynamics.Infrastructure;

/// <summary>
/// Versioned AES-256-GCM keyring. Keys are supplied outside Git and PostgreSQL.
/// V1 AAD is byte-for-byte compatible with the original Phase 01 encryptor.
/// </summary>
public sealed class Encryption : ISecretProtector, IDisposable
{
    private readonly IReadOnlyDictionary<int, byte[]> _keys;
    public int KeyVersion { get; }

    public Encryption(IConfiguration config)
    {
        var keys = new Dictionary<int, byte[]>();
        var keyringJson = config["DYNAMICS_ENCRYPTION_KEYS_JSON"];
        if (!string.IsNullOrWhiteSpace(keyringJson))
        {
            try
            {
                using var document = JsonDocument.Parse(keyringJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Keyring must be a JSON object.");
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                        || version < 1 || version > 65535 || !keys.TryAdd(version, DecodeKey(property.Value.GetString())))
                        throw new InvalidOperationException("Invalid or duplicate encryption key version.");
                }
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("Encryption keyring is not valid JSON.");
            }
        }

        // Legacy single-key installation stays readable during migration to the keyring.
        var legacy = config["DYNAMICS_ENCRYPTION_KEY_BASE64"];
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            var legacyKey = DecodeKey(legacy);
            if (!keys.TryAdd(1, legacyKey))
            {
                if (!CryptographicOperations.FixedTimeEquals(keys[1], legacyKey))
                    throw new InvalidOperationException("Conflicting encryption version 1 keys.");
                CryptographicOperations.ZeroMemory(legacyKey);
            }
        }

        if (keys.Count == 0)
            throw new InvalidOperationException("An external encryption key or keyring is required.");
        if (!int.TryParse(config["DYNAMICS_ENCRYPTION_ACTIVE_KEY_VERSION"] ?? "1",
                NumberStyles.None, CultureInfo.InvariantCulture, out var active)
            || !keys.ContainsKey(active))
            throw new InvalidOperationException("Active encryption key version is not configured.");
        KeyVersion = active;
        _keys = keys;
    }

    public (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(Guid connectionId, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var plaintext = Encoding.UTF8.GetBytes(value);
        try { return EncryptBytes(connectionId, plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private (byte[] Nonce, byte[] Ciphertext, byte[] Tag) EncryptBytes(Guid id, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_keys[KeyVersion], 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AdditionalData(id, KeyVersion));
        return (nonce, ciphertext, tag);
    }

    public string Decrypt(DynamicsConnection connection)
    {
        var plaintext = DecryptBytes(connection);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <summary>Rewraps a secret without materializing another plaintext string.</summary>
    public (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Reencrypt(DynamicsConnection connection)
    {
        var plaintext = DecryptBytes(connection);
        try { return EncryptBytes(connection.Id, plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private byte[] DecryptBytes(DynamicsConnection connection)
    {
        if (!_keys.TryGetValue(connection.SecretKeyVersion, out var key))
            throw new InvalidOperationException("Encryption key version unavailable; restore the key before retry.");
        var plaintext = new byte[connection.SecretCiphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(connection.SecretNonce, connection.SecretCiphertext, connection.SecretTag,
                plaintext, AdditionalData(connection.Id, connection.SecretKeyVersion));
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    private static byte[] DecodeKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException("Encryption key cannot be empty.");
        byte[] key;
        try { key = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new InvalidOperationException("Encryption key must be Base64."); }
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("Encryption key must be 32 bytes.");
        }
        return key;
    }

    private static byte[] AdditionalData(Guid id, int version) =>
        Encoding.UTF8.GetBytes($"dhole-dynamics:client-secret:{id:D}:v{version}");

    public void Dispose()
    {
        foreach (var key in _keys.Values) CryptographicOperations.ZeroMemory(key);
    }
}
