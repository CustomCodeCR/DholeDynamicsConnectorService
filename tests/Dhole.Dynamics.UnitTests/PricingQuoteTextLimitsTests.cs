using System.Text.Json;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class PricingQuoteTextLimitsTests
{
    private static JsonElement J(string text) => JsonSerializer.SerializeToElement(text);

    [Fact]
    public void GeneratedDescriptionIsShortenedWithDholeReference()
    {
        var definition = new IntegrationTemplate("quote", "create",
            [new TemplateInput("description", "string")],
            [new TemplateMapping("description", "description")]);
        var original = new string('X', 200);
        var input = new Dictionary<string, JsonElement> { ["description"] = J(original) };
        var payload = new Dictionary<string, JsonElement> { ["description"] = J(original) };
        var safe = PricingQuoteTextLimits.Prepare(definition, input, payload,
            new Dictionary<string, int> { ["description"] = 70 });
        Assert.NotSame(input, safe);
        Assert.Equal(original, input["description"].GetString());
        var clipped = safe["description"].GetString()!;
        Assert.True(clipped.Length <= 70);
        Assert.Contains("Detalle completo en Dhole", clipped);
        PricingQuoteTextLimits.Check(new Dictionary<string, JsonElement>
            { ["description"] = J(clipped) },
            new Dictionary<string, int> { ["description"] = 70 });
    }

    [Fact]
    public void NeverTruncatesCustomerIdtraOrCommercialConditions()
    {
        var payload = new Dictionary<string, JsonElement>
        {
            ["new_idtraasignado"] = J("IDTRA123456789"),
            ["new_tarifasujetaa"] = J("Long commercial conditions")
        };
        var tooShort = new Dictionary<string, int>
        {
            ["new_idtraasignado"] = 8,
            ["new_tarifasujetaa"] = 5
        };
        var ex = Assert.Throws<ReadRejected>(() =>
            PricingQuoteTextLimits.Check(payload, tooShort));
        Assert.Equal("quote_text_exceeds_dataverse_limit", ex.Code);
        Assert.Equal("new_idtraasignado", ex.LookupInput);
    }

    [Fact]
    public void NoPreviewMutationWhenDescriptionFits()
    {
        var definition = new IntegrationTemplate("quote", "create",
            [new TemplateInput("description", "string")],
            [new TemplateMapping("description", "description")]);
        var input = new Dictionary<string, JsonElement> { ["description"] = J("Short") };
        Assert.Same(input, PricingQuoteTextLimits.Prepare(definition, input, input,
            new Dictionary<string, int> { ["description"] = 200 }));
    }

    [Theory]
    [InlineData("new_pol", 20)]
    [InlineData("new_poe", 20)]
    [InlineData("new_pod", 20)]
    [InlineData("new_tiempodetransitoaprox", 15)]
    public void DisplaySummariesFitRealDataverseLimitWithoutChangingSource(string field, int max)
    {
        var original = "QUO-00000-000072 REINADO DE LAS LLANTAS Xiamen China";
        var definition = new IntegrationTemplate("quote", "create",
            [new TemplateInput("label", "string")],
            [new TemplateMapping(field, "label", [new TemplateStep("trim")])]);
        var input = new Dictionary<string, JsonElement> { ["label"] = J(original) };
        var preview = new Dictionary<string, JsonElement> { [field] = J(original) };
        var bounds = new Dictionary<string, int> { [field] = max };
        var prepared = PricingQuoteTextLimits.Prepare(definition, input, preview, bounds);
        var clipped = prepared["label"].GetString()!;
        Assert.True(clipped.Length <= max);
        Assert.EndsWith("…", clipped);
        Assert.Equal(original, input["label"].GetString());
        PricingQuoteTextLimits.Check(new Dictionary<string, JsonElement>
            { [field] = J(clipped) }, bounds);
    }

    [Fact]
    public void MultipleAllowedTextSummariesAreAbbreviatedTogether()
    {
        var name = new string('N', 120);
        var description = new string('D', 300);
        var definition = new IntegrationTemplate("quote", "create",
            [new TemplateInput("new_pol", "string"), new TemplateInput("description", "string")],
            [new TemplateMapping("new_pol", "new_pol"),
             new TemplateMapping("description", "description")]);
        var input = new Dictionary<string, JsonElement>
        {
            ["new_pol"] = J(name), ["description"] = J(description)
        };
        var payload = new Dictionary<string, JsonElement>
        {
            ["new_pol"] = J(name), ["description"] = J(description)
        };
        var bounds = new Dictionary<string, int> { ["new_pol"] = 35, ["description"] = 80 };
        var prepared = PricingQuoteTextLimits.Prepare(definition, input, payload, bounds);
        Assert.True(prepared["new_pol"].GetString()!.Length <= 35);
        Assert.True(prepared["description"].GetString()!.Length <= 80);
        Assert.Contains("Detalle completo en Dhole", prepared["description"].GetString());
        Assert.Equal(name, input["new_pol"].GetString());
        Assert.Equal(description, input["description"].GetString());
    }

    [Fact]
    public void CompleteQuoteNameCannotBeSilentlyTruncated()
    {
        const string title =
            "QUO-00000-000233 - Tarifa 1 x 40 High Cube - FOB - Qingdao, China To San Jose, Costa Rica Via Caldera - EL REINADO DE LAS LLANTAS";
        var definition = new IntegrationTemplate("quote", "create",
            [new TemplateInput("name", "string")],
            [new TemplateMapping("name", "name")]);
        var values = new Dictionary<string, JsonElement> { ["name"] = J(title) };
        var bounds = new Dictionary<string, int> { ["name"] = 80 };
        var error = Assert.Throws<ReadRejected>(() =>
            PricingQuoteTextLimits.Prepare(definition, values, values, bounds));
        Assert.Equal("quote_text_exceeds_dataverse_limit", error.Code);
        Assert.Equal("name", error.LookupInput);
        Assert.Equal(title, values["name"].GetString());
    }

    [Fact]
    public void NeverShortensSourceSharedWithCustomerRelationship()
    {
        var original = new string('X', 150);
        var definition = new IntegrationTemplate("quote", "create",
            [new TemplateInput("same", "string")],
            [new TemplateMapping("name", "same"),
             new TemplateMapping("customerid_account@odata.bind", "same",
                 [new TemplateStep("lookup_name", "name")])]);
        var input = new Dictionary<string, JsonElement> { ["same"] = J(original) };
        var payload = new Dictionary<string, JsonElement> { ["name"] = J(original) };
        var ex = Assert.Throws<ReadRejected>(() =>
            PricingQuoteTextLimits.Prepare(definition, input, payload,
                new Dictionary<string, int> { ["name"] = 100 }));
        Assert.Equal("quote_text_exceeds_dataverse_limit", ex.Code);
        Assert.Equal("name", ex.LookupInput);
    }

    [Fact]
    public void UnapprovedTextFieldFailsWithItsActualName()
    {
        var original = new string('X', 300);
        var definition = new IntegrationTemplate("quote", "create",
            [new TemplateInput("idtra", "string")],
            [new TemplateMapping("new_idtraasignado", "idtra")]);
        var data = new Dictionary<string, JsonElement> { ["idtra"] = J(original) };
        var preview = new Dictionary<string, JsonElement>
            { ["new_idtraasignado"] = J(original) };
        var ex = Assert.Throws<ReadRejected>(() =>
            PricingQuoteTextLimits.Prepare(definition, data, preview,
                new Dictionary<string, int> { ["new_idtraasignado"] = 100 }));
        Assert.Equal("new_idtraasignado", ex.LookupInput);
    }
}
