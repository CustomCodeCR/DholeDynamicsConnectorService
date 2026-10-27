using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class Phase16CrudOptionSchemaTests
{
    private static MetadataColumn Column(string name, string type,
        IReadOnlyList<MetadataChoice>? choices=null) => new(
            name,type,true,true,true,false,"None",null,null,
            [],new Dictionary<int,string>{{3082,name}},choices??[]);

    private static WritePolicy Policy()
    {
        var status=Column("statuscode","Status",
            [new MetadataChoice(1,new Dictionary<int,string>{{3082,"Activo"},{1033,"Active"}}),
             new MetadataChoice(2,new Dictionary<int,string>{{3082,"Inactivo"}})]);
        var entity=Column("objecttypecode","EntityName");
        var quote=new MetadataTable("quote","quotes","quoteid","name",false,
            new Dictionary<int,string>{{3082,"Oferta"}},[status,entity],[]);
        var account=new MetadataTable("account","accounts","accountid","name",false,
            new Dictionary<int,string>{{3082,"Cuenta"}},[],[]);
        var snapshot=new MetadataSnapshot(Guid.NewGuid(),Guid.NewGuid(),1,
            DateTimeOffset.UtcNow,"fingerprint","ci",new MetadataCatalog([quote,account]));
        var policies=new MetadataPolicies(
            [new MetadataEntityPolicy("quote",true),new MetadataEntityPolicy("account",true)],
            [new MetadataFieldPolicy("quote","statuscode",true,false,true,true,
                MetadataRepository.Fingerprint(status)),
             new MetadataFieldPolicy("quote","objecttypecode",true,false,true,true,
                MetadataRepository.Fingerprint(entity))]);
        return new WritePolicy(null!,snapshot,quote,policies);
    }

    [Fact]
    public void ActiveDataverseAlternateKeysAreExposedAndPendingKeysAreIgnored()
    {
        var cols = new[] {
            Column("new_idtraasignado","String"),
            Column("name","String")
        };
        var rows = new[] {
            System.Text.Json.JsonSerializer.SerializeToElement(new {
                LogicalName="new_dhole_quote_key",
                KeyAttributes=new[]{"new_idtraasignado"},
                EntityKeyIndexStatus="Active"
            }),
            System.Text.Json.JsonSerializer.SerializeToElement(new {
                LogicalName="new_pending_key",
                KeyAttributes=new[]{"name"},
                EntityKeyIndexStatus="Pending"
            }),
            System.Text.Json.JsonSerializer.SerializeToElement(new {
                LogicalName="new_failed_key",
                KeyAttributes=new[]{"name"},
                EntityKeyIndexStatus="Failed"
            }),
        };
        var result=DataverseMetadataDiscovery.ParseActiveAlternateKeys(rows,cols);
        var single=Assert.Single(result);
        Assert.Equal("new_dhole_quote_key",single.LogicalName);
        Assert.Equal(["new_idtraasignado"],single.Attributes);
    }

    [Fact]
    public void SchemaUsesRealChoiceNumbersAndLocalizedLabelsNotGuessedIds()
    {
        var definition=new IntegrationTemplate("quote","create",
            [new TemplateInput("estado","integer"),
             new TemplateInput("tabla","string")],
            [new TemplateMapping("statuscode","estado"),
             new TemplateMapping("objecttypecode","tabla")]);
        var schema=CrudTemplateSchemaBuilder.Build("crear_oferta","principal",1,
            definition,Policy());
        Assert.Equal("quote",schema.EntityName);
        var choice=Assert.Single(schema.Fields.Where(f=>f.Name=="estado"));
        Assert.Equal("Status",choice.DataverseType);
        Assert.Equal(2,choice.Options.Count);
        Assert.Contains(choice.Options,x=>x.Label=="Activo" && (int)x.Value==1);
        Assert.Contains(choice.Options,x=>x.Label=="Inactivo" && (int)x.Value==2);
        var logicalNames=Assert.Single(schema.Fields.Where(f=>f.Name=="tabla"));
        Assert.Equal("EntityName",logicalNames.DataverseType);
        Assert.Contains(logicalNames.Options,x=>x.Label=="Cuenta" && (string)x.Value=="account");
        Assert.Contains(logicalNames.Options,x=>x.Label=="Oferta" && (string)x.Value=="quote");
    }

    [Fact]
    public void SchemaRejectsMappingsWithoutMetadataApproval()
    {
        var definition=new IntegrationTemplate("quote","create",
            [new TemplateInput("nombre","string")],
            [new TemplateMapping("unapproved_field","nombre")]);
        var rejection=Assert.Throws<ReadRejected>(()=>
            CrudTemplateSchemaBuilder.Build("crear_oferta","principal",1,
                definition,Policy()));
        Assert.Equal(403,rejection.Status);
    }
}
