using System.Text.Json;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class Phase07TemplateTests
{
    private static JsonElement J<T>(T x)=>JsonSerializer.SerializeToElement(x);
    private static WritePolicy Policy()
    {
        var col=new MetadataColumn("new_name","String",true,true,true,false,
            null,null,null,[],new Dictionary<int,string>(),[]);
        var other=new MetadataColumn("new_weight","Decimal",true,true,true,false,
            null,4,null,[],new Dictionary<int,string>(),[]);
        var nav=new MetadataColumn("new_parentid","Lookup",true,true,true,false,
            null,null,null,[],new Dictionary<int,string>(),[]);
        var table=new MetadataTable("new_shipments","new_shipments","new_shipmentid","new_name",true,
            new Dictionary<int,string>(),[col,other,nav],[
                new MetadataRelationship("parent","ManyToOneRelationships","account",
                    "new_shipments","accountid","new_parentid","new_Parent")
            ]);
        var account=new MetadataTable("account","accounts","accountid","name",false,
            new Dictionary<int,string>(),[],[]);
        var conn=new DynamicsConnection(Guid.NewGuid(),"ci","https://ci.crm.dynamics.com",
            Guid.NewGuid(),Guid.NewGuid(),null,new byte[12],[4],new byte[16],1,DateTimeOffset.UtcNow);
        var permissions=new MetadataPolicies([new("new_shipments",true)],
            table.Columns.Select(c=>new MetadataFieldPolicy(table.LogicalName,c.LogicalName,true,true,true,true,
                MetadataRepository.Fingerprint(c))).ToArray());
        return new(conn,new MetadataSnapshot(Guid.NewGuid(),conn.Id,1,DateTimeOffset.UtcNow,
            "fingerprint","ci",new MetadataCatalog([table,account])),table,permissions);
    }

    [Fact]
    public void ApiKeyGrantsOnlyApprovedLookupAttributesNotNavigationProperties()
    {
        var policy=Policy();
        var template=new IntegrationTemplate("new_shipments","create",
            [new TemplateInput("name","string"),
             new TemplateInput("parent","string")],
            [new TemplateMapping("new_name","name"),
             new TemplateMapping("new_Parent@odata.bind","parent",
                 [new TemplateStep("lookup_name","name")])]);
        TemplateMapper.ValidateDefinition(template,policy);
        var fields=TemplateApiKeyRepository.ApprovedFields(template,policy);
        Assert.Contains("new_name",fields);
        Assert.Contains("new_parentid",fields);
        Assert.DoesNotContain("new_Parent@odata.bind",fields);
    }

    [Theory]
    [InlineData("other_parent@odata.bind")]
    [InlineData("new_Parent@odata.bind@odata.bind")]
    [InlineData("new_parentid@other")]
    [InlineData("new_parentid.fake")]
    public void ApiKeysRejectUndeclaredOrInjectedNavigationTargets(string target)
    {
        var template=new IntegrationTemplate("new_shipments","create",
            [new TemplateInput("parent","string")],
            [new TemplateMapping(target,"parent")]);
        var e=Assert.Throws<ReadRejected>(()=>
            TemplateApiKeyRepository.ApprovedFields(template,Policy()));
        Assert.Equal("crud_lookup_mapping_not_approved",e.Code);
    }

    [Fact]
    public async Task NewlyConfiguredOperationMapsInputsWithoutCompilingNewController()
    {
        var definition=new IntegrationTemplate("new_shipments","create",
            [new TemplateInput("po","string",Pattern:"identifier"),
             new TemplateInput("weight","decimal")],
            [new TemplateMapping("new_name","po",[new TemplateStep("trim"),
                new TemplateStep("upper"),new TemplateStep("concat",Argument:"-READY")]),
             new TemplateMapping("new_weight","weight",[new TemplateStep("unit",Argument:"lb_to_kg")])]);
        TemplateMapper.ValidateDefinition(definition,Policy());
        var input=TemplateMapper.ValidateInputs(definition.Inputs,new Dictionary<string,JsonElement>
        {["po"]=J("PO123"),["weight"]=J(100m)});
        var values=await TemplateMapper.BuildAsync(definition,input,Policy().Connection.Id,
            (_,_,_,_)=>Task.FromResult<Guid?>(null),CancellationToken.None);
        Assert.Equal("PO123-READY",values["new_name"].GetString());
        Assert.Equal(45.3592m,values["new_weight"].GetDecimal());
        var typed=await WriteValueCompiler.CompileAsync(Policy(),values,
            (_,_,_)=>Task.CompletedTask,CancellationToken.None);
        Assert.Equal("PO123-READY",typed.Payload["new_name"]);
    }

    [Fact]
    public async Task LookupRefNeverChoosesOrganizationOrTargetFromUntrustedInput()
    {
        var target=Guid.NewGuid();
        var definition=new IntegrationTemplate("new_shipments","create",
            [new TemplateInput("dholeid","string")],
            [new TemplateMapping("new_Parent@odata.bind","dholeid",
                [new TemplateStep("lookup_ref",Argument:"dhole")])]);
        TemplateMapper.ValidateDefinition(definition,Policy());
        var data=await TemplateMapper.BuildAsync(definition,
            TemplateMapper.ValidateInputs(definition.Inputs,new Dictionary<string,JsonElement>
                {["dholeid"]=J("123")}),Policy().Connection.Id,
            (space,id,navigation,_)=> {
                Assert.Equal("dhole",space);
                Assert.Equal("123",id);
                Assert.Equal("new_Parent@odata.bind",navigation);
                return Task.FromResult<Guid?>(target);
            },CancellationToken.None);
        Assert.Equal(target.ToString("D"),data["new_Parent@odata.bind"].GetString());
        await Assert.ThrowsAsync<ReadRejected>(()=>TemplateMapper.BuildAsync(definition,
            TemplateMapper.ValidateInputs(definition.Inputs,new Dictionary<string,JsonElement>
                {["dholeid"]=J("missing")}),Policy().Connection.Id,
            (_,_,_,_)=>Task.FromResult<Guid?>(null),CancellationToken.None));
    }

    [Fact]
    public void BlocksScriptHttpAndUnapprovedFieldsAtDefinitionTime()
    {
        var baseline=new IntegrationTemplate("new_shipments","create",
            [new TemplateInput("po","string")],
            [new TemplateMapping("new_name","po")]);
        TemplateMapper.ValidateDefinition(baseline,Policy());
        foreach(var transform in new[]{"eval","javascript","http","shell","fetch"})
        {
            var unsafeTemplate=baseline with {Mappings=[
                new TemplateMapping("new_name","po",[new TemplateStep(transform)])]};
            Assert.Throws<ReadRejected>(()=>TemplateMapper.ValidateDefinition(unsafeTemplate,Policy()));
        }
        Assert.Throws<ReadRejected>(()=>TemplateMapper.ValidateDefinition(
            baseline with {Mappings=[new TemplateMapping("hidden_field","po")]},Policy()));
        Assert.Throws<ReadRejected>(()=>TemplateMapper.ValidateDefinition(
            baseline with {Mappings=[new TemplateMapping("http://evil","po")]},Policy()));
        Assert.Throws<ReadRejected>(()=>TemplateMapper.ValidateInputs(baseline.Inputs,
            new Dictionary<string,JsonElement>{{"po",J("x")},{"unsafe",J("y")}}));
    }

    [Fact]
    public async Task ConditionalChoiceAndDateTransformsAreFinite()
    {
        var definition=new IntegrationTemplate("new_shipments","create",
            [new TemplateInput("title","string"),new TemplateInput("backup","string")],
            [new TemplateMapping("new_name","title",[
                new TemplateStep("if_empty",OtherInput:"backup"),
                new TemplateStep("lower")])]);
        TemplateMapper.ValidateDefinition(definition,Policy());
        var output=await TemplateMapper.BuildAsync(definition,
            TemplateMapper.ValidateInputs(definition.Inputs,new Dictionary<string,JsonElement>
                {["title"]=J(""),["backup"]=J("NUEVO")}),Policy().Connection.Id,
            (_,_,_,_)=>Task.FromResult<Guid?>(null),CancellationToken.None);
        Assert.Equal("nuevo",output["new_name"].GetString());
    }
    [Fact]
    public void CrudTemplatesAccept64FieldsAndRejectMoreThan64()
    {
        var inputs=Enumerable.Range(1,64)
            .Select(n=>new TemplateInput("field"+n,"string"))
            .ToArray();
        var values=Enumerable.Range(1,64)
            .ToDictionary(n=>"field"+n,n=>J("value"+n));
        var template=new IntegrationTemplate("new_shipments","create",inputs,
            [new TemplateMapping("new_name","field1")]);
        TemplateMapper.ValidateDefinition(template,Policy());
        var output=TemplateMapper.ValidateInputs(inputs,values);
        Assert.Equal(64,output.Count);
        var tooMany=inputs.Append(new TemplateInput("field65","string")).ToArray();
        Assert.Throws<ReadRejected>(()=>
            TemplateMapper.ValidateDefinition(template with {Inputs=tooMany},Policy()));
        var excess=values.ToDictionary(x=>x.Key,x=>x.Value);
        excess.Add("field65",J("extra"));
        Assert.Throws<ReadRejected>(()=>
            TemplateMapper.ValidateInputs(tooMany,excess));
    }

}
