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
        var codes = new[] { "pre_state", "rate_category", "customer_type", "proposal_type",
            "specific_project", "cargo_type", "equipment" };
        Assert.All(codes, code => Assert.False(string.IsNullOrWhiteSpace(code)));
        Assert.True(WriteLedger.ValidKey("dhole-rate-" + Guid.NewGuid().ToString("N")));
    }

    [Theory]
    [InlineData("new_idtra1")]
    [InlineData("new_idtra2")]
    [InlineData("new_idtra9")]
    [InlineData("new_idtra10")]
    public void PricingIdtraRelationsSupportBothApprovedIncidentReferenceColumns(string relation)
    {
        Assert.Equal(new[] { "title", "ticketnumber" },
            TemplateOrchestrator.ResolveLookupNameFields("incident", relation, "title"));
        Assert.Equal(new[] { "title", "ticketnumber" },
            TemplateOrchestrator.ResolveLookupNameFields("incident", relation, "ticketnumber"));
        Assert.Equal(new[] { "name" },
            TemplateOrchestrator.ResolveLookupNameFields("account", relation, "name"));
    }

    [Fact]
    public void CustomerAccountLookupUsesContainsOnlyAndRecognizesFullLegalName()
    {
        const string pricing = "SOLUCIONES LUMINICAS ON SRL";
        const string dataverse = "SOLUCIONES LUMINICAS ON SOCIEDAD DE RESPONSABILIDAD LIMITADA";
        Assert.Equal("SOLUCIONES LUMINICAS ON",
            TemplateOrchestrator.CustomerAccountNameKey(pricing));
        Assert.Equal("SOLUCIONES LUMINICAS ON",
            TemplateOrchestrator.CustomerAccountNameKey(dataverse));
        Assert.True(TemplateOrchestrator.CustomerAccountNamesMatch(pricing, dataverse));
        Assert.False(TemplateOrchestrator.CustomerAccountNamesMatch(pricing,
            "SOLUCIONES LUMINICAS ON DEL NORTE SOCIEDAD DE RESPONSABILIDAD LIMITADA"));
        Assert.False(TemplateOrchestrator.CustomerAccountNamesMatch(pricing,
            "OTRA EMPRESA SOLUCIONES LUMINICAS ON SOCIEDAD DE RESPONSABILIDAD LIMITADA"));
        Assert.True(TemplateOrchestrator.CustomerAccountNamesMatch(
            "Compañía Náutica S.A.", "COMPANIA NAUTICA SOCIEDAD ANONIMA"));
        var filters = TemplateOrchestrator.CustomerNameFallbackFilters("name", pricing);
        Assert.NotEmpty(filters);
        Assert.All(filters, p =>
        {
            if (p.Children is { Count: > 0 } children)
                Assert.All(children, child => Assert.Equal("contains", child.Op));
            else
                Assert.Equal("contains", p.Op);
        });
        Assert.Contains(filters, p => p.Op == "contains" &&
            p.Value?.GetString() == "SOLUCIONES LUMINICAS ON");
    }

    [Fact]
    public void CustomerNameSearchCompilesToContainsWithoutODataEquals()
    {
        var name = new MetadataColumn("name","String",true,true,true,false,
            null,null,null,[],new Dictionary<int,string>(),[]);
        var id = new MetadataColumn("accountid","Uniqueidentifier",true,false,false,false,
            null,null,null,[],new Dictionary<int,string>(),[]);
        var table = new MetadataTable("account","accounts","accountid","name",true,
            new Dictionary<int,string>(),[id,name],[]);
        foreach (var filter in TemplateOrchestrator.CustomerNameFallbackFilters(
            "name", "SOLUCIONES LUMINICAS ON SRL"))
        {
            var query = ReadQueryCompiler.Compile(table,
                new ReadSpec(["accountid", "name"], filter, PageSize:25),
                new HashSet<string>(StringComparer.Ordinal) { "accountid", "name" });
            var odata = Uri.UnescapeDataString(query.RelativeUrl);
            Assert.Contains("contains(name,", odata, StringComparison.Ordinal);
            Assert.DoesNotContain("name eq ", odata, StringComparison.Ordinal);
        }
    }

    private static Dhole.Dynamics.Api.WritePolicy QuotePolicy()
    {
        var columns = new[]
        {
            new Dhole.Dynamics.Contracts.MetadataColumn("quoteid","Uniqueidentifier",true,false,false,false,
                null,null,null,[],new Dictionary<int,string>(),[]),
            new Dhole.Dynamics.Contracts.MetadataColumn("name","String",true,true,true,false,
                null,null,null,[],new Dictionary<int,string>(),[]),
            new Dhole.Dynamics.Contracts.MetadataColumn("customerid","Customer",true,true,true,false,
                null,null,null,[],new Dictionary<int,string>(),[]),
            new Dhole.Dynamics.Contracts.MetadataColumn("transactioncurrencyid","Lookup",true,true,true,false,
                null,null,null,[],new Dictionary<int,string>(),[]),
            new Dhole.Dynamics.Contracts.MetadataColumn("new_preestado","Picklist",true,true,true,false,
                null,null,null,[],new Dictionary<int,string>(),[
                    new Dhole.Dynamics.Contracts.MetadataChoice(10,new Dictionary<int,string>())]),
            new Dhole.Dynamics.Contracts.MetadataColumn("new_flete","Money",true,true,true,false,
                null,2,null,[],new Dictionary<int,string>(),[]),
            new Dhole.Dynamics.Contracts.MetadataColumn("msdyn_invoicesetuptotals","Money",true,false,false,true,
                null,2,null,[],new Dictionary<int,string>(),[])
        };
        var rel = new[]
        {
            new Dhole.Dynamics.Contracts.MetadataRelationship("quote_account","ManyToOneRelationships",
                "account","quote","accountid","customerid","customerid_account"),
            new Dhole.Dynamics.Contracts.MetadataRelationship("quote_currency","ManyToOneRelationships",
                "transactioncurrency","quote","transactioncurrencyid","transactioncurrencyid","transactioncurrencyid")
        };
        var quote = new Dhole.Dynamics.Contracts.MetadataTable("quote","quotes","quoteid","name",true,
            new Dictionary<int,string>(), columns, rel);
        var account = new Dhole.Dynamics.Contracts.MetadataTable("account","accounts","accountid","name",true,
            new Dictionary<int,string>(),[],[]);
        var currency = new Dhole.Dynamics.Contracts.MetadataTable("transactioncurrency","transactioncurrencies",
            "transactioncurrencyid","currencyname",true,new Dictionary<int,string>(),[],[]);
        var conn = new Dhole.Dynamics.Domain.DynamicsConnection(Guid.NewGuid(),"sandbox",
            "https://ci.crm.dynamics.com",Guid.NewGuid(),Guid.NewGuid(),null,new byte[12],
            new byte[2],new byte[16],1,DateTimeOffset.UtcNow);
        var permissions = new Dhole.Dynamics.Contracts.MetadataPolicies([new("quote",true)],
            columns.Select(c => new Dhole.Dynamics.Contracts.MetadataFieldPolicy("quote",c.LogicalName,
                true,c.CanRead,c.CanCreate,c.CanUpdate,MetadataRepository.Fingerprint(c))).ToArray());
        var snapshot = new Dhole.Dynamics.Contracts.MetadataSnapshot(Guid.NewGuid(),conn.Id,1,
            DateTimeOffset.UtcNow,"hash","ci",
            new Dhole.Dynamics.Contracts.MetadataCatalog([quote,account,currency]));
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
             new TemplateInput("customer_account_id","guid"),
             new TemplateInput("currency_id","guid"),
             new TemplateInput("pre_state","integer",false),
             new TemplateInput("freight","decimal",false)],
            [new TemplateMapping("name","name"),
             new TemplateMapping("customerid_account@odata.bind","customer_account_id"),
             new TemplateMapping("transactioncurrencyid@odata.bind","currency_id"),
             new TemplateMapping("new_preestado","pre_state"),
             new TemplateMapping("new_flete","freight")]);
        foreach(var mapping in template.Mappings)
        {
            var specific = template with {Mappings = [mapping]};
            try { TemplateMapper.ValidateDefinition(specific, policy); }
            catch(ReadRejected ex)
            {
                throw new InvalidOperationException("Metadata denies quote field " + mapping.Target, ex);
            }
        }
        TemplateMapper.ValidateDefinition(template,policy);
        var values = TemplateMapper.ValidateInputs(template.Inputs,
            new Dictionary<string,JsonElement> {
                ["name"] = JsonSerializer.SerializeToElement("demo"),
                ["customer_account_id"] = JsonSerializer.SerializeToElement(account.ToString("D")),
                ["currency_id"] = JsonSerializer.SerializeToElement(currency.ToString("D")),
                ["pre_state"] = JsonSerializer.SerializeToElement(10),
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
