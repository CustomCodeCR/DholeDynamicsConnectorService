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
    }
}
