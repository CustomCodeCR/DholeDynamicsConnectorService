using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class Phase08ConsoleTests
{
    private static WritePolicy Policy(bool allowUpdate = true, bool drift = false)
    {
        var name = new MetadataColumn("new_name", "String", true, true, true, false,
            null, null, null, [], new Dictionary<int,string>(), []);
        var computed = new MetadataColumn("new_total", "Decimal", true, false, false, true,
            null, 2, null, [], new Dictionary<int,string>(), []);
        var table = new MetadataTable("new_shipment","new_shipments","new_shipmentid","new_name",
            true,new Dictionary<int,string>(),[name,computed],[]);
        var connection = new DynamicsConnection(Guid.NewGuid(),"ci","https://ci.crm.dynamics.com",
            Guid.NewGuid(),Guid.NewGuid(),null,new byte[12],[1],new byte[16],1,DateTimeOffset.UtcNow);
        var permissions=new MetadataPolicies([new("new_shipment",true)],
            [new("new_shipment","new_name",true,true,true,allowUpdate,
                drift?"outdated":MetadataRepository.Fingerprint(name)),
             new("new_shipment","new_total",true,true,true,true,
                MetadataRepository.Fingerprint(computed))]);
        return new(connection,new MetadataSnapshot(Guid.NewGuid(),connection.Id,1,
            DateTimeOffset.UtcNow,"checksum","manual",new MetadataCatalog([table])),
            table,permissions);
    }

    [Fact]
    public void AdminCrudMustRespectExplicitMetadataPolicies()
    {
        AdminRecordService.AssertFields(Policy(),["new_name"],"create");
        AdminRecordService.AssertFields(Policy(),["new_name"],"update");
        Assert.Throws<ReadRejected>(()=>AdminRecordService.AssertFields(
            Policy(allowUpdate:false),["new_name"],"update"));
        Assert.Throws<ReadRejected>(()=>AdminRecordService.AssertFields(
            Policy(drift:true),["new_name"],"create"));
        Assert.Throws<ReadRejected>(()=>AdminRecordService.AssertFields(
            Policy(),["new_total"],"create"));
        Assert.Throws<ReadRejected>(()=>AdminRecordService.AssertFields(
            Policy(),["hidden_column"],"update"));
        Assert.Throws<ReadRejected>(()=>AdminRecordService.AssertFields(
            Policy(),Array.Empty<string>(),"create"));
    }

    [Fact]
    public void ConsoleWritesStillRequireConcreteEtag()
    {
        Assert.Throws<ReadRejected>(()=>GenericWriteService.RequireEtag(null));
        Assert.Throws<ReadRejected>(()=>GenericWriteService.RequireEtag("*"));
        GenericWriteService.RequireEtag("W/\"42\"");
    }

    [Fact]
    public void SearchCannotExposeFieldsOutsideAllowlist()
    {
        var policy=Policy();
        var approved=new HashSet<string>(StringComparer.Ordinal){"new_name"};
        Assert.Throws<UnauthorizedAccessException>(()=>ReadQueryCompiler.Compile(
            policy.Table,new ReadSpec(["new_total"]),approved));
        var valid=ReadQueryCompiler.Compile(policy.Table,new ReadSpec(["new_name"],PageSize:10),approved);
        Assert.Contains("$select=",valid.RelativeUrl);
    }
}
