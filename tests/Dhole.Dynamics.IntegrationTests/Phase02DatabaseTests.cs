using System.Security.Cryptography;
using Dhole.Dynamics.Infrastructure;
using Dhole.Dynamics.Tools;
using Dhole.Dynamics.Domain;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

/// <summary>Runs against disposable CI PostgreSQL only when an explicit connection is provided.</summary>
[Collection("ConnectorApiHost")]
public sealed class Phase02DatabaseTests
{
    private static string? TestDatabase => Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");

    [Fact]
    public async Task MigrationsAreReplayableAndTamperEvident()
    {
        if (TestDatabase is null) return; // CI always supplies PHASE2_TEST_DB_CONNECTION.
        await using var db = NpgsqlDataSource.Create(DatabaseIsolation.Validate(TestDatabase, true));
        var directory = Environment.GetEnvironmentVariable("PHASE2_SQL_DIR")
            ?? throw new InvalidOperationException("Phase 02 SQL directory must be provided.");
        await DatabaseMigrator.ApplyAsync(db, directory);
        await DatabaseMigrator.ApplyAsync(db, directory);
        await using var cn = await db.OpenConnectionAsync();
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM dynamics.schema_migrations", cn))
            Assert.Equal(3L, (long)(await count.ExecuteScalarAsync())!);

        var temp = Path.Combine(Path.GetTempPath(), "phase2-migrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var f in Directory.GetFiles(directory, "*.sql"))
                File.Copy(f, Path.Combine(temp, Path.GetFileName(f)));
            await File.AppendAllTextAsync(Path.Combine(temp, "002_secret_rotation.sql"), "\n-- accidental mutation\n");
            await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseMigrator.ApplyAsync(db, temp));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task OldEncryptedRowsAreRewrappedWithoutDataLossAndAudited()
    {
        if (TestDatabase is null) return;
        await using var db = NpgsqlDataSource.Create(DatabaseIsolation.Validate(TestDatabase, true));
        var firstKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var nextKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var v1 = new Encryption(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DYNAMICS_ENCRYPTION_KEY_BASE64"] = firstKey }).Build());
        using var v2 = new Encryption(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DYNAMICS_ENCRYPTION_KEY_BASE64"] = firstKey,
                ["DYNAMICS_ENCRYPTION_KEYS_JSON"] = "{\"2\":\"" + nextKey + "\"}",
                ["DYNAMICS_ENCRYPTION_ACTIVE_KEY_VERSION"] = "2"
            }).Build());

        var id = Guid.NewGuid();
        const string original = "phase2-fixture-encrypted-secret";
        var encrypted = v1.Encrypt(id, original);
        await using var conn = await db.OpenConnectionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO dynamics.connections
                (id, code, dataverse_url, tenant_id, client_id, secret_nonce,
                 secret_ciphertext, secret_tag, secret_key_version)
            VALUES (@id,@code,'https://sandbox.crm.dynamics.com',@tenant,@client,
                    @nonce,@cipher,@tag,1)
            """, conn))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("code", "phase2-" + id.ToString("N"));
            insert.Parameters.AddWithValue("tenant", Guid.NewGuid());
            insert.Parameters.AddWithValue("client", Guid.NewGuid());
            insert.Parameters.AddWithValue("nonce", encrypted.Nonce);
            insert.Parameters.AddWithValue("cipher", encrypted.Ciphertext);
            insert.Parameters.AddWithValue("tag", encrypted.Tag);
            await insert.ExecuteNonQueryAsync();
        }

        try
        {
            var dry = await KeyRotation.RunAsync(db, v2, false);
            Assert.True(dry.Pending >= 1);
            Assert.Equal(0, dry.Rotated);
            var applied = await KeyRotation.RunAsync(db, v2, true);
            Assert.True(applied.Rotated >= 1);
            Assert.Equal(0, (await KeyRotation.RunAsync(db, v2, false)).Pending);

            await using var read = new NpgsqlCommand("""
                SELECT secret_nonce, secret_ciphertext, secret_tag, secret_key_version
                FROM dynamics.connections WHERE id = @id
                """, conn);
            read.Parameters.AddWithValue("id", id);
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var actual = new DynamicsConnection(id, "test", "https://sandbox.crm.dynamics.com",
                Guid.Empty, Guid.Empty, null,
                (byte[])reader.GetValue(0), (byte[])reader.GetValue(1),
                (byte[])reader.GetValue(2), reader.GetInt32(3), DateTimeOffset.UtcNow);
            Assert.Equal(2, actual.SecretKeyVersion);
            Assert.Equal(original, v2.Decrypt(actual));
            await reader.CloseAsync();
            await using var audit = new NpgsqlCommand("""
                SELECT count(*) FROM dynamics.audit_events
                WHERE connection_id=@id AND action='connection.key.reencrypt'
                """, conn);
            audit.Parameters.AddWithValue("id", id);
            Assert.Equal(1L, (long)(await audit.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var delete = new NpgsqlCommand(
                "DELETE FROM dynamics.connections WHERE id=@id", conn);
            delete.Parameters.AddWithValue("id", id);
            // Audit has ON DELETE SET NULL for connection_id.
            await delete.ExecuteNonQueryAsync();
        }
    }
}
