using Npgsql;

namespace Dhole.Dynamics.Infrastructure;

/// <summary>Rejects accidental connections to Dhole's databases, including lookalike names.</summary>
public static class DatabaseIsolation
{
    public const string DatabasePrefix = "dhole_dynamics_connector";

    public static string Validate(string connectionString, bool requireDedicatedName)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Dedicated connector database connection is required.");

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var name = builder.Database ?? string.Empty;
        if (requireDedicatedName && name != DatabasePrefix &&
            !name.StartsWith(DatabasePrefix + "_", StringComparison.Ordinal))
            throw new InvalidOperationException("Database must use the connector-specific name.");

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(builder.Host))
            throw new InvalidOperationException("Database and host are required.");

        // Avoid accidental leakage of PostgreSQL connection passwords in exceptions.
        return connectionString;
    }
}
