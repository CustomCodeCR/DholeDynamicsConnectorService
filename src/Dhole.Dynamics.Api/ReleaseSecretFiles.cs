using System.Collections.Generic;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Allowlist-backed Docker secret files; no file paths from HTTP inputs.
/// Empty/missing files fail startup. Contents never appear in diagnostics.
/// </summary>
public static class ReleaseSecretFiles
{
    private static readonly IReadOnlyDictionary<string,string> Files =
        new Dictionary<string,string>(StringComparer.Ordinal)
        {
            ["DYNAMICS_CONNECTION_STRING_FILE"]="ConnectionStrings:Dynamics",
            ["DYNAMICS_ENCRYPTION_KEY_FILE"]="DYNAMICS_ENCRYPTION_KEY_BASE64",
            ["DYNAMICS_ENCRYPTION_KEYRING_FILE"]="DYNAMICS_ENCRYPTION_KEYS_JSON",
            ["DYNAMICS_CURSOR_KEY_FILE"]="READ_CURSOR_KEY_BASE64",
            ["ADMIN_OIDC_CLIENT_SECRET_FILE"]="AdminOidc:ClientSecret",
            ["DYNAMICS_DP_CERT_PASSWORD_FILE"]="DataProtection:CertificatePassword"
        };

    public static IDictionary<string,string?> Read(IReadOnlyDictionary<string,string?> environment)
    {
        var values=new Dictionary<string,string?>(StringComparer.Ordinal);
        foreach(var pair in Files)
        {
            if(!environment.TryGetValue(pair.Key,out var path)||string.IsNullOrWhiteSpace(path))
                continue;
            if(!Path.IsPathFullyQualified(path) ||
                !path.StartsWith("/run/secrets/",StringComparison.Ordinal) ||
                path.Length>240 || path.Contains("..",StringComparison.Ordinal))
                throw new InvalidOperationException("Release secret path must be a named Docker secret.");
            var info=new FileInfo(path);
            if(!info.Exists || info.Length is < 1 or > 8192)
                throw new InvalidOperationException("Required release secret is missing or invalid.");
            var raw=File.ReadAllText(path).TrimEnd('\r','\n');
            if(string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException("Required release secret is empty.");
            values[pair.Value]=raw;
        }
        return values;
    }

    public static void Apply(IConfigurationManager configuration)
    {
        var names=Files.Keys.ToDictionary(k=>k,k=>(string?)Environment.GetEnvironmentVariable(k));
        var loaded=Read(names);
        if(loaded.Count>0)configuration.AddInMemoryCollection(loaded);
    }
}
