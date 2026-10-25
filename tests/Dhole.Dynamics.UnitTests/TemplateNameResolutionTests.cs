using System.Text.Json;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class TemplateNameResolutionTests
{
    private static JsonElement String(string text) => JsonSerializer.SerializeToElement(text);

    [Fact]
    public async Task ClientAndIdtraNamesResolveToTheirRealRelatedIds()
    {
        var accountId = Guid.NewGuid();
        var incidentId = Guid.NewGuid();
        var mapping = new IntegrationTemplate("quote", "create",
            [
                new TemplateInput("customerid", "string"),
                new TemplateInput("new_idtra1", "string")
            ],
            [
                new TemplateMapping("customerid_account@odata.bind", "customerid",
                    [new TemplateStep("lookup_name", "name")]),
                new TemplateMapping("new_IDTRA1@odata.bind", "new_idtra1",
                    [new TemplateStep("lookup_name", "ticketnumber")])
            ]);
        var calls = new List<string>();
        var values = await TemplateMapper.BuildAsync(mapping,
            new Dictionary<string, JsonElement>
            {
                ["customerid"] = String("Renapa S.A."),
                ["new_idtra1"] = String("TRA-000123")
            }, Guid.NewGuid(),
            (_, _, _, _) => Task.FromResult<Guid?>(null),
            CancellationToken.None,
            (navigation, field, text, _) =>
            {
                calls.Add(navigation + "/" + field + "/" + text);
                return Task.FromResult<Guid?>(field == "name" ? accountId : incidentId);
            });
        Assert.Equal(accountId.ToString("D"), values["customerid_account@odata.bind"].GetString());
        Assert.Equal(incidentId.ToString("D"), values["new_IDTRA1@odata.bind"].GetString());
        Assert.Contains("customerid_account@odata.bind/name/Renapa S.A.", calls);
        Assert.Contains("new_IDTRA1@odata.bind/ticketnumber/TRA-000123", calls);
    }

    [Theory]
    [InlineData("incident", "new_idtra1", "ticketnumber", "title")]
    [InlineData("incident", "new_IDTRA1", "ticketnumber", "title")]
    [InlineData("incident", "new_idtra1", "title", "title")]
    [InlineData("incident", "customerid", "ticketnumber", "ticketnumber")]
    [InlineData("account", "new_idtra1", "ticketnumber", "ticketnumber")]
    [InlineData("incident", "new_otherlookup", "ticketnumber", "ticketnumber")]
    public void LegacyIdtraLookupUsesTitleOnlyForIdtra1(
        string entity, string attribute, string configured, string expected)
    {
        Assert.Equal(expected, TemplateOrchestrator.ResolveLookupNameField(
            entity, attribute, configured));
    }

    [Theory]
    [InlineData("incident", "new_idtra1", "ticketnumber", "title,ticketnumber")]
    [InlineData("incident", "new_IDTRA1", "title", "title,ticketnumber")]
    [InlineData("account", "customerid", "name", "name")]
    [InlineData("incident", "customerid", "ticketnumber", "ticketnumber")]
    public void IdtraLookupChecksOnlyApprovedExactNameColumns(
        string entity, string attribute, string field, string expected)
    {
        Assert.Equal(expected,
            string.Join(",", TemplateOrchestrator.ResolveLookupNameFields(
                entity, attribute, field)));
    }

    [Fact]
    public void CustomerLookupTriesFullNameThenAllDistinctiveWords()
    {
        var filters = TemplateOrchestrator.CustomerNameFallbackFilters(
            "name", "Grupo Castro Fallas S.A.");
        Assert.Equal(3, filters.Count);
        Assert.Equal("contains", filters[0].Op);
        Assert.Equal("Grupo Castro Fallas S.A.", filters[0].Value?.GetString());
        Assert.Equal("contains", filters[1].Op);
        Assert.Equal("GRUPO CASTRO FALLAS", filters[1].Value?.GetString());
        Assert.Equal("and", filters[2].Join);
        Assert.NotNull(filters[2].Children);
        Assert.Equal(3, filters[2].Children!.Count);
        Assert.All(filters[2].Children, part =>
        {
            Assert.Equal("name", part.Field);
            Assert.Equal("contains", part.Op);
        });
        Assert.Contains(filters[2].Children, part => part.Value?.GetString() == "GRUPO");
        Assert.Contains(filters[2].Children, part => part.Value?.GetString() == "CASTRO");
        Assert.Contains(filters[2].Children, part => part.Value?.GetString() == "FALLAS");
    }

    [Fact]
    public void CustomerLookupCanMatchWithoutLegalSuffix()
    {
        var filters = TemplateOrchestrator.CustomerNameFallbackFilters("name", "Renapa S.A.");
        Assert.Equal(2, filters.Count);
        Assert.Equal("RENAPA", filters[1].Value?.GetString());
    }

    [Theory]
    [InlineData("S.A.")]
    [InlineData("S.A. DE C.V.")]
    [InlineData("LTDA")]
    public void CustomerLookupNeverUsesOnlyLegalSuffix(string name)
    {
        Assert.Empty(TemplateOrchestrator.CustomerNameFallbackFilters("name", name));
    }

    [Fact]
    public async Task EquipmentLabelUsesMetadataChoiceWithoutGuessingItsCode()
    {
        var template = new IntegrationTemplate("quote", "create",
            [new TemplateInput("new_equipo", "string")],
            [new TemplateMapping("new_equipo", "new_equipo",
                [new TemplateStep("choice_label")])]);
        var values = await TemplateMapper.BuildAsync(template,
            new Dictionary<string, JsonElement> { ["new_equipo"] = String("40 HC FCL") },
            Guid.NewGuid(), (_, _, _, _) => Task.FromResult<Guid?>(null),
            CancellationToken.None, null,
            (name, label, _) => Task.FromResult<int?>(name == "new_equipo" &&
                label == "40 HC FCL" ? 100000015 : null));
        Assert.Equal(100000015, values["new_equipo"].GetInt32());
    }

    [Fact]
    public async Task MissingCustomerDoesNotGenerateAPlaceholderGuid()
    {
        var template = new IntegrationTemplate("quote", "create",
            [new TemplateInput("customerid", "string")],
            [new TemplateMapping("customerid_account@odata.bind", "customerid",
                [new TemplateStep("lookup_name", "name")])]);
        var ex = await Assert.ThrowsAsync<ReadRejected>(() => TemplateMapper.BuildAsync(
            template,new Dictionary<string,JsonElement>
            {
                ["customerid"] = String("Unknown customer")
            },Guid.NewGuid(),(_,_,_,_) => Task.FromResult<Guid?>(null),
            CancellationToken.None, (_,_,_,_) => Task.FromResult<Guid?>(null)));
        Assert.Equal("lookup_name_not_found",ex.Code);
        Assert.Equal("customerid",ex.LookupInput);
        Assert.Equal("name",ex.LookupSearchField);
    }
    [Fact]
    public async Task PricingQuoteOmitsUnknownCustomerButKeepsIdtraRelation()
    {
        var incidentId=Guid.NewGuid();
        var mapping=new IntegrationTemplate("quote","create",
            [
                new TemplateInput("customerid","string"),
                new TemplateInput("new_idtra1","string")
            ],
            [
                new TemplateMapping("customerid_account@odata.bind","customerid",
                    [new TemplateStep("lookup_name","name")]),
                new TemplateMapping("new_IDTRA1@odata.bind","new_idtra1",
                    [new TemplateStep("lookup_name","ticketnumber")])
            ]);
        var data=await TemplateMapper.BuildAsync(mapping,
            new Dictionary<string,JsonElement>
            {
                ["customerid"]=String("PRUEBA DYNAMICS"),
                ["new_idtra1"]=String("TRA-000123")
            },
            Guid.NewGuid(),(_,_,_,_)=>Task.FromResult<Guid?>(null),
            CancellationToken.None,
            (navigation,_,_,_)=>navigation.StartsWith("customerid_",StringComparison.Ordinal)
                ? Task.FromResult<Guid?>(null)
                : Task.FromResult<Guid?>(incidentId),
            omitMissingQuoteCustomer:true);
        Assert.False(data.ContainsKey("customerid_account@odata.bind"));
        Assert.Equal(incidentId.ToString("D"),data["new_IDTRA1@odata.bind"].GetString());
    }

    [Fact]
    public async Task PricingQuoteOmitsProviderNotFoundOnlyForCustomer()
    {
        var mapping=new IntegrationTemplate("quote","create",
            [new TemplateInput("customerid","string")],
            [new TemplateMapping("customerid_account@odata.bind","customerid",
                [new TemplateStep("lookup_name","name")])]);
        var values=await TemplateMapper.BuildAsync(mapping,
            new Dictionary<string,JsonElement> { ["customerid"]=String("PRUEBA DYNAMICS") },
            Guid.NewGuid(),(_,_,_,_)=>Task.FromResult<Guid?>(null),
            CancellationToken.None,(_,_,_,_)=>throw new ReadRejected(404,
                "lookup_name_not_found","customerid","name"),
            omitMissingQuoteCustomer:true);
        Assert.Empty(values);

        var error=await Assert.ThrowsAsync<ReadRejected>(()=>TemplateMapper.BuildAsync(
            mapping,new Dictionary<string,JsonElement>
            { ["customerid"]=String("PRUEBA DYNAMICS") },
            Guid.NewGuid(),(_,_,_,_)=>Task.FromResult<Guid?>(null),
            CancellationToken.None,(_,_,_,_)=>throw new ReadRejected(409,
                "lookup_name_ambiguous","customerid","name"),
            omitMissingQuoteCustomer:true));
        Assert.Equal("lookup_name_ambiguous",error.Code);
    }

    [Fact]
    public async Task MissingIdtraCannotBeSkippedEvenWhenPricingCustomerIsOptional()
    {
        var mapping=new IntegrationTemplate("quote","create",
            [new TemplateInput("new_idtra1","string")],
            [new TemplateMapping("new_IDTRA1@odata.bind","new_idtra1",
                [new TemplateStep("lookup_name","ticketnumber")])]);
        var error=await Assert.ThrowsAsync<ReadRejected>(()=>TemplateMapper.BuildAsync(
            mapping,new Dictionary<string,JsonElement>
            { ["new_idtra1"]=String("TRA-000123") },
            Guid.NewGuid(),(_,_,_,_)=>Task.FromResult<Guid?>(null),
            CancellationToken.None,(_,_,_,_)=>Task.FromResult<Guid?>(null),
            omitMissingQuoteCustomer:true));
        Assert.Equal("lookup_name_not_found",error.Code);
    }

    [Fact]
    public async Task OrdinaryTemplateDoesNotSilentlyOmitCustomer()
    {
        var mapping=new IntegrationTemplate("quote","create",
            [new TemplateInput("customerid","string")],
            [new TemplateMapping("customerid_account@odata.bind","customerid",
                [new TemplateStep("lookup_name","name")])]);
        var error=await Assert.ThrowsAsync<ReadRejected>(()=>TemplateMapper.BuildAsync(
            mapping,new Dictionary<string,JsonElement>
            { ["customerid"]=String("PRUEBA DYNAMICS") },
            Guid.NewGuid(),(_,_,_,_)=>Task.FromResult<Guid?>(null),
            CancellationToken.None,(_,_,_,_)=>Task.FromResult<Guid?>(null)));
        Assert.Equal("lookup_name_not_found",error.Code);
    }

    [Fact]
    public async Task PricingUsesFixedCurrencyGuidWithoutLegacyNameLookup()
    {
        const string currency = "b3a963da-2eda-e611-80f2-fc15b42826a0";
        var template = new IntegrationTemplate("quote", "create",
            [new TemplateInput("transactioncurrencyid", "string")],
            [new TemplateMapping("transactioncurrencyid@odata.bind",
                "transactioncurrencyid",
                [new TemplateStep("lookup_name", "isocurrencycode")])]);
        var called = false;
        var result = await TemplateMapper.BuildAsync(
            template,
            new Dictionary<string,JsonElement>
            { ["transactioncurrencyid"] = String(currency) },
            Guid.NewGuid(), (_,_,_,_) => Task.FromResult<Guid?>(null),
            CancellationToken.None, (_,_,_,_) =>
            {
                called = true;
                return Task.FromResult<Guid?>(null);
            },
            omitMissingQuoteCustomer: true);
        Assert.False(called);
        Assert.Equal(currency,
            result["transactioncurrencyid@odata.bind"].GetString());

        var ordinary = await Assert.ThrowsAsync<ReadRejected>(() =>
            TemplateMapper.BuildAsync(
                template,
                new Dictionary<string,JsonElement>
                { ["transactioncurrencyid"] = String(currency) },
                Guid.NewGuid(), (_,_,_,_) => Task.FromResult<Guid?>(null),
                CancellationToken.None, (_,_,_,_) => Task.FromResult<Guid?>(null)));
        Assert.Equal("lookup_name_not_found", ordinary.Code);
    }


}
