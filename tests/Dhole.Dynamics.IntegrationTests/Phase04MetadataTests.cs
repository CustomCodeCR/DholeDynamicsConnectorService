using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Npgsql;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

[Collection("ConnectorApiHost")]
public sealed class Phase04MetadataTests
{
    private static MetadataCatalog Fixture(bool writable, bool newTable)
    {
        var tables = new List<MetadataTable>
        {
            new("account", "accounts", "accountid", "name", false,
                new Dictionary<int,string>(),
                [new MetadataColumn("name", "String", true, writable, writable, !writable,
                    "ApplicationRequired", null, null, [], new Dictionary<int,string>(), [])], [])
        };
        if (newTable)
            tables.Add(new("new_shipment", "new_shipments", "new_shipmentid", "new_name", true,
                new Dictionary<int,string>(),
                [new MetadataColumn("new_name", "String", true, true, true, false,
                    null, null, null, [], new Dictionary<int,string>(), [])], []));
        return new MetadataCatalog(tables);
    }

    [Fact]
    public async Task NewEntityIsDisabledUntilExplicitGrantAndSchemaDriftRevokesWrite()
    {
        var cs = Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if (cs is null) return;
        await using var db = NpgsqlDataSource.Create(cs);
        var id = Guid.NewGuid();
        await using var conn = await db.OpenConnectionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO dynamics.connections
              (id,code,dataverse_url,tenant_id,client_id,secret_nonce,secret_ciphertext,secret_tag,secret_key_version)
            VALUES (@id,@code,'https://ci.crm.dynamics.com',@tenant,@client,@nonce,@cipher,@tag,1)
            """, conn))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("code", "phase04-" + id.ToString("N"));
            insert.Parameters.AddWithValue("tenant", Guid.NewGuid());
            insert.Parameters.AddWithValue("client", Guid.NewGuid());
            insert.Parameters.AddWithValue("nonce", new byte[12]);
            insert.Parameters.AddWithValue("cipher", new byte[] {4});
            insert.Parameters.AddWithValue("tag", new byte[16]);
            await insert.ExecuteNonQueryAsync();
        }
        try
        {
            var catalog = new MetadataRepository(db);
            var first = await catalog.SaveAsync(id, Fixture(true, false), "manual", "ci", CancellationToken.None);
            Assert.Equal(1, first.Snapshot.Version);
            var replay = await catalog.SaveAsync(id, Fixture(true, false), "manual", "ci", CancellationToken.None);
            Assert.True(replay.Diff.Unchanged);
            Assert.Equal(1, replay.Snapshot.Version);
            var discovered = await catalog.SaveAsync(id, Fixture(true, true), "manual", "ci", CancellationToken.None);
            Assert.Contains("new_shipment", discovered.Diff.AddedEntities);
            Assert.Empty((await catalog.PoliciesAsync(id, CancellationToken.None)).Entities);
            Assert.False(await catalog.SetFieldAsync(id, "new_shipment", "new_name",
                new MetadataFieldToggle(true,true,true,false), "ci", CancellationToken.None));
            Assert.True(await catalog.SetEntityAsync(id, "new_shipment", true, "ci", CancellationToken.None));
            Assert.True(await catalog.SetFieldAsync(id, "new_shipment", "new_name",
                new MetadataFieldToggle(true,true,true,false), "ci", CancellationToken.None));
            Assert.True(await catalog.SetEntityAsync(id, "account", true, "ci", CancellationToken.None));
            Assert.True(await catalog.SetFieldAsync(id, "account", "name",
                new MetadataFieldToggle(true,true,true,false), "ci", CancellationToken.None));
            var changed = await catalog.SaveAsync(id, Fixture(false, true), "manual", "ci", CancellationToken.None);
            Assert.Contains("account.name", changed.Diff.ChangedFields);
            var fields = (await catalog.PoliciesAsync(id, CancellationToken.None)).Fields;
            Assert.False(fields.Single(f=>f.EntityName=="account").Enabled);
            Assert.True(fields.Single(f=>f.EntityName=="new_shipment").Enabled);
            Assert.False(await catalog.SetFieldAsync(id, "account", "name",
                new MetadataFieldToggle(true,true,true,false), "ci", CancellationToken.None));
            Assert.True(await catalog.SetFieldAsync(id, "account", "name",
                new MetadataFieldToggle(true,true,false,false), "ci", CancellationToken.None));
            // Scheduled work is opt-in per connection, claimed once even with multiple app instances.
            await catalog.SetScheduleAsync(id, new MetadataSchedule(true, 60, null), "ci", CancellationToken.None);
            Assert.True((await catalog.GetScheduleAsync(id, CancellationToken.None)).Enabled);
            Assert.DoesNotContain("phase04-" + id.ToString("N"),
                await catalog.ClaimDueAsync(CancellationToken.None));
            await using (var makeDue = new NpgsqlCommand("""
                UPDATE dynamics.metadata_refresh_schedules
                SET next_run_at=now() - interval '1 minute' WHERE connection_id=@id
                """, conn))
            {
                makeDue.Parameters.AddWithValue("id", id);
                await makeDue.ExecuteNonQueryAsync();
            }
            Assert.Contains("phase04-" + id.ToString("N"),
                await catalog.ClaimDueAsync(CancellationToken.None));
            Assert.DoesNotContain("phase04-" + id.ToString("N"),
                await catalog.ClaimDueAsync(CancellationToken.None));

            var removed = await catalog.SaveAsync(id, Fixture(false, false), "manual", "ci", CancellationToken.None);
            Assert.Contains("new_shipment", removed.Diff.RemovedEntities);
            Assert.False((await catalog.PoliciesAsync(id, CancellationToken.None)).Entities
                .Single(e=>e.LogicalName=="new_shipment").Enabled);
            Assert.Equal(4, (await catalog.LatestAsync(id, CancellationToken.None))!.Version);
            Assert.Contains("new_shipment", (await catalog.CompareLatestAsync(id, CancellationToken.None))!.RemovedEntities);
        }
        finally
        {
            await using var del = new NpgsqlCommand("DELETE FROM dynamics.connections WHERE id=@id", conn);
            del.Parameters.AddWithValue("id", id);
            await del.ExecuteNonQueryAsync();
        }
    }
}
