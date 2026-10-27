using System.Text.Json;
using Dhole.Dynamics.Api;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class PricingQuoteTotalTests
{
    [Fact]
    public void TrustedPricingAmountsOverrideAllTemplateAmountsAndSalesLineUsesSale()
    {
        const decimal supplierCost = 271.08m;
        const decimal saleTotal = 443.00m;
        var untrusted = new Dictionary<string, JsonElement>
        {
            ["new_monto1"] = JsonSerializer.SerializeToElement(999m),
            ["new_monto2"] = JsonSerializer.SerializeToElement(250m),
            ["new_monto9"] = JsonSerializer.SerializeToElement(100m),
            ["new_flete"] = JsonSerializer.SerializeToElement(271.08m),
            ["name"] = JsonSerializer.SerializeToElement("QUO-00000-000355")
        };
        var amounts = PricingQuoteTotalEndpoints.WithExactQuoteAmounts(
            untrusted, supplierCost, saleTotal);
        Assert.Equal(supplierCost, amounts["new_monto1"].GetDecimal());
        foreach(var index in Enumerable.Range(2, 8))
            Assert.Equal(0m, amounts["new_monto" + index].GetDecimal());
        Assert.Equal(saleTotal, amounts["new_flete"].GetDecimal());
        Assert.Equal("QUO-00000-000355", amounts["name"].GetString());
        Assert.DoesNotContain("totalamount", amounts.Keys);

        var line = PricingQuoteTotalEndpoints.BuildLine(
            Guid.NewGuid(), saleTotal, Guid.NewGuid());
        Assert.Equal(1m, line["quantity"].GetDecimal());
        Assert.Equal(saleTotal, line["priceperunit"].GetDecimal());
        Assert.Equal("Tarifa logística Dhole", line["productdescription"].GetString());
    }

    [Theory]
    [InlineData(-1, 443)]
    [InlineData(271, -1)]
    public void InvalidTrustedAmountsCannotBeWritten(decimal cost, decimal sale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PricingQuoteTotalEndpoints.WithExactQuoteAmounts(
                new Dictionary<string, JsonElement>(), cost, sale));
    }

    [Fact]
    public void OneWriteInProductHasExactlyOneQuantityAndOriginalSalesPrice()
    {
        var quoteId = Guid.NewGuid();
        var rateId = Guid.NewGuid();
        var fields = PricingQuoteTotalEndpoints.BuildLine(rateId, 17755.25m,
            quoteId, "quoteid");

        Assert.Equal(7, fields.Count);
        Assert.Equal(quoteId.ToString("D"),
            fields["quoteid@odata.bind"].GetString());
        Assert.Equal(17755.25m, fields["priceperunit"].GetDecimal());
        Assert.Equal(1m, fields["quantity"].GetDecimal());
        Assert.True(fields["isproductoverridden"].GetBoolean());
        Assert.True(fields["ispriceoverridden"].GetBoolean());
        Assert.DoesNotContain("totalamount", fields.Keys);
        Assert.DoesNotContain("msdyn_invoicesetuptotals", fields.Keys);
    }

    [Fact]
    public void QuoteLineRejectsMissingIdsOrNegativePrice()
    {
        Assert.Throws<ArgumentException>(() =>
            PricingQuoteTotalEndpoints.BuildLine(Guid.Empty, 100m, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() =>
            PricingQuoteTotalEndpoints.BuildLine(Guid.NewGuid(), -1m, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() =>
            PricingQuoteTotalEndpoints.BuildLine(Guid.NewGuid(), 10m, Guid.Empty));
    }

    [Fact]
    public void NavigationPropertyIsTakenFromLiveMetadata()
    {
        var quoteId = Guid.NewGuid();
        var fields = PricingQuoteTotalEndpoints.BuildLine(
            Guid.NewGuid(), 500m, quoteId, "quoteid_custom");
        Assert.True(fields.ContainsKey("quoteid_custom@odata.bind"));
        Assert.False(fields.ContainsKey("quoteid@odata.bind"));
    }
    [Fact]
    public void DataverseErrorCodeIsPreservedWithoutLeakingMessageOrClientData()
    {
        var error = System.Text.Encoding.UTF8.GetBytes(
            """{"error":{"code":"0x80040265","message":"Sensitive customer AUTOMANIA"}}""");
        Assert.Equal("0x80040265",
            DataverseWriteTransport.ExtractSafeErrorCode(error));
        var malicious = System.Text.Encoding.UTF8.GetBytes(
            """{"error":{"code":"0x80040265;DROP TABLE","message":"secret"}}""");
        Assert.Null(DataverseWriteTransport.ExtractSafeErrorCode(malicious));
        var missing = System.Text.Encoding.UTF8.GetBytes(
            """{"error":{"message":"Sensitive data only"}}""");
        Assert.Null(DataverseWriteTransport.ExtractSafeErrorCode(missing));
    }

    [Fact]
    public void ItemizedQuoteLineBindsRealQuoteAndPreservesConceptQuantityAndPrice()
    {
        var id = Guid.NewGuid();
        var fields = PricingQuoteTotalEndpoints.BuildItemizedLine(
            new PricingQuoteTotalEndpoints.PricingQuoteLine(
                "  Transporte local  ", "  Entrega hasta bodega  ", 2m, 145.50m),
            id, "custom_quote_navigation");
        Assert.Equal(7, fields.Count);
        Assert.Equal(id.ToString("D"), fields["custom_quote_navigation@odata.bind"].GetString());
        Assert.Equal("Transporte local", fields["quotedetailname"].GetString());
        Assert.Equal("Entrega hasta bodega", fields["productdescription"].GetString());
        Assert.Equal(2m, fields["quantity"].GetDecimal());
        Assert.Equal(145.50m, fields["priceperunit"].GetDecimal());
        Assert.True(fields["ispriceoverridden"].GetBoolean());
        Assert.True(fields["isproductoverridden"].GetBoolean());
        Assert.DoesNotContain("totalamount", fields.Keys);
        Assert.DoesNotContain("msdyn_invoicesetuptotals", fields.Keys);
    }

    [Fact]
    public void ItemizedQuoteLineUsesNameWhenDescriptionIsEmpty()
    {
        var fields = PricingQuoteTotalEndpoints.BuildItemizedLine(
            new PricingQuoteTotalEndpoints.PricingQuoteLine("Manejos", "", 1m, 175m),
            Guid.NewGuid());
        Assert.Equal("Manejos", fields["productdescription"].GetString());
    }

    [Fact]
    public void ItemizedQuoteLineRejectsInvalidQuantityPriceNameOrQuote()
    {
        Assert.Throws<ArgumentException>(() =>
            PricingQuoteTotalEndpoints.BuildItemizedLine(
                new PricingQuoteTotalEndpoints.PricingQuoteLine("", "", 1m, 10m),
                Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() =>
            PricingQuoteTotalEndpoints.BuildItemizedLine(
                new PricingQuoteTotalEndpoints.PricingQuoteLine("Cargo", "", 0m, 10m),
                Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() =>
            PricingQuoteTotalEndpoints.BuildItemizedLine(
                new PricingQuoteTotalEndpoints.PricingQuoteLine("Cargo", "", 1m, -1m),
                Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() =>
            PricingQuoteTotalEndpoints.BuildItemizedLine(
                new PricingQuoteTotalEndpoints.PricingQuoteLine("Cargo", "", 1m, 10m),
                Guid.Empty));
        Assert.Throws<ArgumentException>(() =>
            PricingQuoteTotalEndpoints.BuildItemizedLine(
                new PricingQuoteTotalEndpoints.PricingQuoteLine("Cargo", "", 1m, 10m),
                Guid.NewGuid(), "bad@navigation"));
    }


}
