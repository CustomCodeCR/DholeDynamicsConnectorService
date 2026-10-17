using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Microsoft.Extensions.Hosting;

namespace Dhole.Dynamics.Api;

public sealed record ReadCursor(
    Guid ConnectionId, string Issuer, string ClientId, string Entity, int MetadataVersion,
    ReadSpec Spec, string NextLink, int Page, DateTimeOffset ExpiresAt, string? SavedName);

/// <summary>Authenticated/encrypted opaque cursors bound to principal, connection and metadata snapshot.</summary>
public sealed class ReadCursorProtector : IDisposable
{
    private readonly byte[] key;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Aad = Encoding.ASCII.GetBytes("dhole-dynamics:read-cursor:v1");

    public ReadCursorProtector(IConfiguration config, IHostEnvironment environment)
    {
        var supplied = config["READ_CURSOR_KEY_BASE64"];
        if (string.IsNullOrWhiteSpace(supplied) && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            throw new InvalidOperationException("READ_CURSOR_KEY_BASE64 must be supplied by secret manager.");
        try { key = string.IsNullOrWhiteSpace(supplied) ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(supplied); }
        catch (FormatException) { throw new InvalidOperationException("Cursor key must be Base64."); }
        if (key.Length != 32) throw new InvalidOperationException("Cursor key must be 32 bytes.");
    }

    public string Seal(ReadCursor payload)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(payload, Options);
        if (clear.Length > 12000) throw new ReadRejected(400, "cursor_too_large");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[clear.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, clear, cipher, tag, Aad);
            var output = new byte[nonce.Length + tag.Length + cipher.Length];
            Buffer.BlockCopy(nonce, 0, output, 0, 12);
            Buffer.BlockCopy(tag, 0, output, 12, 16);
            Buffer.BlockCopy(cipher, 0, output, 28, cipher.Length);
            return Convert.ToBase64String(output).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public ReadCursor Open(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 17000)
            throw new ReadRejected(400, "invalid_cursor");
        byte[]? clear = null;
        try
        {
            var value = token.Replace('-', '+').Replace('_', '/');
            value = value.PadRight((value.Length + 3) / 4 * 4, '=');
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length < 29 || bytes.Length > 13000) throw new ReadRejected(400, "invalid_cursor");
            clear = new byte[bytes.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), clear, Aad);
            var cursor = JsonSerializer.Deserialize<ReadCursor>(clear, Options);
            if (cursor is null || cursor.Page is < 2 or > ReadQueryCompiler.MaxPages
                || cursor.ExpiresAt < DateTimeOffset.UtcNow || cursor.NextLink.Length > 12000
                || cursor.ConnectionId == Guid.Empty || cursor.Spec is null)
                throw new ReadRejected(400, "invalid_cursor");
            return cursor;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
        {
            throw new ReadRejected(400, "invalid_cursor");
        }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}
