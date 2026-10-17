using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;
using Dhole.Dynamics.Infrastructure;
using Npgsql;
using Xunit;

namespace Dhole.Dynamics.IntegrationTests;

[Collection("ConnectorApiHost")]
public sealed class Phase06WriteTests
{
    private static JsonElement V<T>(T value) => JsonSerializer.SerializeToElement(value);
    private sealed class Connections(params DynamicsConnection[] items) : IConnectionRepository
    {
        public Task<DynamicsConnection?> FindAsync(string code,CancellationToken ct) =>
            Task.FromResult(items.FirstOrDefault(c=>c.Code==code));
        public Task<IReadOnlyList<ConnectionSummary>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConnectionSummary>>([]);
        public Task<ConnectionSummary> UpsertAsync(ConnectionUpsertRequest value,string actor,CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> RotateSecretAsync(string code,string secret,string actor,CancellationToken ct) =>
            throw new NotSupportedException();
    }
    private sealed class Tokens : IDataverseAccessTokenProvider
    {
        public Task<string> AcquireAsync(DynamicsConnection c,CancellationToken ct)=>Task.FromResult("fake-token");
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls {get;private set;}
        public bool Timeout {get;set;}
        public HttpStatusCode Status {get;set;} = HttpStatusCode.Created;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
        {
            Calls++;
            Assert.Equal("ci.crm.dynamics.com",req.RequestUri!.Host);
            if(Timeout)throw new TaskCanceledException("response lost after send");
            if(req.RequestUri!.AbsolutePath.EndsWith("/$batch",StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("HTTP/1.1 201 Created\r\nHTTP/1.1 201 Created",
                        Encoding.UTF8,"multipart/mixed")
                });
            var result=new HttpResponseMessage(Status);
            if(req.Method==HttpMethod.Post && Status==HttpStatusCode.Created)
                result.Content=new StringContent("""{"new_shipmentid":"93c39236-5309-4281-9d92-2fdf7edb1a1f"}""",
                    Encoding.UTF8,"application/json");
            return Task.FromResult(result);
        }
    }
    private static MetadataCatalog Catalog() => new([
        new MetadataTable("new_shipment","new_shipments","new_shipmentid","new_name",true,
            new Dictionary<int,string>(),
            [
                new MetadataColumn("new_shipmentid","Uniqueidentifier",true,false,false,false,
                    null,null,null,[],new Dictionary<int,string>(),[]),
                new MetadataColumn("new_name","String",true,true,true,false,
                    null,null,null,[],new Dictionary<int,string>(),[]),
                new MetadataColumn("new_status","Picklist",true,true,true,false,
                    null,null,null,[],new Dictionary<int,string>(),[
                        new MetadataChoice(10,new Dictionary<int,string>())]),
            ], [],[new MetadataAlternateKey("new_shipment_name",["new_name"])])
    ]);

