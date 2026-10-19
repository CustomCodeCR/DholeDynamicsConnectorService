namespace Dhole.Dynamics.Api;

/// <summary>
/// Dhole integration: use only keys generated into the isolated Dynamics Docker volume.
/// No keys can be supplied by a caller, and Dhole's JWT signing key is never reused
/// for data encryption. Persist, restrict access to, and back up this volume.
/// </summary>
public static class DholeRuntimeKeys
{
    public static void Apply(IConfigurationManager configuration)
    {
        if (!string.Equals(configuration["DYNAMICS_AUTH_MODE"], "Dhole",
                StringComparison.OrdinalIgnoreCase))
            return;

        var directory = configuration["DYNAMICS_LOCAL_KEYS_DIR"];
        if (!string.Equals(directory, "/data/dynamics-keys", StringComparison.Ordinal))
            throw new InvalidOperationException("Dhole runtime requires the isolated keys volume.");

        static string ReadKey(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 1 or > 256 || info.LinkTarget is not null)
                throw new InvalidOperationException("Missing or invalid Dynamics runtime encryption key.");
            var content = File.ReadAllText(path).TrimEnd('\r', '\n');
            byte[] decoded;
            try { decoded = Convert.FromBase64String(content); }
            catch (FormatException) { throw new InvalidOperationException("Dynamics encryption key must be Base64."); }
            if (decoded.Length != 32)
                throw new InvalidOperationException("Dynamics runtime key must be 32 bytes.");
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(decoded);
            return content;
        }

        var secret = ReadKey(Path.Combine(directory, "encryption_key"));
        var cursor = ReadKey(Path.Combine(directory, "cursor_key"));
        if (string.Equals(secret, cursor, StringComparison.Ordinal))
            throw new InvalidOperationException("Encryption and cursor keys must be different.");
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DYNAMICS_ENCRYPTION_KEY_BASE64"] = secret,
            ["READ_CURSOR_KEY_BASE64"] = cursor
        });
    }
}
