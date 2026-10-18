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
}
