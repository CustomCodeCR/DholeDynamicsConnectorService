using System.Text.Json;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class PricingQuoteTermOmissionTests
{
    private static JsonElement J(string value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task DedicatedQuoteRouteOmitsAllThreeTextFieldsEvenWhenTemplateRequiresThem()
    {
        var customerId = Guid.NewGuid();
        var original = new IntegrationTemplate("quote", "create",
            [
                new TemplateInput("name", "string"),
                new TemplateInput("customer", "string"),
                new TemplateInput("noincluye", "string"),
                new TemplateInput("sujetaa", "string"),
                new TemplateInput("incluye", "string")
            ],
            [
                new TemplateMapping("name", "name"),
                new TemplateMapping("customerid_account@odata.bind", "customer",
                    [new TemplateStep("lookup_name", "name")]),
                new TemplateMapping("new_noincluye", "noincluye"),
                new TemplateMapping("new_tarifasujetaa", "sujetaa"),
                new TemplateMapping("new_tarifaincluye", "incluye")
            ]);
        var effective = PricingQuoteTermOmission.FilterDefinition(original);
        var raw = new Dictionary<string, JsonElement>
        {
            ["name"] = J("QUO-00000-000072"),
            ["customer"] = J("REINADO DE LAS LLANTAS"),
            ["noincluye"] = J(new string('X', 5000)),
            ["sujetaa"] = J(new string('Y', 5000)),
            ["incluye"] = J(new string('Z', 5000))
        };
        var filtered = PricingQuoteTermOmission.FilterInputs(original, effective, raw);
        var bound = TemplateMapper.ValidateInputs(effective.Inputs, filtered);
        var built = await TemplateMapper.BuildAsync(effective, bound, Guid.NewGuid(),
            (_, _, _, _) => Task.FromResult<Guid?>(null),
            CancellationToken.None,
            (_, _, _, _) => Task.FromResult<Guid?>(customerId));
        Assert.True(built.ContainsKey("name"));
        Assert.True(built.ContainsKey("customerid_account@odata.bind"));
        Assert.DoesNotContain("new_noincluye", built.Keys);
        Assert.DoesNotContain("new_tarifasujetaa", built.Keys);
        Assert.DoesNotContain("new_tarifaincluye", built.Keys);
        Assert.Equal(5, original.Mappings.Count);
        Assert.Equal(5, original.Inputs.Count);
        Assert.Equal(2, effective.Mappings.Count);
        Assert.Equal(2, effective.Inputs.Count);
    }

    [Fact]
    public void SharedSourceStillFeedsRequiredNonExcludedField()
    {
        var original = new IntegrationTemplate("quote", "create",
            [new TemplateInput("name", "string")],
            [
                new TemplateMapping("name", "name"),
                new TemplateMapping("new_noincluye", "name")
            ]);
        var effective = PricingQuoteTermOmission.FilterDefinition(original);
        Assert.Single(effective.Inputs);
        Assert.Single(effective.Mappings);
        Assert.Equal("name", effective.Mappings[0].Target);
        var filtered = PricingQuoteTermOmission.FilterInputs(original, effective,
            new Dictionary<string, JsonElement> { ["name"] = J("QUO") });
        Assert.Equal("QUO", TemplateMapper.ValidateInputs(effective.Inputs, filtered)["name"].GetString());
    }

    [Fact]
    public void GenericEntityCannotOptInToPricingOnlyPolicy()
    {
        var other = new IntegrationTemplate("account", "create",
            [new TemplateInput("name", "string")],
            [new TemplateMapping("name", "name")]);
        var ex = Assert.Throws<ReadRejected>(() => PricingQuoteTermOmission.FilterDefinition(other));
        Assert.Equal("pricing_quote_terms_only_for_quote_create", ex.Code);
    }

    [Fact]
    public void UnexpectedInputsAreNotSilentlyDiscarded()
    {
        var definition = new IntegrationTemplate("quote", "create",
            [new TemplateInput("name", "string"), new TemplateInput("includes", "string")],
            [new TemplateMapping("name", "name"),
             new TemplateMapping("new_tarifaincluye", "includes")]);
        var effective = PricingQuoteTermOmission.FilterDefinition(definition);
        var untrusted = new Dictionary<string, JsonElement>
        {
            ["name"] = J("QUO"),
            ["includes"] = J("Original Dhole terms"),
            ["unknown_parameter"] = J("MUST BE REJECTED")
        };
        var filtered = PricingQuoteTermOmission.FilterInputs(definition, effective, untrusted);
        Assert.False(filtered.ContainsKey("includes"));
        Assert.True(filtered.ContainsKey("unknown_parameter"));
        var ex = Assert.Throws<ReadRejected>(() => TemplateMapper.ValidateInputs(effective.Inputs, filtered));
        Assert.Equal("unexpected_template_input", ex.Code);
    }
}
