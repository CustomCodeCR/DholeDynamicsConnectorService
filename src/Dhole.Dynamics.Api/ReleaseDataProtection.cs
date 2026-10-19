using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace Dhole.Dynamics.Api;

/// <summary>Persist signed/encrypted auth cookie keys in a dedicated release-only volume.</summary>
public static class ReleaseDataProtection
{
    public static void Configure(IServiceCollection services,IConfiguration config,IHostEnvironment env)
    {
        if(env.IsDevelopment() || env.IsEnvironment("Testing") ||
           string.Equals(config["DYNAMICS_AUTH_MODE"], "Dhole", StringComparison.OrdinalIgnoreCase)) return;
        var path=config["DataProtection:KeyPath"];
        var certPath=config["DataProtection:CertificatePath"];
        var password=config["DataProtection:CertificatePassword"];
        if(string.IsNullOrWhiteSpace(path)||string.IsNullOrWhiteSpace(certPath)||
           string.IsNullOrEmpty(password)||!Path.IsPathFullyQualified(path)||
           !certPath.StartsWith("/run/secrets/",StringComparison.Ordinal))
            throw new InvalidOperationException("Release DataProtection requires a persisted key volume and encrypted certificate.");
        var pfx=X509CertificateLoader.LoadPkcs12FromFile(certPath,password,
            X509KeyStorageFlags.EphemeralKeySet);
        if(!pfx.HasPrivateKey)throw new InvalidOperationException("Release DataProtection requires a private key.");
        Directory.CreateDirectory(path);
        services.AddDataProtection()
            .SetApplicationName("DholeDynamicsConnectorService")
            .PersistKeysToFileSystem(new DirectoryInfo(path))
            .ProtectKeysWithCertificate(pfx);
    }
}
