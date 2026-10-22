using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;
using Dhole.Dynamics.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

[Collection("ConnectorApiHost")]
public sealed class Phase07TemplateTests
{
    private sealed class FakeConnections(params DynamicsConnection[] connections):IConnectionRepository
    {
        public Task<DynamicsConnection?> FindAsync(string code,CancellationToken ct)=>
            Task.FromResult(connections.FirstOrDefault(c=>c.Code==code));
        public Task<IReadOnlyList<ConnectionSummary>> ListAsync(CancellationToken ct)=>
            Task.FromResult<IReadOnlyList<ConnectionSummary>>([]);
        public Task<ConnectionSummary> UpsertAsync(ConnectionUpsertRequest request,string actor,CancellationToken ct)=>
            throw new NotSupportedException();
        public Task<bool> RotateSecretAsync(string code,string secret,string actor,CancellationToken ct)=>
            throw new NotSupportedException();
    }
    private sealed class FakeTokens:IDataverseAccessTokenProvider
    {
        public Task<string> AcquireAsync(DynamicsConnection connection,CancellationToken ct)=>
            Task.FromResult("integration-test-token");
    }
    private sealed class FakeDataverse:HttpMessageHandler
    {
        public List<string> Sent {get;}=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken ct)
        {
            Assert.Equal("ci.crm.dynamics.com",message.RequestUri!.Host);
            if(message.Method!=HttpMethod.Post)
                throw new InvalidOperationException("Unexpected call to Dataverse");
            Sent.Add((message.RequestUri.AbsolutePath)+" "+(await message.Content!.ReadAsStringAsync(ct)));
            if(message.RequestUri.AbsolutePath.Contains("Microsoft.Dynamics.CRM.",StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return new HttpResponseMessage(HttpStatusCode.Created) {
                Content=new StringContent("""{"new_shipmentid":"d64a19c1-d8bc-4678-8b03-08c3a53467a1"}""",
                    Encoding.UTF8,"application/json")
            };
        }
    }
    private sealed class Host:IHostEnvironment
    {
        public string EnvironmentName {get;set;}="Testing";
        public string ApplicationName {get;set;}="ConnectorTests";
        public string ContentRootPath {get;set;}="/";
        public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
    }
    private static MetadataCatalog Catalog()=>
        new([new MetadataTable("new_shipment","new_shipments","new_shipmentid","new_name",true,
            new Dictionary<int,string>(),[
                new MetadataColumn("new_shipmentid","Uniqueidentifier",true,false,false,false,
                    null,null,null,[],new Dictionary<int,string>(),[]),
                new MetadataColumn("new_name","String",true,true,true,false,
                    null,null,null,[],new Dictionary<int,string>(),[])
            ],[])]);
    private static DynamicsConnection Connection(Guid id)=>new(id,"phase07-"+id.ToString("N"),
        "https://ci.crm.dynamics.com",Guid.NewGuid(),Guid.NewGuid(),null,new byte[12],[9],
        new byte[16],1,DateTimeOffset.UtcNow);
    private static JsonElement J<T>(T value)=>JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task NewOperationFromAdminConfigWorksWithoutRebuildAndRollsBack()
    {
        var cs=Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if(cs is null)return;
        await using var db=NpgsqlDataSource.Create(cs);
        var a=Connection(Guid.NewGuid());
        var b=Connection(Guid.NewGuid());
        var clientId=Guid.NewGuid().ToString("D");
        const string issuer="https://login.microsoftonline.com/phase07-tests/v2.0";
        await using var conn=await db.OpenConnectionAsync();
        foreach(var c in new[]{a,b})
        {
            await using var cmd=new NpgsqlCommand("""
                INSERT INTO dynamics.connections
                  (id,code,dataverse_url,tenant_id,client_id,secret_nonce,secret_ciphertext,secret_tag,secret_key_version)
                VALUES(@id,@code,@host,@tenant,@client,@nonce,@cipher,@tag,1)
                """,conn);
            cmd.Parameters.AddWithValue("id",c.Id);
            cmd.Parameters.AddWithValue("code",c.Code);
            cmd.Parameters.AddWithValue("host",c.DataverseUrl);
            cmd.Parameters.AddWithValue("tenant",c.TenantId);
            cmd.Parameters.AddWithValue("client",c.ClientId);
            cmd.Parameters.AddWithValue("nonce",c.SecretNonce);
            cmd.Parameters.AddWithValue("cipher",c.SecretCiphertext);
            cmd.Parameters.AddWithValue("tag",c.SecretTag);
            await cmd.ExecuteNonQueryAsync();
        }
        try
        {
            var meta=new MetadataRepository(db);
            var oauth=new ClientAccessRepository(db);
            var templates=new TemplateRepository(db);
            var links=new ExternalReferenceRepository(db,meta);
            var actionPolicies=new ActionPolicyRepository(db);
            var grants=await oauth.RegisterAsync(
                new ClientRegistrationRequest(issuer,clientId,"Integration Phase07"),"ci",CancellationToken.None);
            foreach(var c in new[]{a,b})
            {
                await meta.SaveAsync(c.Id,Catalog(),"manual","ci",CancellationToken.None);
                Assert.True(await meta.SetEntityAsync(c.Id,"new_shipment",true,"ci",CancellationToken.None));
                Assert.True(await meta.SetFieldAsync(c.Id,"new_shipment","new_name",
                    new MetadataFieldToggle(true,true,true,true),"ci",CancellationToken.None));
            }
            Assert.NotNull(await oauth.UpsertGrantAsync(new GrantRequest(grants.Id,a.Code,"new_shipment",
                "create",["new_name"],100),"ci",CancellationToken.None));
            var actor=new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("connector:issuer",issuer),new Claim("azp",clientId),
                new Claim("roles","dynamics.records.create"),
                new Claim("roles","dynamics.actions.invoke")
            ],"test-jwt"));
            var connectionRepo=new FakeConnections(a,b);
            var access=new WriteAccessService(connectionRepo,meta,oauth);
            var readAccess=new ReadAccessRepository(connectionRepo,meta,oauth);
            var fake=new FakeDataverse();
            var tokens=new FakeTokens();
            var ledger=new WriteLedger(db);
            var saveQuery=new SavedQueryRepository(db);
            var config=new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string,string?>{{"READ_CURSOR_KEY_BASE64",
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}}).Build();
            using var cursor=new ReadCursorProtector(config,new Host());
            var reader=new ReadSearchService(new HttpClient(fake),tokens,readAccess,cursor);
            var writer=new GenericWriteService(access,ledger,
                new DataverseWriteTransport(new HttpClient(fake),tokens),new DeletePolicyRepository(db));
            var actions=new BoundActionExecutor(actionPolicies,oauth,ledger,new HttpClient(fake),tokens);
            var orchestrator=new TemplateOrchestrator(templates,links,access,writer,reader,saveQuery,
                readAccess,actionPolicies,actions,
                new DataverseMetadataDiscovery(new HttpClient(fake),tokens));
            const string name="CreateShipment";
            var first=new IntegrationTemplate("new_shipment","create",
                [new TemplateInput("po","string")],
                [new TemplateMapping("new_name","po",[new TemplateStep("upper")])]);
            var draft=await orchestrator.CreateDraftAsync(a.Code,name,first,"ci",CancellationToken.None);
            Assert.Null(draft.ActiveVersion);
            Assert.Single(draft.History);
            Assert.Null(await templates.FindAsync(b.Id,name,CancellationToken.None));
            var inactive=await Assert.ThrowsAsync<ReadRejected>(()=>orchestrator.ExecuteAsync(actor,name,
                new TemplateInputs(a.Code,new Dictionary<string,JsonElement>{{"po",J("foo")}}),
                "phase07-intent-000001",CancellationToken.None));
            Assert.Equal(404,inactive.Status);
            var preview=await orchestrator.PreviewAsync(a.Code,name,1,
                new Dictionary<string,JsonElement>{{"po",J("abc")}},CancellationToken.None);
            Assert.Equal("ABC",preview.Payload["new_name"].GetString());
            Assert.False(preview.WouldSend);
            Assert.Empty(fake.Sent);

            await orchestrator.ActivateAsync(a.Code,name,1,"ci",CancellationToken.None);
            var firstRun=await orchestrator.ExecuteAsync(actor,name,
                new TemplateInputs(a.Code,new Dictionary<string,JsonElement>{{"po",J("abc")}}),
                "phase07-intent-000001",CancellationToken.None);
            Assert.Equal("succeeded",Assert.IsType<WriteResult>(firstRun).State);
            Assert.Contains("ABC",fake.Sent.Single());
            var v2=first with {Mappings=[new TemplateMapping("new_name","po",
                [new TemplateStep("lower")])]};
            var updated=await orchestrator.CreateDraftAsync(a.Code,name,v2,"ci",CancellationToken.None);
            Assert.Equal(1,updated.ActiveVersion);
            Assert.Equal(2,updated.History.Count);
            await orchestrator.ActivateAsync(a.Code,name,2,"ci",CancellationToken.None);
            await orchestrator.ExecuteAsync(actor,name,
                new TemplateInputs(a.Code,new Dictionary<string,JsonElement>{{"po",J("ABC")}}),
                "phase07-intent-000002",CancellationToken.None);
            Assert.Contains("abc",fake.Sent.Last());
            await orchestrator.ActivateAsync(a.Code,name,1,"ci",CancellationToken.None);
            await orchestrator.ExecuteAsync(actor,name,
                new TemplateInputs(a.Code,new Dictionary<string,JsonElement>{{"po",J("abc")}}),
                "phase07-intent-000003",CancellationToken.None);
            Assert.Contains("ABC",fake.Sent.Last());

            // Connection-bound ID mapping cannot be resolved from a different organization.
            var link=await links.UpsertAsync(a.Id,new ReferenceWrite("dhole","rate-123","new_shipment",
                Guid.NewGuid()),"ci",CancellationToken.None);
            Assert.Null(await links.FindAsync(b.Id,"dhole","rate-123","new_shipment",CancellationToken.None));
            Assert.Equal(link.DataverseId,(await links.FindAsync(a.Id,"dhole","rate-123",
                "new_shipment",CancellationToken.None))!.DataverseId);

            // Bound action: requires *separate* admin approval, OAuth app role and grant.
            await actionPolicies.SetAsync(a.Id,
                new BoundActionWrite("new_shipment","new_CloseShipment",
                    [new TemplateInput("reason","string")]),"ci",CancellationToken.None);
            await actionPolicies.EnableAsync(a.Id,"new_shipment","new_CloseShipment",true,"ci",CancellationToken.None);
            var actionDef=new IntegrationTemplate("new_shipment","action",
                [new TemplateInput("message","string")],
                [new TemplateMapping("reason","message")],ActionName:"new_CloseShipment");
            await orchestrator.CreateDraftAsync(a.Code,"close_shipment",actionDef,"ci",CancellationToken.None);
            await orchestrator.ActivateAsync(a.Code,"close_shipment",1,"ci",CancellationToken.None);
            await Assert.ThrowsAsync<ReadRejected>(()=>orchestrator.ExecuteAsync(actor,"close_shipment",
                new TemplateInputs(a.Code,new Dictionary<string,JsonElement>{{"message",J("ok")}},
                    RecordId:Guid.NewGuid()),"phase07-action-000001",CancellationToken.None));
            Assert.NotNull(await oauth.UpsertGrantAsync(new GrantRequest(grants.Id,a.Code,"new_shipment",
                "action",["__action_new_CloseShipment","reason"],100),"ci",CancellationToken.None));
            var acted=await orchestrator.ExecuteAsync(actor,"close_shipment",
                new TemplateInputs(a.Code,new Dictionary<string,JsonElement>{{"message",J("ok")}},
                    RecordId:Guid.NewGuid()),"phase07-action-000001",CancellationToken.None);
            Assert.Equal("succeeded",Assert.IsType<WriteResult>(acted).State);
            Assert.Contains("Microsoft.Dynamics.CRM.new_CloseShipment",fake.Sent.Last());
            await actionPolicies.EnableAsync(a.Id,"new_shipment","new_CloseShipment",false,"ci",CancellationToken.None);
            await Assert.ThrowsAsync<ReadRejected>(()=>orchestrator.ExecuteAsync(actor,"close_shipment",
                new TemplateInputs(a.Code,new Dictionary<string,JsonElement>{{"message",J("ok")}},
                    RecordId:Guid.NewGuid()),"phase07-action-000002",CancellationToken.None));

            await orchestrator.ActivateAsync(a.Code,name,null,"ci",CancellationToken.None);
            Assert.Null((await templates.FindAsync(a.Id,name,CancellationToken.None))!.ActiveVersion);
        }
        finally
        {
            await using var cleanup=new NpgsqlCommand("""
                DELETE FROM dynamics.external_references
                WHERE connection_id IN (@a,@b) AND integration_reference_space IS NOT NULL;
                DELETE FROM dynamics.api_clients WHERE issuer=@issuer AND oauth_client_id=@client;
                DELETE FROM dynamics.connections WHERE id IN (@a,@b)
                """,conn);
            cleanup.Parameters.AddWithValue("issuer",issuer);cleanup.Parameters.AddWithValue("client",clientId);
            cleanup.Parameters.AddWithValue("a",a.Id);cleanup.Parameters.AddWithValue("b",b.Id);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
