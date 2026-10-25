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
}