    [Fact]
    public async Task ConcurrentKeysAndAmbiguousCreateNeverDispatchTwice()
    {
        var cs=Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if(cs is null)return;
        await using var db=NpgsqlDataSource.Create(cs);
        var a=Connection(Guid.NewGuid());
        var b=Connection(Guid.NewGuid());
        const string issuer="https://login.microsoftonline.com/phase06-tests/v2.0";
        var clientId=Guid.NewGuid().ToString("D");
        await using var sql=await db.OpenConnectionAsync();
        foreach(var connection in new[]{a,b})
        {
            await using var insert=new NpgsqlCommand("""
                INSERT INTO dynamics.connections
                  (id,code,dataverse_url,tenant_id,client_id,secret_nonce,secret_ciphertext,secret_tag,secret_key_version)
                VALUES (@id,@code,@host,@tenant,@client,@nonce,@cipher,@tag,1)
                """,sql);
            insert.Parameters.AddWithValue("id",connection.Id);
            insert.Parameters.AddWithValue("code",connection.Code);
            insert.Parameters.AddWithValue("host",connection.DataverseUrl);
            insert.Parameters.AddWithValue("tenant",connection.TenantId);
            insert.Parameters.AddWithValue("client",connection.ClientId);
            insert.Parameters.AddWithValue("nonce",connection.SecretNonce);
            insert.Parameters.AddWithValue("cipher",connection.SecretCiphertext);
            insert.Parameters.AddWithValue("tag",connection.SecretTag);
            await insert.ExecuteNonQueryAsync();
        }
        try
        {
            var metadata=new MetadataRepository(db);
            var clients=new ClientAccessRepository(db);
            var stored=new WriteLedger(db);
            var retention=new DeletePolicyRepository(db);
            var registry=await clients.RegisterAsync(new ClientRegistrationRequest(
                issuer,clientId,"CI Phase06"),"ci",CancellationToken.None);
            foreach(var connection in new[]{a,b})
            {
                await metadata.SaveAsync(connection.Id,Catalog(),"manual","ci",CancellationToken.None);
                Assert.True(await metadata.SetEntityAsync(connection.Id,"new_shipment",true,"ci",CancellationToken.None));
                foreach(var col in Catalog().Tables[0].Columns.Where(c=>c.CanCreate))
                    Assert.True(await metadata.SetFieldAsync(connection.Id,"new_shipment",col.LogicalName,
                        new MetadataFieldToggle(true,true,true,true),"ci",CancellationToken.None));
            }
            foreach(var op in new[]{"create","update","upsert","delete"})
            {
                var fields=op=="delete"?new[]{"new_shipmentid"}:new[]{"new_name","new_status"};
                Assert.NotNull(await clients.UpsertGrantAsync(new GrantRequest(registry.Id,a.Code,
                    "new_shipment",op,fields,100),"ci",CancellationToken.None));
            }
            var principal=new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("connector:issuer",issuer),new Claim("azp",clientId),
                new Claim("roles","dynamics.records.create"),
                new Claim("roles","dynamics.records.update"),
                new Claim("roles","dynamics.records.delete"),
                new Claim("roles","dynamics.records.upsert"),
                new Claim("roles","dynamics.records.read")
            ],"test-signed-app"));
            var access=new WriteAccessService(new Connections(a,b),metadata,clients);
            var handler=new Handler();
            var transport=new DataverseWriteTransport(new HttpClient(handler),new Tokens());
            var service=new GenericWriteService(access,stored,transport,retention);
            var create=new GenericCreateRequest(a.Code,new Dictionary<string,JsonElement>
            {["new_name"]=V("First shipment"),["new_status"]=V(10)});
            const string key="same-intent-key-000001";
            handler.Timeout=true;
            var ambiguous=await service.CreateAsync(principal,"new_shipment",create,key,CancellationToken.None);
            Assert.Equal("ambiguous",ambiguous.State);
            Assert.Equal(1,handler.Calls);
            var retry=await Assert.ThrowsAsync<ReadRejected>(()=>service.CreateAsync(
                principal,"new_shipment",create,key,CancellationToken.None));
            Assert.Equal(409,retry.Status);
            Assert.Equal(1,handler.Calls);
            Assert.Equal("ambiguous",(await stored.FindAsync(a.Id,ambiguous.OperationId,
                CancellationToken.None,issuer,clientId))!.State);
            Assert.Null(await stored.FindAsync(a.Id,ambiguous.OperationId,CancellationToken.None,
                issuer,"different-client"));
            Assert.False(await retention.GetAsync(a.Id,"new_shipment",CancellationToken.None)
                .ContinueWith(t=>t.Result.Enabled));
            await Assert.ThrowsAsync<ReadRejected>(()=>service.DeleteAsync(principal,"new_shipment",
                Guid.NewGuid(),new GenericDeleteRequest(a.Code,true,"confirmed-delete"),
                "delete-test-00000001","W/\"100\"",CancellationToken.None));
            Assert.True(await retention.SetAsync(a.Id,"new_shipment",
                new DeletePolicyRequest(true,0),"ci",CancellationToken.None));
            await Assert.ThrowsAsync<ReadRejected>(()=>service.DeleteAsync(principal,"new_shipment",
                Guid.NewGuid(),new GenericDeleteRequest(a.Code,false,"confirmed-delete"),
                "delete-test-00000002","W/\"100\"",CancellationToken.None));

