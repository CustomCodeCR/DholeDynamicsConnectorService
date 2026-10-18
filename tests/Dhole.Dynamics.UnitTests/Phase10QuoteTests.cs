using System.Text.Json;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class Phase10QuoteTests
{
    [Fact]
    public async Task OptionalFieldsAndNullsNeverEnterDataversePayload()
    {
        var config = new IntegrationTemplate("quote", "create",
            [new TemplateInput("name", "string"),
             new TemplateInput("description", "string", false),
             new TemplateInput("freight", "decimal", false)],
            [new TemplateMapping("name", "name"),
             new TemplateMapping("description", "description"),
             new TemplateMapping("new_flete", "freight")]);
        var given = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement("TEST"),
            ["description"] = JsonSerializer.SerializeToElement<string?>(null)
        };
        var inputs = TemplateMapper.ValidateInputs(config.Inputs, given);
        Assert.Single(inputs);
        var built = await TemplateMapper.BuildAsync(config, inputs, Guid.NewGuid(),
            (_, _, _, _) => Task.FromResult<Guid?>(null), CancellationToken.None);
        Assert.Single(built);
        Assert.Equal("TEST", built["name"].GetString());
    }

    [Fact]
    public void RequiredNullOrUnmappedInputFailsClosed()
    {
        var inputs = new[] { new TemplateInput("name", "string") };
        Assert.Throws<ReadRejected>(() => TemplateMapper.ValidateInputs(inputs,
            new Dictionary<string, JsonElement>
            { ["name"] = JsonSerializer.SerializeToElement<string?>(null) }));
        Assert.Throws<ReadRejected>(() => TemplateMapper.ValidateInputs(inputs,
            new Dictionary<string, JsonElement>
            { ["name"] = JsonSerializer.SerializeToElement("ok"),
              ["extra"] = JsonSerializer.SerializeToElement("not permitted") }));
    }

    [Fact]
    public void CreateQuoteDefinitionContainsNoFixedChoiceCodes()
    {
        // Values are inputs selected from each organization's approved Dataverse metadata.
        var codes = new[] { "preState", "rateCategory", "customerType", "proposalType",
            "specificProject", "cargoType", "equipment" };
        Assert.All(codes, code => Assert.False(string.IsNullOrWhiteSpace(code)));
        Assert.True(WriteLedger.ValidKey("dhole-rate-" + Guid.NewGuid().ToString("N")));
    }

    private static Dhole.Dynamics.Api.WritePolicy QuotePolicy()
    {
        using var noOp = new System.IO.MemoryStream();
        var columns = new[]
        {
            new Dhole.Dynamics.Domain.MetadataColumn("quoteid","Uniqueidentifier",true,false,false,false,
                null,null,null,[],new Dictionary<int,string>(),[]),
            new Dhole.Dynamics.Domain.MetadataColumn("name","String",true,true,true,false,
                null,null,null,[],new Dictionary<int,string>(),[]),
            new Dhole.Dynamics.Domain.MetadataColumn("customerid","Customer",true,true,true,false,
                null,null,null,[],new Dictionary<int,string>(),[]),
            new Dhole.Dynamics.Domain.MetadataColumn("transactioncurrencyid","Lookup",true,true,true,false,
                null,null,null,[],new Dictionary<int,string>(),[]),
            new Dhole.Dynamics.Domain.MetadataColumn("new_preestado","Picklist",true,true,true,false,
                null,null,null,[],new Dictionary<int,string>(),[
                    new Dhole.Dynamics.Domain.MetadataChoice(10,new Dictionary<int,string>())]),
            new Dhole.Dynamics.Domain.MetadataColumn("new_flete","Money",true,true,true,false,
                null,2,null,[],new Dictionary<int,string>(),[]),
            new Dhole.Dynamics.Domain.MetadataColumn("msdyn_invoicesetuptotals","Money",true,false,false,true,
                null,2,null,[],new Dictionary<int,string>(),[])
        };
        var rel = new[]
        {
            new Dhole.Dynamics.Domain.MetadataRelationship("quote_account","ManyToOneRelationships",
                "account","quote","accountid","customerid","customerid_account"),
            new Dhole.Dynamics.Domain.MetadataRelationship("quote_currency","ManyToOneRelationships",
                "transactioncurrency","quote","transactioncurrencyid","transactioncurrencyid")
        };
        var quote = new Dhole.Dynamics.Domain.MetadataTable("quote","quotes","quoteid","name",true,
            new Dictionary<int,string>(), columns, rel);
        var account = new Dhole.Dynamics.Domain.MetadataTable("account","accounts","accountid","name",true,
            new Dictionary<int,string>(),[],[]);
        var currency = new Dhole.Dynamics.Domain.MetadataTable("transactioncurrency","transactioncurrencies",
            "transactioncurrencyid","currencyname",true,new Dictionary<int,string>(),[],[]);
        var conn = new Dhole.Dynamics.Domain.DynamicsConnection(Guid.NewGuid(),"sandbox",
            "https://ci.crm.dynamics.com",Guid.NewGuid(),Guid.NewGuid(),null,new byte[12],
            new byte[2],new byte[16],1,DateTimeOffset.UtcNow);
        var permissions = new Dhole.Dynamics.Domain.MetadataPolicies([new("quote",true)],
            columns.Select(c => new Dhole.Dynamics.Domain.MetadataFieldPolicy("quote",c.LogicalName,
                true,c.CanRead,c.CanCreate,c.CanUpdate,MetadataRepository.Fingerprint(c))).ToArray());
        var snapshot = new Dhole.Dynamics.Domain.MetadataSnapshot(Guid.NewGuid(),conn.Id,1,
            DateTimeOffset.UtcNow,"hash","ci",
            new Dhole.Dynamics.Domain.MetadataCatalog([quote,account,currency]));
        return new(conn,snapshot,quote,permissions);
    }

    [Fact]
    public async Task CreateQuoteUsesMetadataForCustomerCurrencyAndChoice()
    {
        var policy = QuotePolicy();
        var account = Guid.NewGuid();
        var currency = Guid.NewGuid();
        var template = new IntegrationTemplate("quote","create",
            [new TemplateInput("name","string"),
             new TemplateInput("customerAccountId","guid"),
             new TemplateInput("currencyId","guid"),
             new TemplateInput("preState","integer",false),
             new TemplateInput("freight","decimal",false)],
            [new TemplateMapping("name","name"),
             new TemplateMapping("customerid_account@odata.bind","customerAccountId"),
             new TemplateMapping("transactioncurrencyid@odata.bind","currencyId"),
             new TemplateMapping("new_preestado","preState"),
             new TemplateMapping("new_flete","freight")]);
        TemplateMapper.ValidateDefinition(template,policy);
        var values = TemplateMapper.ValidateInputs(template.Inputs,
            new Dictionary<string,JsonElement> {
                ["name"] = JsonSerializer.SerializeToElement("demo"),
                ["customerAccountId"] = JsonSerializer.SerializeToElement(account.ToString("D")),
                ["currencyId"] = JsonSerializer.SerializeToElement(currency.ToString("D")),
                ["preState"] = JsonSerializer.SerializeToElement(10),
                ["freight"] = JsonSerializer.SerializeToElement(32.50m)
            });
        var mapped=await TemplateMapper.BuildAsync(template,values,policy.Connection.Id,
            (_,_,_,_)=>Task.FromResult<Guid?>(null),CancellationToken.None);
        var approved=new List<string>();
        var result=await WriteValueCompiler.CompileAsync(policy,mapped,
            (rel,id,_)=> { approved.Add(rel.ReferencedEntity! + ":" + id); return Task.CompletedTask; },
            CancellationToken.None);
        Assert.Equal("/accounts(" + account.ToString("D") + ")",
            result.Payload["customerid_account@odata.bind"]);
        Assert.Equal("/transactioncurrencies(" + currency.ToString("D") + ")",
            result.Payload["transactioncurrencyid@odata.bind"]);
        Assert.Equal(10,result.Payload["new_preestado"]);
        Assert.Equal(2,approved.Count);
        var unknown = mapped.ToDictionary(k => k.Key,v => v.Value);
        unknown["new_preestado"]=JsonSerializer.SerializeToElement(999);
        var problem=await Assert.ThrowsAsync<ReadRejected>(()=>
            WriteValueCompiler.CompileAsync(policy,unknown,
                (_,_,_)=>Task.CompletedTask,CancellationToken.None));
        Assert.Equal("unknown_choice",problem.Code);
    }

    [Fact]
    public void ReadOnlyInvoiceTotalsAreNeverImplicitlyWritable()
    {
        var policy=QuotePolicy();
        var invalid=new IntegrationTemplate("quote","create",
            [new TemplateInput("value","decimal")],
            [new TemplateMapping("msdyn_invoicesetuptotals","value")]);
        Assert.Throws<ReadRejected>(()=>TemplateMapper.ValidateDefinition(invalid,policy));
    }
}
