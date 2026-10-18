using System.Text.Json;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;
using Npgsql;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

[Collection("ConnectorApiHost")]
public sealed class Phase09MigrationTests
{
    private sealed class Connections(params DynamicsConnection[] items):IConnectionRepository
    {
        public Task<DynamicsConnection?> FindAsync(string code,CancellationToken ct)=>
            Task.FromResult(items.FirstOrDefault(c=>c.Code==code));
        public Task<IReadOnlyList<ConnectionSummary>> ListAsync(CancellationToken ct)=>
            Task.FromResult<IReadOnlyList<ConnectionSummary>>([]);
        public Task<ConnectionSummary> UpsertAsync(ConnectionUpsertRequest req,string actor,CancellationToken ct)=>
            throw new NotSupportedException();
        public Task<bool> RotateSecretAsync(string code,string secret,string actor,CancellationToken ct)=>
            throw new NotSupportedException();
    }
    private static DynamicsConnection Connection(Guid id)=>
        new(id,"p09-"+id.ToString("N"),"https://ci.crm.dynamics.com",
            Guid.NewGuid(),Guid.NewGuid(),null,new byte[12],[4],new byte[16],1,DateTimeOffset.UtcNow);

    [Fact]
    public async Task StagesEnabledMetadataOnlyWithoutActiveTemplatesAndIsIdempotent()
    {
        var cs=Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if(cs is null)return;
        await using var db=NpgsqlDataSource.Create(cs);
        var first=Connection(Guid.NewGuid());
        var second=Connection(Guid.NewGuid());
        await using var sql=await db.OpenConnectionAsync();
        foreach(var c in new[]{first,second})
        {
            await using var command=new NpgsqlCommand("""
                INSERT INTO dynamics.connections
                (id,code,dataverse_url,tenant_id,client_id,secret_nonce,secret_ciphertext,secret_tag,secret_key_version)
                VALUES(@id,@code,@host,@tenant,@client,@nonce,@cipher,@tag,1)
                """,sql);
            command.Parameters.AddWithValue("id",c.Id);
            command.Parameters.AddWithValue("code",c.Code);
            command.Parameters.AddWithValue("host",c.DataverseUrl);
            command.Parameters.AddWithValue("tenant",c.TenantId);
            command.Parameters.AddWithValue("client",c.ClientId);
            command.Parameters.AddWithValue("nonce",c.SecretNonce);
            command.Parameters.AddWithValue("cipher",c.SecretCiphertext);
            command.Parameters.AddWithValue("tag",c.SecretTag);
            await command.ExecuteNonQueryAsync();
        }
        try
        {
            var metadata=new MetadataRepository(db);
            var saved=new SavedQueryRepository(db);
            var templates=new TemplateRepository(db);
            var connections=new Connections(first,second);
            var read=new ReadAccessRepository(connections,metadata,new ClientAccessRepository(db));
            var write=new WriteAccessService(connections,metadata,new ClientAccessRepository(db));
            var migration=new LegacyMigrationService(read,write,saved,templates,db);
            var src=LegacyMigrationService.Bundle.Entries.Single(x=>x.Id=="LEG-001");
            var names=src.Query!.Select.Concat(["title"]).Distinct(StringComparer.Ordinal).ToArray();
            var catalog=new MetadataCatalog([new MetadataTable("incident","incidents","incidentid",
                "title",false,new Dictionary<int,string>(),
                names.Select(n=>new MetadataColumn(n,"String",true,true,true,false,
                    null,null,null,[],new Dictionary<int,string>(),[])).ToArray(),[])]);
            await metadata.SaveAsync(first.Id,catalog,"manual","ci",CancellationToken.None);
            Assert.True(await metadata.SetEntityAsync(first.Id,"incident",true,"ci",CancellationToken.None));
            foreach(var name in names)
                Assert.True(await metadata.SetFieldAsync(first.Id,"incident",name,
                    new MetadataFieldToggle(true,true,true,true),"ci",CancellationToken.None));

            var before=await migration.PreviewAsync(first.Code,CancellationToken.None);
            Assert.Equal(88,before.Total);
            Assert.True(before.Items.Any(e=>e.Id=="LEG-001"&&e.Status=="ready_to_stage"),
                "LEG-001: "+string.Join(",",before.Items.Single(e=>e.Id=="LEG-001").Reasons));
            Assert.Contains(before.Items,e=>e.Id=="LEG-080"&&e.Status=="blocked_source");
            Assert.Equal(0,(await migration.PreviewAsync(second.Code,CancellationToken.None)).Ready);
            await Assert.ThrowsAsync<ReadRejected>(()=>migration.ImportAsync(first.Code,false,"ci",CancellationToken.None));
            var imported=await migration.ImportAsync(first.Code,true,"ci",CancellationToken.None);
            Assert.Contains(imported.Items,e=>e.Id=="LEG-001"&&e.Status=="staged");
            var query=await saved.FindAsync(first.Id,"legacy-001",CancellationToken.None);
            Assert.NotNull(query);
            Assert.False(query!.Enabled);
            var template=await templates.FindAsync(first.Id,"Legacy001",CancellationToken.None);
            Assert.NotNull(template);
            Assert.Null(template!.ActiveVersion);
            Assert.Single(template.History);
            Assert.Null(await saved.FindAsync(second.Id,"legacy-001",CancellationToken.None));

            var again=await migration.ImportAsync(first.Code,true,"ci",CancellationToken.None);
            Assert.Contains(again.Items,e=>e.Id=="LEG-001"&&e.Status=="already_staged");
            Assert.Single((await templates.FindAsync(first.Id,"Legacy001",CancellationToken.None))!.History);
            var history=await migration.HistoryAsync(first.Code,CancellationToken.None);
            Assert.Equal(88,history.Count);
            Assert.Contains(history,e=>e.Id=="LEG-080"&&e.Status=="blocked_source");

            // A source with a fixed hardcoded tenant-specific GUID remains incompatible.
            Assert.Contains(history,e=>e.Id=="LEG-036"&&e.Status=="blocked_source");
            Assert.Null(await templates.FindAsync(first.Id,"Legacy036",CancellationToken.None));
        }
        finally
        {
            await using var clean=new NpgsqlCommand("""
                DELETE FROM dynamics.connections WHERE id IN (@a,@b)
                """,sql);
            clean.Parameters.AddWithValue("a",first.Id);
            clean.Parameters.AddWithValue("b",second.Id);
            await clean.ExecuteNonQueryAsync();
        }
    }
}