            handler.Timeout=false;handler.Status=HttpStatusCode.PreconditionFailed;
            var patch=await service.PatchAsync(principal,"new_shipment",Guid.NewGuid(),
                new GenericPatchRequest(a.Code,new Dictionary<string,JsonElement>{{"new_name",V("Changed")}}),
                "patch-test-00000001","W/\"101\"",CancellationToken.None);
            Assert.Equal("rejected",patch.State);
            Assert.Equal(412,patch.HttpStatus);
            var replay=await service.PatchAsync(principal,"new_shipment",Guid.Parse(
                    "49d29e53-caf3-4bed-a7b5-f1a9c71a4d16"),
                new GenericPatchRequest(a.Code,new Dictionary<string,JsonElement>{{"new_name",V("Changed")}}),
                "patch-test-00000002","W/\"101\"",CancellationToken.None);
            Assert.Equal(412,replay.HttpStatus);

            handler.Status=HttpStatusCode.NoContent;
            var many=await service.BatchAsync(principal,
                new GenericBatchRequest(a.Code,[
                    new GenericBatchItem("create","new_shipment",null,null,
                        new Dictionary<string,JsonElement>{{"new_name",V("Shipment A")}}),
                    new GenericBatchItem("create","new_shipment",null,null,
                        new Dictionary<string,JsonElement>{{"new_name",V("Shipment B")}})
                ]),"batch-test-00000001",CancellationToken.None);
            Assert.Equal("succeeded",many.State);
            Assert.Equal(200,many.HttpStatus);
            var upsert=await service.UpsertAsync(principal,"new_shipment",
                new GenericUpsertRequest(a.Code,
                    new Dictionary<string,JsonElement>{{"new_name",V("PO-4455")}},
                    new Dictionary<string,JsonElement>{{"new_status",V(10)}}),
                "upsert-test-000001",CancellationToken.None);
            Assert.Equal("succeeded",upsert.State);
            var currentSnapshot = (await metadata.LatestAsync(a.Id,CancellationToken.None))!;
            var currentPolicies = await metadata.PoliciesAsync(a.Id,CancellationToken.None);
            Assert.Throws<ReadRejected>(()=>GenericWriteService.AlternativeKey(
                new WritePolicy(a,currentSnapshot,Catalog().Tables[0],currentPolicies),
                new Dictionary<string,JsonElement>{{"evil",V("x")}}));

            // Two instances sharing PostgreSQL may reserve one intent only once.
            var pair=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>stored.ReserveAsync(a.Id,
                issuer,clientId,"concurrent-key-000001",new string('A',64),"new_shipment",
                "create",null,CancellationToken.None)));
            Assert.Equal(1,pair.Count(x=>x.Created));
            await Assert.ThrowsAsync<ReadRejected>(()=>stored.ReserveAsync(a.Id,
                issuer,clientId,"concurrent-key-000001",new string('B',64),"new_shipment",
                "create",null,CancellationToken.None));
            var isolated=await stored.ReserveAsync(b.Id,issuer,clientId,
                "concurrent-key-000001",new string('A',64),"new_shipment","create",
                null,CancellationToken.None);
            Assert.True(isolated.Created);
            Assert.True(await stored.ReconcileAsync(a.Id,ambiguous.OperationId,"ci",
                new WriteReconcileRequest(null,true,"verified with separate Dataverse audit"),
                CancellationToken.None));
            Assert.Equal("reconciled",(await stored.FindAsync(a.Id,ambiguous.OperationId,
                CancellationToken.None))!.State);
        }
        finally
        {
            await using var cmd=new NpgsqlCommand("""
                DELETE FROM dynamics.api_clients WHERE issuer=@issuer AND oauth_client_id=@client;
                DELETE FROM dynamics.connections WHERE id IN (@a,@b)
                """,sql);
            cmd.Parameters.AddWithValue("issuer",issuer);
            cmd.Parameters.AddWithValue("client",clientId);
            cmd.Parameters.AddWithValue("a",a.Id);
            cmd.Parameters.AddWithValue("b",b.Id);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static DynamicsConnection Connection(Guid id)=>
        new(id,"phase06-"+id.ToString("N"),"https://ci.crm.dynamics.com",
            Guid.NewGuid(),Guid.NewGuid(),null,new byte[12],[4],new byte[16],1,DateTimeOffset.UtcNow);
}
