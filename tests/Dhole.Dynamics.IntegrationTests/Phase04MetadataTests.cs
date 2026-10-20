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
    [Fact]
    public async Task BulkEnableApprovesHundredsAtomicallyAndAllowsReadOnlyLookupShadows()
    {
        var cs = Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if (cs is null) return;
        await using var data = NpgsqlDataSource.Create(cs);
        var id = Guid.NewGuid();
        await using var connection = await data.OpenConnectionAsync();
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.connections
              (id,code,dataverse_url,tenant_id,client_id,secret_nonce,secret_ciphertext,secret_tag,secret_key_version)
            VALUES(@id,@code,'https://ci.crm.dynamics.com',@tenant,@client,@nonce,@cipher,@tag,1)
            """, connection))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("code", "bulk-" + id.ToString("N"));
            cmd.Parameters.AddWithValue("tenant", Guid.NewGuid());
            cmd.Parameters.AddWithValue("client", Guid.NewGuid());
            cmd.Parameters.AddWithValue("nonce", new byte[12]);
            cmd.Parameters.AddWithValue("cipher", new byte[] { 4 });
            cmd.Parameters.AddWithValue("tag", new byte[16]);
            await cmd.ExecuteNonQueryAsync();
        }
        try
        {
            var repo = new MetadataRepository(data);
            var columns = Enumerable.Range(0, 520).Select(i => new MetadataColumn(
                "field_" + i, "String", true, i % 2 == 0, i % 3 == 0, false, null,
                null, null, [], new Dictionary<int, string>(), [])).ToList();
            columns.Add(new MetadataColumn("_accountid_value", "Uniqueidentifier",
                true, false, false, true, null, null, null, [], new Dictionary<int, string>(), []));
            columns.Add(new MetadataColumn("write_only", "String",
                false, true, false, false, null, null, null, [], new Dictionary<int, string>(), []));
            var snap = await repo.SaveAsync(id, new MetadataCatalog([
                new MetadataTable("quote", "quotes", "quoteid", "name", false,
                    new Dictionary<int, string>(), columns, [])
            ]), "manual", "ci", CancellationToken.None);
            var version = snap.Snapshot.Version;
            await Assert.ThrowsAsync<ReadRejected>(() => repo.EnableFieldsBulkAsync(id, "quote",
                new MetadataBulkFieldToggle(version, false, true), "ci", CancellationToken.None));
            Assert.True(await repo.SetEntityAsync(id, "quote", true, "ci", CancellationToken.None));
            await Assert.ThrowsAsync<ReadRejected>(() => repo.EnableFieldsBulkAsync(id, "quote",
                new MetadataBulkFieldToggle(version, false, false), "ci", CancellationToken.None));

            var readOnly = await repo.EnableFieldsBulkAsync(id, "quote",
                new MetadataBulkFieldToggle(version, false, true), "ci", CancellationToken.None);
            Assert.Equal(521, readOnly.EnabledFields);
            Assert.Equal(521, readOnly.ReadFields);
            Assert.Equal(0, readOnly.CreateFields);
            var policies = await repo.PoliciesAsync(id, CancellationToken.None);
            Assert.True(policies.Fields.Single(f => f.FieldName == "_accountid_value").CanRead);
            Assert.False(policies.Fields.Single(f => f.FieldName == "_accountid_value").CanCreate);
            Assert.DoesNotContain(policies.Fields, f => f.FieldName == "write_only");

            var all = await repo.EnableFieldsBulkAsync(id, "quote",
                new MetadataBulkFieldToggle(version, true, true), "ci", CancellationToken.None);
            Assert.Equal(522, all.EnabledFields);
            Assert.Equal(261, all.CreateFields);
            Assert.Equal(174, all.UpdateFields);
            var after = await repo.PoliciesAsync(id, CancellationToken.None);
            Assert.True(after.Fields.Single(f => f.FieldName == "field_0").CanCreate);
            Assert.True(after.Fields.Single(f => f.FieldName == "field_0").CanUpdate);
            Assert.False(after.Fields.Single(f => f.FieldName == "_accountid_value").CanCreate);
            Assert.False(after.Fields.Single(f => f.FieldName == "_accountid_value").CanUpdate);

            var againReadOnly = await repo.EnableFieldsBulkAsync(id, "quote",
                new MetadataBulkFieldToggle(version, false, true), "ci", CancellationToken.None);
            Assert.Equal(521, againReadOnly.EnabledFields);
            after = await repo.PoliciesAsync(id, CancellationToken.None);
            Assert.True(after.Fields.Single(f => f.FieldName == "field_0").CanCreate);
            await Assert.ThrowsAsync<ReadRejected>(() => repo.EnableFieldsBulkAsync(id, "quote",
                new MetadataBulkFieldToggle(version + 1, true, true), "ci", CancellationToken.None));
            Assert.Equal(522, (await repo.PoliciesAsync(id, CancellationToken.None)).Fields.Count);
        }
        finally
        {
            await using var del = new NpgsqlCommand("DELETE FROM dynamics.connections WHERE id=@id", connection);
            del.Parameters.AddWithValue("id", id);
            await del.ExecuteNonQueryAsync();
        }
    }

}
