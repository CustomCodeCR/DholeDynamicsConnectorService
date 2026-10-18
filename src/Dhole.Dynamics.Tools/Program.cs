using System.Security.Cryptography;
using Dhole.Dynamics.Infrastructure;
using Dhole.Dynamics.Tools;
using Microsoft.Extensions.Configuration;
using Npgsql;

if (args.Length is < 1 or > 2 || args[0] is not ("migrate" or "rotate-keys")
    || (args.Length == 2 && args[1] is not ("--dry-run" or "--apply")))
{
    Console.Error.WriteLine("Usage: connector-tools migrate | rotate-keys [--dry-run|--apply]");
    return 2;
}

try
{
    var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    var file=Environment.GetEnvironmentVariable("DYNAMICS_CONNECTION_STRING_FILE");
    string? connectionValue=config["ConnectionStrings:Dynamics"];
    if(!string.IsNullOrEmpty(file))
    {
        if(!file.StartsWith("/run/secrets/",StringComparison.Ordinal) ||
            file.Contains("..",StringComparison.Ordinal) || !File.Exists(file) ||
            new FileInfo(file).Length is < 1 or > 8192)
            throw new InvalidOperationException("Invalid release database secret file.");
        connectionValue=(await File.ReadAllTextAsync(file)).TrimEnd('\r','\n');
    }
    var connectionString = DatabaseIsolation.Validate(
        connectionValue ?? throw new InvalidOperationException("Missing connector DB connection."),
        requireDedicatedName: true);
    await using var database = new NpgsqlDataSourceBuilder(connectionString).Build();
    if (args[0] == "migrate")
    {
        if (args.Length != 1)
            throw new InvalidOperationException("The migration command does not accept write mode flags.");
        await DatabaseMigrator.ApplyAsync(database, Path.Combine(AppContext.BaseDirectory, "migrations"));
        Console.WriteLine("Migrations validated and applied transactionally.");
    }
    else
    {
        var applying = args.Length == 2 && args[1] == "--apply";
        if (applying && config["DYNAMICS_ROTATION_APPROVED"] != "YES")
            throw new InvalidOperationException("Rotation requires DYNAMICS_ROTATION_APPROVED=YES after a verified backup.");
        using var encryption = new Encryption(config);
        var result = await KeyRotation.RunAsync(database, encryption, applying);
        Console.WriteLine($"Key rotation: target={encryption.KeyVersion}, pending={result.Pending}, rotated={result.Rotated}, mode={(applying ? "APPLIED" : "DRY-RUN")}.");
    }
    return 0;
}
catch (PostgresException e)
{
    // SQLSTATE and constraint identifier are safe structural diagnostics; never print secrets.
    Console.Error.WriteLine($"Connector migration rejected by PostgreSQL, SQLSTATE={e.SqlState}, constraint={e.ConstraintName ?? "none"}.");
    return 1;
}
catch (Exception e)
{
    // Never print exception messages here: Npgsql and upstream exceptions can include connection details.
    Console.Error.WriteLine($"Connector maintenance command failed ({e.GetType().Name}). See restricted operational logs.");
    return 1;
}
