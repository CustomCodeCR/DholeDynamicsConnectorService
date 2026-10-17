using System.Security.Cryptography;
using System.Security.Claims;
using System.Text.Json;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class Phase05ReadTests
{
    private static MetadataTable Table => new(
        "new_shipment", "new_shipments", "new_shipmentid", "new_name", true,
        new Dictionary<int,string>(),
        [
          Col("new_shipmentid", "Uniqueidentifier"), Col("new_name", "String"),
          Col("new_weight", "Decimal"), Col("new_status", "Picklist"),
          Col("new_loadedon", "DateTime"), Col("new_active", "Boolean")
        ], [],
        [new MetadataAlternateKey("new_shipment_key", ["new_name"])]);
    private static MetadataColumn Col(string name, string type) =>
        new(name, type, true, false, false, false, null, null, null, [],
            new Dictionary<int,string>(), []);
    private static IReadOnlySet<string> Allowed =>
        Table.Columns.Select(x=>x.LogicalName).ToHashSet(StringComparer.Ordinal);
    private static JsonElement Json<T>(T item) => JsonSerializer.SerializeToElement(item);

    [Fact]
    public void NestedTypedFiltersEscapeInjectionInAliases()
    {
        var spec = new ReadSpec(["new_shipmentid","new_name"],
            new ReadPredicate(Join:"and",Children:[
                new ReadPredicate(Field:"new_name",Op:"eq",Value:Json("O'Neil) or true")),
                new ReadPredicate(Join:"or",Children:[
                    new ReadPredicate(Field:"new_weight",Op:"gt",Value:Json(100.25m)),
                    new ReadPredicate(Field:"new_status",Op:"in",Value:Json(new[]{5,6}))
                ])
            ]),[new ReadSort("new_loadedon", true)],PageSize:25,Count:true);
        var query=ReadQueryCompiler.Compile(Table,spec,Allowed);
        var decoded=Uri.UnescapeDataString(query.RelativeUrl);
        Assert.Contains("new_name eq @p0", decoded);
        Assert.Contains("@p0='O''Neil) or true'",decoded);
        Assert.Contains("new_weight gt @p1",decoded);
        Assert.Contains("new_status eq @p2",decoded);
        Assert.Contains("new_status eq @p3",decoded);
        Assert.Contains("$count=true",decoded);
        Assert.DoesNotContain("new_name eq 'O'Neil",decoded);
        Assert.Contains("new_loadedon",query.RequiredFields);
    }

    [Fact]
    public void RejectsUnknownFieldsOperatorsAndWrongTypes()
    {
        Assert.Throws<UnauthorizedAccessException>(() => ReadQueryCompiler.Compile(Table,
            new ReadSpec(["secret"]),Allowed));
        Assert.Throws<ArgumentException>(() => ReadQueryCompiler.Compile(Table,
            new ReadSpec(["new_name"],new ReadPredicate(Field:"new_weight",Op:"contains",Value:Json("test"))),Allowed));
        Assert.Throws<ArgumentException>(() => ReadQueryCompiler.Compile(Table,
            new ReadSpec(["new_name"],new ReadPredicate(Field:"new_name",Op:"eq",Value:Json(1234))),Allowed));
        Assert.Throws<ArgumentException>(() => ReadQueryCompiler.Compile(Table,
            new ReadSpec(["new_name"],new ReadPredicate(Field:"new_name",Op:"startsWith",Value:Json("test"))),Allowed));
        Assert.Throws<ArgumentException>(() => ReadQueryCompiler.Compile(Table,
            new ReadSpec(["new_name"],PageSize:101),Allowed));
        Assert.Throws<ArgumentException>(() => ReadQueryCompiler.Compile(Table,
            new ReadSpec(["new_name"],new ReadPredicate(Field:"new_name",Op:"in",
                Value:Json(Enumerable.Range(1,20).ToArray()))),Allowed));
    }

    [Fact]
    public void SavedTemplateParametersNeverBecomeODataSyntax()
    {
        var spec=new ReadSpec(["new_name"],new ReadPredicate(Field:"new_name",Op:"contains",
            Parameter:"term"));
        var defs=new[]{new ReadParamDefinition("term","string")};
        var bound=ReadSavedQueryBinder.Bind(spec,defs,
            new Dictionary<string,JsonElement>{{"term",Json("x') or new_active eq true")}});
        var compiled=ReadQueryCompiler.Compile(Table,bound,Allowed);
        Assert.Contains("contains(new_name,@p0)",Uri.UnescapeDataString(compiled.RelativeUrl));
        Assert.Throws<ArgumentException>(()=>ReadSavedQueryBinder.Bind(spec,defs,
            new Dictionary<string,JsonElement>{{"term",Json(123)}}));
        Assert.Throws<ArgumentException>(()=>ReadSavedQueryBinder.Bind(spec,defs,
            new Dictionary<string,JsonElement>{{"other",Json("x")}}));
    }

    [Fact]
    public void CursorIsEncryptedAuthenticatedAndExpires()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {["READ_CURSOR_KEY_BASE64"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}).Build();
        using var protectedCursor=new ReadCursorProtector(config,new TestHost());
        var id=Guid.NewGuid();
        var payload=new ReadCursor(id,"https://identity.example","some-app","new_shipment",1,
            new ReadSpec(["new_name"]),"https://fixture.crm.dynamics.com/api/data/v9.2/new_shipments?$skiptoken=x",
            2,DateTimeOffset.UtcNow.AddMinutes(5),null);
        var token=protectedCursor.Seal(payload);
        Assert.DoesNotContain("skiptoken",token);
        Assert.Equal(id,protectedCursor.Open(token).ConnectionId);
        var bytes=token.ToCharArray();bytes[30]=bytes[30]=='A'?'B':'A';
        Assert.Throws<ReadRejected>(()=>protectedCursor.Open(new string(bytes)));
        Assert.Throws<ReadRejected>(()=>protectedCursor.Seal(payload with
        { ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1) }) is string expired
            ? protectedCursor.Open(expired) : throw new Exception());
    }

    [Fact]
    public void NextLinksCannotRedirectToAnotherHostOrEntitySet()
    {
        var conn=new DynamicsConnection(Guid.NewGuid(),"fixture","https://fixture.crm.dynamics.com",
            Guid.NewGuid(),Guid.NewGuid(),null,new byte[12],[1],new byte[16],1,DateTimeOffset.UtcNow);
        var catalog=new MetadataCatalog([Table]);
        var policy=new ReadPolicy(conn,new MetadataSnapshot(Guid.NewGuid(),conn.Id,1,
            DateTimeOffset.UtcNow,"test","manual",catalog),Table,Allowed);
        var safe="https://fixture.crm.dynamics.com/api/data/v9.2/new_shipments?$skiptoken=abcdef";
        Assert.Equal("fixture.crm.dynamics.com",ReadSearchService.ValidateUrl(policy,safe,true).Host);
        Assert.Throws<ReadRejected>(()=>ReadSearchService.ValidateUrl(policy,
            "https://attacker.example/api/data/v9.2/new_shipments?$skiptoken=1",true));
        Assert.Throws<ReadRejected>(()=>ReadSearchService.ValidateUrl(policy,
            "https://fixture.crm.dynamics.com/api/data/v9.2/accounts?$skiptoken=1",true));
        Assert.Throws<ReadRejected>(()=>ReadSearchService.ValidateUrl(policy,
            "http://fixture.crm.dynamics.com/api/data/v9.2/new_shipments?$skiptoken=1",true));
    }

    private sealed class TestHost : IHostEnvironment
    {
        public string EnvironmentName { get; set; }="Testing";
        public string ApplicationName { get; set; }="Test";
        public string ContentRootPath { get; set; }="/";
        public IFileProvider ContentRootFileProvider { get; set; }=new NullFileProvider();
    }
}
