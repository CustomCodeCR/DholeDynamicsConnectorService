using Dhole.Dynamics.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class Phase12ReleaseSecurityTests
{
    private sealed class EnvironmentStub(string value):IHostEnvironment
    {
        public string EnvironmentName {get;set;}=value;
        public string ApplicationName {get;set;}="test";
        public string ContentRootPath {get;set;}="/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider {get;set;}=
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
    private static ProductionWritePolicy Policy(string env,string? writes=null,
        string? deletes=null,string? refWrite=null,string? refDelete=null,
        string? quotes=null,string? refQuotes=null)
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?>
            {
                ["ProductionWrites:Approved"]=writes,
                ["ProductionWrites:DeletesApproved"]=deletes,
                ["ProductionWrites:ApprovalReference"]=refWrite,
                ["ProductionWrites:DeletesApprovalReference"]=refDelete,
                ["ProductionQuotes:Approved"]=quotes,
                ["ProductionQuotes:ApprovalReference"]=refQuotes
            }).Build();
        return ProductionWritePolicy.FromConfiguration(config,new EnvironmentStub(env));
    }
    [Fact]
    public void ProductionRejectsWritesEvenWithFlagButNoTicket()
    {
        var policy=Policy("Production",writes:"YES");
        Assert.False(policy.WritesApproved);
        Assert.Equal("production_writes_not_approved",
            Assert.Throws<ReadRejected>(()=>policy.Demand("create")).Code);
        Assert.True(policy.IsBlocked(new DefaultHttpContext {Request={Method="POST",Path="/api/v1/dhole/quotes"}}.Request));
        Assert.False(policy.IsBlocked(new DefaultHttpContext {Request={Method="GET",Path="/api/v1/quotes"}}.Request));
        Assert.False(policy.IsBlocked(new DefaultHttpContext {
            Request={Method="POST",Path="/api/v1/records/quote/search"}}.Request));
        Assert.False(policy.IsBlocked(new DefaultHttpContext {
            Request={Method="POST",Path="/api/admin/v1/console/records/quote/search"}}.Request));
    }
    [Theory]
    [InlineData("POST", "/api/v1/saved-queries/cotizaciones/execute", false)]
    [InlineData("POST", "/api/v1/keyed/saved-queries/cotizaciones/execute", false)]
    [InlineData("POST", "/api/v1/keyed/templates/crear_oferta/execute", true)]
    [InlineData("POST", "/api/v1/keyed/saved-queries/cotizaciones/revoke", true)]
    [InlineData("POST", "/api/v1/records/account/by-key", false)]
    [InlineData("POST", "/api/v1/records/account/search", false)]
    [InlineData("POST", "/api/v1/records/account", true)]
    [InlineData("PUT", "/api/v1/records/account/by-key", true)]
    [InlineData("POST", "/api/v1/records/batch", true)]
    [InlineData("POST", "/api/v1/saved-queries/cotizaciones/enable", true)]
    public void ProductionGateOnlyExemptsKnownReadEndpoints(string method,string path,bool blocked)
    {
        var request=new DefaultHttpContext().Request;
        request.Method=method;
        request.Path=path;
        Assert.Equal(blocked,Policy("Production").IsBlocked(request));
    }

    [Fact]
    public void ProductionDeleteNeedsSecondDistinctApproval()
    {
        var allow=Policy("Production",writes:"YES",refWrite:"CAB-2026-00001");
        allow.Demand("create");
        Assert.Equal("production_deletes_not_approved",
            Assert.Throws<ReadRejected>(()=>allow.Demand("delete")).Code);
        var batch=new DefaultHttpContext();
        batch.Request.Method="DELETE";
        batch.Request.Path="/api/v1/records/quote/11111111-1111-4111-8111-111111111111";
        Assert.True(allow.IsBlocked(batch.Request));
        var full=Policy("Production","YES","YES","CAB-2026-00001","CAB-2026-00002");
        full.Demand("delete");
        Assert.False(full.IsBlocked(batch.Request));
        Assert.Equal("production_deletes_not_approved",
            Assert.Throws<ReadRejected>(()=>allow.Demand("action")).Code);
        full.Demand("action");
    }
    [Fact]
    public void QuoteApprovalNeedsTicketAndDoesNotOpenGenericWrites()
    {
        var route="/api/v1/keyed/templates/crear_oferta/execute-with-total";
        static HttpRequest Request(string path,string method="POST")
        {
            var r=new DefaultHttpContext().Request;
            r.Path=path;r.Method=method;
            return r;
        }
        var noTicket=Policy("Production",quotes:"YES");
        Assert.False(noTicket.QuoteWritesApproved);
        Assert.True(noTicket.IsBlocked(Request(route)));
        Assert.Equal("production_quote_writes_not_approved",
            Assert.Throws<ReadRejected>(()=>noTicket.DemandPricingQuote()).Code);

        var approved=Policy("Production",quotes:"YES",
            refQuotes:"CAB-2026-QUOTE-00001");
        approved.DemandPricingQuote();
        Assert.False(approved.WritesApproved);
        Assert.False(approved.IsBlocked(Request(route)));
        Assert.True(approved.IsBlocked(Request("/api/v1/keyed/templates/crear_oferta/execute")));
        Assert.True(approved.IsBlocked(Request("/api/v1/records/quote")));
        Assert.True(approved.IsBlocked(Request("/api/v1/keyed/templates/crear_oferta/execute-with-total/anything")));
        Assert.True(approved.IsBlocked(Request(route,"DELETE")));
        Assert.Equal("production_writes_not_approved",
            Assert.Throws<ReadRejected>(()=>approved.Demand("create")).Code);
        Assert.Equal("production_writes_not_approved",
            Assert.Throws<ReadRejected>(()=>approved.Demand("delete")).Code);
    }

    [Fact]
    public void StagingNeverAccidentallyRequiresProductionApproval()
    {
        var policy=Policy("Staging");
        policy.Demand("delete");
        Assert.False(policy.IsBlocked(new DefaultHttpContext {Request={Method="POST",Path="/api/v1/quotes"}}.Request));
    }
    [Fact]
    public async Task MiddlewareFailsClosedWithoutInvokingAnyMutation()
    {
        var downstream=false;
        var middleware=new ProductionWriteGateMiddleware(_=>{downstream=true;return Task.CompletedTask;});
        var http=new DefaultHttpContext();
        http.Request.Method="POST";http.Request.Path="/api/v1/records/quote";
        http.Response.Body=new MemoryStream();
        http.RequestServices=new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        await middleware.InvokeAsync(http,Policy("Production"));
        Assert.Equal(403,http.Response.StatusCode);
        Assert.False(downstream);
    }
    [Theory]
    [InlineData("/tmp/secret")]
    [InlineData("/run/secrets/../invalid")]
    [InlineData("relative-path")]
    public void SecretFilesRejectUnsafePaths(string path)
    {
        Assert.Throws<InvalidOperationException>(()=>ReleaseSecretFiles.Read(
            new Dictionary<string,string?> {["DYNAMICS_CONNECTION_STRING_FILE"]=path}));
    }
}
