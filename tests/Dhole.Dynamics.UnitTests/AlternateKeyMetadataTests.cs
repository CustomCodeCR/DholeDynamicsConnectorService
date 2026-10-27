using System.Text.Json;
using Dhole.Dynamics.Infrastructure;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class AlternateKeyMetadataTests
{
    [Theory]
    [InlineData(64, false, null)]
    [InlineData(450, false, null)]
    [InlineData(451, false, "key_size")]
    [InlineData(4000, false, "key_size")]
    [InlineData(64, true, "secured_field")]
    public void LiveStringMetadataIsCheckedBeforeCreatingAKey(
        int maxLength, bool secured, string? expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            LogicalName = "new_idtraasignado",
            MaxLength = maxLength,
            IsSecured = secured
        }));
        Assert.Equal(expected,
            DataverseMetadataDiscovery.QuoteKeyTextCompatibility(json.RootElement));
    }

    [Fact]
    public void UnknownOrWrongFieldMetadataDoesNotAllowAnIndex()
    {
        using var unknown = JsonDocument.Parse("{\"LogicalName\":\"new_idtraasignado\"}");
        using var wrong = JsonDocument.Parse(
            "{\"LogicalName\":\"other_field\",\"MaxLength\":64}");
        Assert.Equal("attribute",
            DataverseMetadataDiscovery.QuoteKeyTextCompatibility(unknown.RootElement));
        Assert.Equal("attribute",
            DataverseMetadataDiscovery.QuoteKeyTextCompatibility(wrong.RootElement));
    }

    [Fact]
    public void KeyPayloadUsesTheEnabledLabelLanguageAndExistingColumn()
    {
        var payload = DataverseMetadataDiscovery.BuildDholeQuoteKeyMetadata(
            "new_idtraasignado", 3082);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = json.RootElement;
        Assert.Equal("new_DholeKey_new_idtraasignado",
            root.GetProperty("SchemaName").GetString());
        Assert.Equal("new_idtraasignado",
            root.GetProperty("KeyAttributes")[0].GetString());
        var label = root.GetProperty("DisplayName").GetProperty("LocalizedLabels")[0];
        Assert.Equal(3082, label.GetProperty("LanguageCode").GetInt32());
        Assert.Equal("Microsoft.Dynamics.CRM.LocalizedLabel",
            label.GetProperty("@odata.type").GetString());
    }
}
