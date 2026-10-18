using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;
using Dhole.Dynamics.Infrastructure;
using Dhole.Dynamics.Workers;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class Phase11SyncTests
{
    private sealed class Token : IDataverseAccessTokenProvider
    {
        public Task<string> AcquireAsync(DynamicsConnection c,CancellationToken ct)=>
            Task.FromResult("fake");
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> callback)
        : HttpMessageHandler
    {
        public int Calls {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(callback(req));
        }
    }
    private static ReadPolicy Policy()
    {
        var conn=new DynamicsConnection(Guid.NewGuid(),"demo","https://ci.crm.dynamics.com",
            Guid.NewGuid(),Guid.NewGuid(),null,new byte[12],[4],new byte[16],1,DateTimeOffset.UtcNow);
        var cols=new[]{
            new MetadataColumn("quoteid","Uniqueidentifier",true,false,false,false,
                null,null,null,[],new Dictionary<int,string>(),[]),
            new MetadataColumn("modifiedon","DateTime",true,false,false,false,
                null,null,null,[],new Dictionary<int,string>(),[])
        };
        var table=new MetadataTable("quote","quotes","quoteid","name",false,
            new Dictionary<int,string>(),cols,[]);
        var snap=new MetadataSnapshot(Guid.NewGuid(),conn.Id,1,DateTimeOffset.UtcNow,"hash","ci",
            new MetadataCatalog([table]));
        return new(conn,snap,table,new HashSet<string>(["quoteid","modifiedon"]));
    }

    [Fact]
    public void RetryIsBoundedAndNeverPermitsA401()
    {
        Assert.True(JobRetryPolicy.Retryable(429));
        Assert.True(JobRetryPolicy.Retryable(503));
        Assert.False(JobRetryPolicy.Retryable(401));
        Assert.False(JobRetryPolicy.Retryable(400));
        Assert.Equal(3600,JobRetryPolicy.DelaySeconds(50,99999));
        Assert.Equal(500,JobRetryPolicy.DelaySeconds(1,500));
        Assert.Equal("dataverse_transient",
            DurableJobWorker.Classify(new SyncRequestFailure(429,20)).Code);
        Assert.False(DurableJobWorker.Classify(new SyncRequestFailure(403)).Retry);
    }

    [Theory]
    [InlineData("http://ci.crm.dynamics.com/api/data/v9.2/quotes?$skiptoken=a")]
    [InlineData("https://evil.example/api/data/v9.2/quotes?$skiptoken=a")]
    [InlineData("https://ci.crm.dynamics.com/api/data/v9.2/accounts?$skiptoken=a")]
    [InlineData("https://ci.crm.dynamics.com/api/data/v9.2/quotes?$expand=customer")]
    [InlineData("https://ci.crm.dynamics.com/api/data/v9.2/quotes?$skiptoken=a#frag")]
    public void RejectsUntrustedOrWrongEntityContinuations(string url) =>
        Assert.Throws<ReadRejected>(()=>DataverseDeltaReader.ValidatedLink(Policy(),url));

    [Fact]
    public async Task TracksChangesOnlyFromPinnedHostAndPersistsOpaqueHashManifest()
    {
        var quote=Guid.NewGuid();
        var handler=new Handler(req=>
        {
            Assert.Equal("ci.crm.dynamics.com",req.RequestUri!.Host);
            Assert.Equal(HttpMethod.Get,req.Method);
            Assert.Equal("Bearer",req.Headers.Authorization!.Scheme);
            Assert.True(req.Headers.TryGetValues("Prefer",out var prefer) &&
                prefer.Any(x=>x.Contains("odata.track-changes",StringComparison.Ordinal)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content=new StringContent(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string,object> {
                    ["value"]=new[]{new Dictionary<string,object> {["quoteid"]=quote.ToString("D"),
                        ["@odata.etag"]="W/\"123\"",["name"]="private customer name"}},
                    ["@odata.deltaLink"]="https://ci.crm.dynamics.com/api/data/v9.2/quotes?$deltatoken=ABC"
                }),Encoding.UTF8,"application/json")
            };
        });
        var page=await new DataverseDeltaReader(new HttpClient(handler),new Token())
            .ReadAsync(Policy(),"delta",null,null,null,CancellationToken.None);
        Assert.Equal(1,handler.Calls);
        Assert.Single(page.Changes);
        Assert.Equal(quote,page.Changes[0].RecordId);
        Assert.Equal(64,page.Changes[0].ChangeHash.Length);
        Assert.DoesNotContain("private",page.Changes[0].ChangeHash);
        Assert.Null(page.NextLink);
        Assert.NotNull(page.DeltaLink);
    }

    [Fact]
    public async Task UnsupportedSuccessfulDeltaResponseIsMarkedForConfiguredFallback()
    {
        var handler=new Handler(_=>new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content=new StringContent("""{"value":[]}""",Encoding.UTF8,"application/json")
        });
        var page=await new DataverseDeltaReader(new HttpClient(handler),new Token())
            .ReadAsync(Policy(),"delta",null,null,null,CancellationToken.None);
        Assert.True(page.UnsupportedDelta);
        Assert.Empty(page.Changes);
        Assert.Equal(1,handler.Calls);
    }

    [Fact]
    public async Task RateLimitedReadPreservesRetryAfterButNeverResendsItself()
    {
        var handler=new Handler(_=>{
            var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(28));
            return response;
        });
        var ex=await Assert.ThrowsAsync<SyncRequestFailure>(()=>
            new DataverseDeltaReader(new HttpClient(handler),new Token())
                .ReadAsync(Policy(),"delta",null,null,null,CancellationToken.None));
        Assert.Equal(429,ex.StatusCode);
        Assert.Equal(28,ex.RetrySeconds);
        Assert.Equal(1,handler.Calls);
    }
}
