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
    public void QuoteGuidAlternateKeyMetadataIncludesDataverseLocalizedDisplayName()
    {
        var metadata=DataverseMetadataDiscovery.BuildDholeQuoteKeyMetadata(
            "new_idtraasignado");
        var json=System.Text.Json.JsonSerializer.SerializeToElement(metadata);
        Assert.Equal("Microsoft.Dynamics.CRM.EntityKeyMetadata",
            json.GetProperty("@odata.type").GetString());
        Assert.Equal("new_DholeKey_new_idtraasignado",
            json.GetProperty("SchemaName").GetString());
        Assert.Equal("new_idtraasignado",
            Assert.Single(json.GetProperty("KeyAttributes").EnumerateArray()).GetString());
        var label=json.GetProperty("DisplayName");
        Assert.Equal("Microsoft.Dynamics.CRM.Label",
            label.GetProperty("@odata.type").GetString());
        var localized=Assert.Single(label.GetProperty("LocalizedLabels").EnumerateArray());
        Assert.Equal("Dhole Rate GUID", localized.GetProperty("Label").GetString());
        Assert.Equal(1033, localized.GetProperty("LanguageCode").GetInt32());
        Assert.Throws<ArgumentException>(()=>
            DataverseMetadataDiscovery.BuildDholeQuoteKeyMetadata("new_idtra1"));
    }

    [Theory]
    [InlineData("DisplayName is required", "display_name")]
    [InlineData("Field level security is applied to column", "secured_field")]
    [InlineData("Alternate key index exceeds 900 bytes", "key_size")]
    [InlineData("SchemaName is not a valid publisher prefix", "schema_name")]
    [InlineData("An attribute is not eligible", "attribute")]
    public void DataverseKeyErrorClassificationReturnsOnlyAllowlistedReasons(
        string detail, string expected)
    {
        var json=System.Text.Json.JsonSerializer.Serialize(new
        {
            error=new { code="0x80040216",message=detail }
        });
        Assert.Equal(expected,DataverseMetadataDiscovery.ClassifyKeyCreationError(json));
        Assert.Equal("unknown",
            DataverseMetadataDiscovery.ClassifyKeyCreationError(
                "{\"error\":{\"message\":\"tenant secret\"}}"));
    }

    [Fact]
    public void ExistingQuoteStringColumnCanBeUsedForUniqueKeyButNotCalculatedFields()
    {
        var original=Column("new_idtraasignado","String");
        var quote=new MetadataTable("quote","quotes","quoteid","name",false,
            new Dictionary<int,string>(),[original],[]);
        Assert.True(MetadataEndpoints.CanCreateQuoteAlternateKey(quote,"new_idtraasignado"));
        Assert.False(MetadataEndpoints.CanCreateQuoteAlternateKey(quote,"nonexistent"));
        Assert.False(MetadataEndpoints.CanCreateQuoteAlternateKey(quote,"name"));
        var otherCustom=quote with { Columns=[original,Column("new_other_id","String")] };
        Assert.False(MetadataEndpoints.CanCreateQuoteAlternateKey(otherCustom,"new_other_id"));
        var nonQuote=quote with { LogicalName="account" };
        Assert.False(MetadataEndpoints.CanCreateQuoteAlternateKey(nonQuote,"new_idtraasignado"));
        var calculated=quote with { Columns=[original with { IsCalculated=true }] };
        Assert.False(MetadataEndpoints.CanCreateQuoteAlternateKey(calculated,"new_idtraasignado"));
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
    public void KeyRefreshShowsPendingInProgressAndFailedWithoutEnablingPrematureUpsert()
    {
        var fields=new[]{Column("new_idtraasignado","String")};
        var rows=new[]{
            System.Text.Json.JsonSerializer.SerializeToElement(new{
                LogicalName="new_dhole_pending",KeyAttributes=new[]{"new_idtraasignado"},
                EntityKeyIndexStatus=0
            }),
            System.Text.Json.JsonSerializer.SerializeToElement(new{
                LogicalName="new_dhole_building",KeyAttributes=new[]{"new_idtraasignado"},
                EntityKeyIndexStatus=1
            }),
            System.Text.Json.JsonSerializer.SerializeToElement(new{
                LogicalName="new_dhole_failed",KeyAttributes=new[]{"new_idtraasignado"},
                EntityKeyIndexStatus=3
            })
        };
        var states=DataverseMetadataDiscovery.ParseAlternateKeyStates(rows);
        Assert.Equal(3,states.Count);
        Assert.Contains(states,k=>k.Status=="Pending");
        Assert.Contains(states,k=>k.Status=="InProgress");
        Assert.Contains(states,k=>k.Status=="Failed");
        Assert.Empty(DataverseMetadataDiscovery.ActiveAlternateKeys(states,fields));
        var active=DataverseMetadataDiscovery.ParseAlternateKeyStates([
            System.Text.Json.JsonSerializer.SerializeToElement(new{
                LogicalName="new_dhole_active",KeyAttributes=new[]{"new_idtraasignado"},
                EntityKeyIndexStatus=2
            })]);
        Assert.Equal("new_dhole_active",
            Assert.Single(DataverseMetadataDiscovery.ActiveAlternateKeys(active,fields)).LogicalName);
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
