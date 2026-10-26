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
public sealed class Phase05ReadTests
{
    private sealed class ConnectionFixture(params DynamicsConnection[] items) : IConnectionRepository
    {
        public Task<DynamicsConnection?> FindAsync(string code, CancellationToken ct) =>
            Task.FromResult(items.FirstOrDefault(c => c.Code == code));
        public Task<IReadOnlyList<ConnectionSummary>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConnectionSummary>>([]);
        public Task<ConnectionSummary> UpsertAsync(ConnectionUpsertRequest request, string actor, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> RotateSecretAsync(string code, string secret, string actor, CancellationToken ct) =>
            throw new NotSupportedException();
    }
    private sealed class NoRealTokens : IDataverseAccessTokenProvider
    {
        public Task<string> AcquireAsync(DynamicsConnection c, CancellationToken ct) => Task.FromResult("mock");
    }
    private sealed class PagesHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var uri = req.RequestUri!;
            Requests.Add(uri);
            Assert.Equal("ci.crm.dynamics.com", uri.Host);
            Assert.StartsWith("/api/data/v9.2/new_shipments", uri.AbsolutePath);
            var prefer = req.Headers.GetValues("Prefer").Single();
            Assert.Contains("odata.maxpagesize=", prefer);
            Assert.Contains("odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"", prefer);
            var continuation = uri.Query.Contains("skiptoken", StringComparison.OrdinalIgnoreCase);
            var content = continuation
                ? """{"value":[{"new_name":"Shipment 2"}]}"""
                : """{"@odata.count":2,"value":[{"new_name":"Shipment 1"}],"@odata.nextLink":"https://ci.crm.dynamics.com/api/data/v9.2/new_shipments?$skiptoken=mockpage2"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(content, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class TestingHost : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "ConnectorTests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static MetadataCatalog Catalog(bool withExtra = false)
    {
        var tables = new List<MetadataTable>
        {
            new("new_shipment","new_shipments","new_shipmentid","new_name",true,
                new Dictionary<int,string>(),
                [
                    new MetadataColumn("new_name","String",true,true,true,false,null,null,null,[],new Dictionary<int,string>(),[]),
                    new MetadataColumn("new_status","Picklist",true,true,true,false,null,null,null,[],new Dictionary<int,string>(),[])
                ], [], [new MetadataAlternateKey("new_name_key",["new_name"])])
        };
        if (withExtra) tables.Add(new("new_extra","new_extras","new_extraid",null,true,
            new Dictionary<int,string>(),[],[]));
        return new MetadataCatalog(tables);
    }

    [Fact]
    public async Task CrossConnectionPolicyQuotaCursorAndSavedTemplatesSurviveRealPostgres()
    {
        var cs = Environment.GetEnvironmentVariable("PHASE2_TEST_DB_CONNECTION");
        if (cs is null) return; // CI provisions disposable PostgreSQL before running this test.
        await using var db = NpgsqlDataSource.Create(cs);
        var first = Create(Guid.NewGuid());
        var second = Create(Guid.NewGuid());
        var actorId = Guid.NewGuid().ToString("D");
        const string issuer = "https://login.microsoftonline.com/phase05-tests/v2.0";
        await using var dbConn = await db.OpenConnectionAsync();
        foreach (var item in new[] { first, second })
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO dynamics.connections
                (id,code,dataverse_url,tenant_id,client_id,secret_nonce,secret_ciphertext,secret_tag,secret_key_version)
                VALUES(@id,@code,@host,@tenant,@client,@nonce,@cipher,@tag,1)
                """, dbConn);
            insert.Parameters.AddWithValue("id", item.Id);
            insert.Parameters.AddWithValue("code", item.Code);
            insert.Parameters.AddWithValue("host", item.DataverseUrl);
            insert.Parameters.AddWithValue("tenant", item.TenantId);
            insert.Parameters.AddWithValue("client", item.ClientId);
            insert.Parameters.AddWithValue("nonce", item.SecretNonce);
            insert.Parameters.AddWithValue("cipher", item.SecretCiphertext);
            insert.Parameters.AddWithValue("tag", item.SecretTag);
            await insert.ExecuteNonQueryAsync();
        }
        try
        {
            var catalog = new MetadataRepository(db);
            var clients = new ClientAccessRepository(db);
            var repository = new SavedQueryRepository(db);
            var reg = await clients.RegisterAsync(new ClientRegistrationRequest(issuer,actorId,"Phase05"),
                "ci",CancellationToken.None);
            foreach (var conn in new[] { first, second })
            {
                await catalog.SaveAsync(conn.Id,Catalog(),"manual","ci",CancellationToken.None);
                Assert.True(await catalog.SetEntityAsync(conn.Id,"new_shipment",true,"ci",CancellationToken.None));
                foreach (var field in new[] { "new_name", "new_status" })
                    Assert.True(await catalog.SetFieldAsync(conn.Id,"new_shipment",field,
                        new MetadataFieldToggle(true,true,false,false),"ci",CancellationToken.None));
            }
            var grant = await clients.UpsertGrantAsync(new GrantRequest(reg.Id,first.Code,
                "new_shipment","read",["new_name","new_status"],2),"ci",CancellationToken.None);
            Assert.NotNull(grant);
            var access = new ReadAccessRepository(new ConnectionFixture(first,second),catalog,clients);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("connector:issuer",issuer),new Claim("azp",actorId),
                new Claim("roles","dynamics.records.read")
            ],"test-signed-app"));

            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string,string?> {["READ_CURSOR_KEY_BASE64"] =
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}).Build();
            using var protector = new ReadCursorProtector(config,new TestingHost());
            var handler = new PagesHandler();
            var runtime = new ReadSearchService(new HttpClient(handler),new NoRealTokens(),access,protector);
            var query = new ReadSpec(["new_name"],new ReadPredicate(Field:"new_status",Op:"eq",
                Value:JsonSerializer.SerializeToElement(5)),PageSize:1,Count:true);
            var page1 = await runtime.SearchAsync(principal,"new_shipment",
                new ReadSearchRequest(first.Code,query),CancellationToken.None);
            Assert.Single(page1.Items);
            Assert.Equal(2,page1.Count);
            Assert.NotNull(page1.NextCursor);
            await Assert.ThrowsAsync<ReadRejected>(() => runtime.SearchAsync(principal,"new_shipment",
                new ReadSearchRequest(second.Code,null,page1.NextCursor),CancellationToken.None));
            var page2 = await runtime.SearchAsync(principal,"new_shipment",
                new ReadSearchRequest(first.Code,null,page1.NextCursor),CancellationToken.None);
            Assert.Equal("Shipment 2",page2.Items.Single().GetProperty("new_name").GetString());
            Assert.Null(page2.NextCursor);
            await Assert.ThrowsAsync<ReadRejected>(() => runtime.SearchAsync(principal,"new_shipment",
                new ReadSearchRequest(first.Code,query),CancellationToken.None)); // quota = 2
            Assert.Equal(2,handler.Requests.Count);

            var saved = await repository.SaveAsync(first.Id,
                new SavedQueryWrite("shipment_lookup","new_shipment",
                    new ReadSpec(["new_name"],new ReadPredicate(Field:"new_name",Op:"contains",
                        Parameter:"search")),[new ReadParamDefinition("search","string")],true),
                "ci",CancellationToken.None);
            Assert.False(saved.Enabled); // admin must enable separately
            Assert.Null(await repository.FindAsync(second.Id,"shipment_lookup",CancellationToken.None));
            Assert.True(await repository.SetEnabledAsync(first.Id,"shipment_lookup",true,"ci",CancellationToken.None));
            Assert.True((await repository.FindAsync(first.Id,"shipment_lookup",CancellationToken.None))!.Enabled);
            await repository.SaveAsync(first.Id,new SavedQueryWrite("shipment_lookup","new_shipment",
                saved.Query,saved.Parameters,true),"ci",CancellationToken.None);
            Assert.False((await repository.FindAsync(first.Id,"shipment_lookup",CancellationToken.None))!.Enabled);

            await catalog.SaveAsync(first.Id,Catalog(withExtra:true),"manual","ci",CancellationToken.None);
            await Assert.ThrowsAsync<ReadRejected>(() => runtime.SearchAsync(principal,"new_shipment",
                new ReadSearchRequest(first.Code,null,page1.NextCursor),CancellationToken.None));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("""
                DELETE FROM dynamics.api_clients WHERE issuer=@issuer AND oauth_client_id=@actor;
                DELETE FROM dynamics.connections WHERE id IN (@a,@b);
                """, dbConn);
            cleanup.Parameters.AddWithValue("issuer",issuer);
            cleanup.Parameters.AddWithValue("actor",actorId);
            cleanup.Parameters.AddWithValue("a",first.Id);
            cleanup.Parameters.AddWithValue("b",second.Id);
            await cleanup.ExecuteNonQueryAsync();
        }

        static DynamicsConnection Create(Guid id) => new(
            id,"phase05-"+id.ToString("N"),"https://ci.crm.dynamics.com",
            Guid.NewGuid(),Guid.NewGuid(),null,new byte[12],[5],new byte[16],
            1,DateTimeOffset.UtcNow);
    }
}
