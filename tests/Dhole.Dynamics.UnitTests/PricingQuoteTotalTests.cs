using System.Text.Json;
using Dhole.Dynamics.Api;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class PricingQuoteTotalTests
{
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

}
