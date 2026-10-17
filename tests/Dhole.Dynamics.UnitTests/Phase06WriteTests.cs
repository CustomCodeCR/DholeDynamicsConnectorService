using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;
using Dhole.Dynamics.Infrastructure;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class Phase06WriteTests
{
    private static JsonElement Val<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static MetadataColumn Column(string name,string type,bool create=true,bool update=true,
        IReadOnlyList<MetadataChoice>? choices=null) =>
        new(name,type,true,create,update,false,null,2,null,[],
            new Dictionary<int,string>(),choices??[]);

    private static WritePolicy Fixture() 
    {
        var columns = new MetadataColumn[] {
            Column("new_name","String"), Column("new_status","Picklist",choices:[
                new MetadataChoice(1001,new Dictionary<int,string>()),
                new MetadataChoice(1002,new Dictionary<int,string>())]),
            Column("new_amount","Money"),Column("new_date","DateTime"),
            Column("new_count","Integer"), Column("new_active","Boolean"),
            Column("new_customer","Lookup")
        };
        var table = new MetadataTable("new_shipment","new_shipments","new_shipmentid","new_name",
            true,new Dictionary<int,string>(),columns,[
                new MetadataRelationship("new_customer_link","ManyToOneRelationships",
                    "account","new_shipment","accountid","new_customer",
                    "new_CustomerAccount",null)
            ],[new MetadataAlternateKey("new_name_key",["new_name"])]);
        var account = new MetadataTable("account","accounts","accountid","name",false,
            new Dictionary<int,string>(),[],[]);
        var conn = new DynamicsConnection(Guid.NewGuid(),"fixture","https://fixture.crm.dynamics.com",
            Guid.NewGuid(),Guid.NewGuid(),null,new byte[12],[3],new byte[16],
            1,DateTimeOffset.UtcNow);
        return new WritePolicy(conn,new MetadataSnapshot(Guid.NewGuid(),conn.Id,1,
            DateTimeOffset.UtcNow,new string('A',64),"manual",new MetadataCatalog([table,account])),
            table,new MetadataPolicies([],
                columns.Select(c=>new MetadataFieldPolicy("new_shipment",c.LogicalName,true,
                    true,c.CanCreate,c.CanUpdate,MetadataRepository.Fingerprint(c))).ToArray()));
    }

    [Fact]
    public async Task ValidatesChoiceMoneyAndBoolWithoutRawStrings()
    {
        var policy=Fixture();
        var (payload,fields)=await WriteValueCompiler.CompileAsync(policy,
            new Dictionary<string,JsonElement> {
                ["new_status"]=Val(1001), ["new_amount"]=Val(1250.50m),
                ["new_active"]=Val(true)
            }, (_,_,_)=>Task.CompletedTask,CancellationToken.None);
        Assert.Equal(3,fields.Length);
        Assert.Equal(1001,payload["new_status"]);
        Assert.Equal(1250.50m,payload["new_amount"]);
        await Assert.ThrowsAsync<ReadRejected>(()=>WriteValueCompiler.CompileAsync(policy,
            new Dictionary<string,JsonElement>{{"new_status",Val(9999)}},
            (_,_,_)=>Task.CompletedTask,CancellationToken.None));
        await Assert.ThrowsAsync<ReadRejected>(()=>WriteValueCompiler.CompileAsync(policy,
            new Dictionary<string,JsonElement>{{"new_amount",Val(1.234m)}},
            (_,_,_)=>Task.CompletedTask,CancellationToken.None));
    }

    [Fact]
    public async Task LookupRequiresApprovedRelationAndGuid()
    {
        var policy=Fixture();
        var target=Guid.NewGuid();
        var approved=false;
        var (payload,fields)=await WriteValueCompiler.CompileAsync(policy,
            new Dictionary<string,JsonElement>{{"new_CustomerAccount@odata.bind",Val(target.ToString("D"))}},
            (rel,id,_)=>{approved=rel.ReferencingAttribute=="new_customer"&&id==target;return Task.CompletedTask;},
            CancellationToken.None);
        Assert.True(approved);
        Assert.Contains("new_customer",fields);
        Assert.Equal("/accounts("+target.ToString("D")+")",payload["new_CustomerAccount@odata.bind"]);
        await Assert.ThrowsAsync<ReadRejected>(()=>WriteValueCompiler.CompileAsync(policy,
            new Dictionary<string,JsonElement>{{"http://evil@odata.bind",Val(target.ToString())}},
            (_,_,_)=>Task.CompletedTask,CancellationToken.None));
        await Assert.ThrowsAsync<ReadRejected>(()=>WriteValueCompiler.CompileAsync(policy,
            new Dictionary<string,JsonElement>{{"new_customer",Val(target.ToString())}},
            (_,_,_)=>Task.CompletedTask,CancellationToken.None));
    }

    [Fact]
    public void EtagAndRegisteredAlternateKeyPreventBlindOverwrites()
    {
        var policy=Fixture();
        Assert.Throws<ReadRejected>(()=>GenericWriteService.RequireEtag("*"));
        Assert.Throws<ReadRejected>(()=>GenericWriteService.RequireEtag(null));
        GenericWriteService.RequireEtag("W/\"12345\"");
        var key=GenericWriteService.AlternativeKey(policy,new Dictionary<string,JsonElement>
        {["new_name"]=Val("O'Connor")});
        Assert.Contains("new_name=",key);
        Assert.DoesNotContain("O'Connor",key);
        Assert.Throws<ReadRejected>(()=>GenericWriteService.AlternativeKey(policy,
            new Dictionary<string,JsonElement>{{"unregistered",Val("x")}}));
    }

    private sealed class Tokens : IDataverseAccessTokenProvider
    {
        public Task<string> AcquireAsync(DynamicsConnection connection,CancellationToken ct)=>
            Task.FromResult("unit-only");
    }
    private sealed class CountingHandler(HttpStatusCode status, bool timeout=false) : HttpMessageHandler
    {
        public int Calls {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
        {
            Calls++;
            Assert.Equal("fixture.crm.dynamics.com",req.RequestUri!.Host);
            if(timeout) throw new TaskCanceledException("simulated lost response");
            var result=new HttpResponseMessage(status);
            if(status==HttpStatusCode.Created)
                result.Content=new StringContent("""{"new_shipmentid":"6e4e5802-1dad-4c79-82ec-54a8fc1e5b13"}""",
                    Encoding.UTF8,"application/json");
            if(status==HttpStatusCode.TooManyRequests)
                result.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            return Task.FromResult(result);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed,412)]
    [InlineData(HttpStatusCode.TooManyRequests,429)]
    [InlineData(HttpStatusCode.InternalServerError,500)]
    [InlineData(HttpStatusCode.Created,201)]
    public async Task TransportSurfacesExactStatusAndNeverRetries(HttpStatusCode response,int expected)
    {
        var policy=Fixture();var handler=new CountingHandler(response);
        var transport=new DataverseWriteTransport(new HttpClient(handler),new Tokens());
        var result=await transport.SendAsync(new WriteCommand("create",policy,
            GenericWriteService.BasePath(policy),
            new Dictionary<string,object?>{{"new_name","unit"}}),CancellationToken.None);
        Assert.Equal(expected,result.HttpStatus);
        Assert.Equal(1,handler.Calls);
        if(expected==429)Assert.Equal(10,result.RetryAfterSeconds);
        if(expected==201)Assert.NotNull(result.RecordId);
    }

    [Fact]
    public async Task TimeoutAfterPostIsNotAutomaticallyResent()
    {
        var policy=Fixture();var handler=new CountingHandler(HttpStatusCode.OK,true);
        var transport=new DataverseWriteTransport(new HttpClient(handler),new Tokens());
        await Assert.ThrowsAsync<TaskCanceledException>(()=>transport.SendAsync(
            new WriteCommand("create",policy,GenericWriteService.BasePath(policy),
                new Dictionary<string,object?>{{"new_name","once"}}),CancellationToken.None));
        Assert.Equal(1,handler.Calls);
    }
    private sealed class BatchHandler(string payload) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;
            Assert.Equal("/api/data/v9.2/$batch", request.RequestUri!.AbsolutePath);
            Assert.StartsWith("multipart/mixed",request.Content!.Headers.ContentType!.MediaType);
            var sent=await request.Content.ReadAsStringAsync(ct);
            Assert.Contains("Content-Transfer-Encoding: binary",sent);
            Assert.Contains("If-Match: W/\"202\"",sent);
            Assert.Contains("changeset_",sent);
            return new HttpResponseMessage(HttpStatusCode.OK)
                {Content=new StringContent(payload,Encoding.UTF8,"multipart/mixed")};
        }
    }

    [Fact]
    public async Task BatchInspectsEachSubResponseInsteadOfTrustingOuterHttp200()
    {
        var policy=Fixture();
        var updatePath=GenericWriteService.ById(policy,Guid.NewGuid());
        var commands=new WriteCommand[]{
            new("create",policy,GenericWriteService.BasePath(policy),
                new Dictionary<string,object?>{{"new_name","New"}}),
            new("update",policy,updatePath,
                new Dictionary<string,object?>{{"new_status",1001}},IfMatch:"W/\"202\"")
        };
        const string success="HTTP/1.1 201 Created\r\n\r\nHTTP/1.1 204 No Content";
        var accepted=new BatchHandler(success);
        var result=await new DataverseWriteTransport(new HttpClient(accepted),new Tokens())
            .SendBatchAsync(commands,CancellationToken.None);
        Assert.Equal(200,result.HttpStatus);
        Assert.False(result.Ambiguous);
        Assert.Equal(1,accepted.Calls);

        var rejected=new BatchHandler("HTTP/1.1 412 Precondition Failed\r\nHTTP/1.1 204 No Content");
        var failed=await new DataverseWriteTransport(new HttpClient(rejected),new Tokens())
            .SendBatchAsync(commands,CancellationToken.None);
        Assert.Equal(412,failed.HttpStatus);

        var truncated=new BatchHandler("HTTP/1.1 200 OK");
        var uncertain=await new DataverseWriteTransport(new HttpClient(truncated),new Tokens())
            .SendBatchAsync(commands,CancellationToken.None);
        Assert.True(uncertain.Ambiguous);
    }

}
