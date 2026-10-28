using System.Text.Json;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class PricingQuoteTotalTests
{

    [Fact]
    public void PublishedQuoteNotificationMustUseExactIntegerPicklistMapping()
    {
        var valid = new IntegrationTemplate("quote", "create",
            [new TemplateInput("notificationChoice", "integer")],
            [new TemplateMapping("new_notifrespuestacotizacion", "notificationChoice")]);
        Assert.Equal("notificationChoice",
            PricingQuoteTotalEndpoints.ResponseNotificationInput(valid));

        var missing = valid with { Mappings =
            [new TemplateMapping("new_notif_iniciocotizacion", "notificationChoice")] };
        Assert.Null(PricingQuoteTotalEndpoints.ResponseNotificationInput(missing));

        var boolean = valid with { Inputs =
            [new TemplateInput("notificationChoice", "boolean")] };
        Assert.Null(PricingQuoteTotalEndpoints.ResponseNotificationInput(boolean));

        var duplicate = valid with { Mappings =
            [new TemplateMapping("new_notifrespuestacotizacion", "notificationChoice"),
             new TemplateMapping("new_notifrespuestacotizacion", "notificationChoice")] };
        Assert.Null(PricingQuoteTotalEndpoints.ResponseNotificationInput(duplicate));
    }

    [Fact]
    public void TrustedQuoteNotificationOverridesSuppliedChoiceOrMissingInput()
    {
        var supplied = new Dictionary<string, JsonElement>
        {
            ["notificationChoice"] = JsonSerializer.SerializeToElement(100000001),
            ["name"] = JsonSerializer.SerializeToElement("QUO-00000-000400")
        };
        var trusted = PricingQuoteTotalEndpoints.WithExactResponseNotification(
            "notificationChoice", supplied);
        Assert.Equal(100000000, trusted["notificationChoice"].GetInt32());
        Assert.Equal(100000001, supplied["notificationChoice"].GetInt32());
        Assert.Equal("QUO-00000-000400", trusted["name"].GetString());

        var omitted = PricingQuoteTotalEndpoints.WithExactResponseNotification(
            "notificationChoice", new Dictionary<string, JsonElement>());
        Assert.Equal(100000000, omitted["notificationChoice"].GetInt32());
    }

    [Fact]
    public void HistoricalCreateHeaderKeysResolveWithoutTreatingCurrentRevisionAsAbsent()
    {
        var rateId = Guid.Parse("a92de101-cbee-4741-9bed-082c58364616");
        var prefix = "dhole-pricing-quote-" + rateId.ToString("N");
        Assert.Equal(3, PricingQuoteTotalEndpoints.TryParseLegacyHeaderRevision(
            prefix + "-v3-header", rateId, 8));
        Assert.Equal(7, PricingQuoteTotalEndpoints.TryParseLegacyHeaderRevision(
            prefix + "-v7-r1-header", rateId, 8));
        Assert.Equal(8, PricingQuoteTotalEndpoints.TryParseLegacyHeaderRevision(
            prefix + "-v8-header", rateId, 8));
        Assert.Null(PricingQuoteTotalEndpoints.TryParseLegacyHeaderRevision(
            prefix + "-v9-header", rateId, 8));
        Assert.Null(PricingQuoteTotalEndpoints.TryParseLegacyHeaderRevision(
            prefix + "-v3-total", rateId, 8));
        Assert.Null(PricingQuoteTotalEndpoints.TryParseLegacyHeaderRevision(
            prefix + "-v3-header-extra", rateId, 8));
        Assert.Null(PricingQuoteTotalEndpoints.TryParseLegacyHeaderRevision(
            "dhole-pricing-quote-00000000000000000000000000000000-v3-header", rateId, 8));
    }

    [Theory]
    [InlineData("quote", "create", true)]
    [InlineData("quote", "upsert", true)]
    [InlineData("quote", "update", false)]
    [InlineData("quote", "delete", false)]
    [InlineData("account", "upsert", false)]
    public void RecoveryEligibilityUsesSameQuoteOperationsAsExecution(
        string entity, string operation, bool expected)
    {
        Assert.Equal(expected,
            PricingQuoteTotalEndpoints.IsPricingRecoveryOperation(entity, operation));
    }

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
    public void TextLengthRejectionStoresOnlyFieldAndBoundWithoutCustomerText()
    {
        var body = System.Text.Encoding.UTF8.GetBytes(
            """
            {"error":{"code":"0x80044331","message":
             "A validation error occurred. The length of the 'new_idtraasignado' attribute of the 'quote' entity exceeded the maximum allowed length of '30'. Sensitive customer AUTOMANIA and IDTRA999.12636"}}
            """);
        var reference = DataverseWriteTransport.ExtractSafeErrorReference(body);
        Assert.Equal("dataverse_code:0x80044331;field:new_idtraasignado;max:30", reference);
        Assert.DoesNotContain("AUTOMANIA", reference!);
        Assert.DoesNotContain("12636", reference!);
        Assert.Equal("0x80044331",
            DataverseWriteTransport.ExtractSafeErrorCode(body));
    }

    [Fact]
    public void UnknownOrMalformedDataverseTextMessageNeverPersistsRawText()
    {
        var body = System.Text.Encoding.UTF8.GetBytes(
            """
            {"error":{"code":"0x80044331","message":
             "Sensitive client AUTOMANIA exceeded some field with an unknown max."}}
            """);
        Assert.Equal("dataverse_code:0x80044331",
            DataverseWriteTransport.ExtractSafeErrorReference(body));
        var malicious = System.Text.Encoding.UTF8.GetBytes(
            """
            {"error":{"code":"0x80044331","message":
             "The length of the 'customerid;secret=AUTOMANIA' attribute of the 'quote' entity exceeded the maximum allowed length of '30'."}}
            """);
        Assert.Equal("dataverse_code:0x80044331",
            DataverseWriteTransport.ExtractSafeErrorReference(malicious));
        var wrongEntity = System.Text.Encoding.UTF8.GetBytes(
            """
            {"error":{"code":"0x80044331","message":
             "The length of the 'name' attribute of the 'account' entity exceeded the maximum allowed length of '100'."}}
            """);
        Assert.Equal("dataverse_code:0x80044331",
            DataverseWriteTransport.ExtractSafeErrorReference(wrongEntity));
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


    [Fact]
    public void ExistingQuoteLineUpdateKeepsOneTotalAndNeverRebindsParent()
    {
        const decimal newSale = 2750.25m;
        var fields = PricingQuoteTotalEndpoints.BuildLineUpdate(newSale);
        Assert.Equal(4, fields.Count);
        Assert.Equal(newSale, fields["priceperunit"].GetDecimal());
        Assert.Equal(1m, fields["quantity"].GetDecimal());
        Assert.Equal("Total venta de tarifa Dhole", fields["quotedetailname"].GetString());
        Assert.DoesNotContain("quoteid@odata.bind", fields.Keys);
        Assert.DoesNotContain("totalamount", fields.Keys);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PricingQuoteTotalEndpoints.BuildLineUpdate(-1m));
    }


}
