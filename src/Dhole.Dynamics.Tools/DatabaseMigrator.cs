using System.Security.Cryptography;
using Npgsql;

namespace Dhole.Dynamics.Tools;

public static class DatabaseMigrator
{
    private static readonly string[] Migrations = ["001_init.sql", "002_secret_rotation.sql", "003_api_access.sql", "004_metadata_catalog.sql", "005_saved_queries.sql", "006_generic_writes.sql", "007_integration_templates.sql", "008_admin_console.sql", "009_legacy_migration.sql", "010_durable_jobs.sql", "011_metadata_lookup_shadow_fields.sql", "012_saved_query_api_keys.sql", "013_integration_key_query_pin.sql", "014_event_automation_rules.sql", "015_template_api_keys.sql", "016_multi_query_integration_keys.sql", "017_integration_key_query_selection.sql"];

    public static async Task ApplyAsync(NpgsqlDataSource dataSource, string directory, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);
        // Advisory xact lock serializes concurrent migrators, including ledger initialization.
        await ExecuteAsync(conn, transaction, "SELECT pg_advisory_xact_lock(842713, 2002)", ct);
        await ExecuteAsync(conn, transaction, """
            CREATE SCHEMA IF NOT EXISTS dynamics;
            CREATE TABLE IF NOT EXISTS dynamics.schema_migrations (
                version varchar(64) PRIMARY KEY,
                sha256 char(64) NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now()
            );
            """, ct);

        foreach (var name in Migrations)
        {
            var contents = await File.ReadAllTextAsync(Path.Combine(directory, name), ct);
            var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contents)));
            await using var lookup = new NpgsqlCommand(
                "SELECT sha256 FROM dynamics.schema_migrations WHERE version = @name", conn, transaction);
            lookup.Parameters.AddWithValue("name", name);
            var previous = await lookup.ExecuteScalarAsync(ct) as string;
            if (previous is not null)
            {
                if (!string.Equals(previous, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Previously applied migration {name} has changed.");
                continue;
            }

            await ExecuteAsync(conn, transaction, contents, ct);
            await using var track = new NpgsqlCommand(
                "INSERT INTO dynamics.schema_migrations(version, sha256) VALUES (@name, @hash)", conn, transaction);
            track.Parameters.AddWithValue("name", name);
            track.Parameters.AddWithValue("hash", hash);
            await track.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, conn, tx);
        await command.ExecuteNonQueryAsync(ct);
    }
}
