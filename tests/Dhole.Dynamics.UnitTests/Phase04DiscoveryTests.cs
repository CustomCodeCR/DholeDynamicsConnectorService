using System.Net;
using System.Text;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Domain;
using Dhole.Dynamics.Infrastructure;
using Dhole.Dynamics.Api;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class Phase04DiscoveryTests
{
    private static readonly DynamicsConnection Organization = new(
        Guid.NewGuid(), "fixture", "https://fixture.crm.dynamics.com",
        Guid.NewGuid(), Guid.NewGuid(), null, new byte[12], [1], new byte[16], 1, DateTimeOffset.UtcNow);

    private sealed class FakeTokens : IDataverseAccessTokenProvider
    {
        public Task<string> AcquireAsync(DynamicsConnection connection, CancellationToken ct) =>
            Task.FromResult("test-token");
    }

    private sealed class FixtureHandler(bool includeCustom = true, bool changedField = false,
        bool globalChoiceWithEmptyLocal = false) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (uri.Scheme != "https" || uri.Host != "fixture.crm.dynamics.com"
                || uri.AbsolutePath.Contains("api/data") is false)
                throw new InvalidOperationException("Unsafe metadata URL");
            var json = uri.AbsolutePath switch
            {
                "/api/data/v9.2/EntityDefinitions" => includeCustom ? """
                    {"value":[
                      {"LogicalName":"account","EntitySetName":"accounts",
                       "PrimaryIdAttribute":"accountid","PrimaryNameAttribute":"name",
                       "IsCustomEntity":false,"DisplayName":{"LocalizedLabels":[{"LanguageCode":1033,"Label":"Account"}]}},
                      {"LogicalName":"new_shipments","EntitySetName":"new_shipments",
                       "PrimaryIdAttribute":"new_shipmentid","PrimaryNameAttribute":"new_name",
                       "IsCustomEntity":true}
                    ]}
                    """ : """
                    {"value":[{"LogicalName":"account","EntitySetName":"accounts",
                      "PrimaryIdAttribute":"accountid","PrimaryNameAttribute":"name",
                      "IsCustomEntity":false,"DisplayName":{"LocalizedLabels":[{"LanguageCode":1033,"Label":"Account"}]}}]}
                    """,
                "/api/data/v9.2/EntityDefinitions(LogicalName='new_shipments')" => """
                    {"LogicalName":"new_shipments","EntitySetName":"new_shipments",
                    "PrimaryIdAttribute":"new_shipmentid","PrimaryNameAttribute":"new_name",
                    "IsCustomEntity":true}
                    """,
                "/api/data/v9.2/EntityDefinitions(LogicalName='account')" => """
                    {"LogicalName":"account","EntitySetName":"accounts",
                    "PrimaryIdAttribute":"accountid","PrimaryNameAttribute":"name",
                    "IsCustomEntity":false}
                    """,
                "/api/data/v9.2/EntityDefinitions(LogicalName='account')/Attributes" => """
                    {"value":[{"LogicalName":"name","AttributeType":"String",
                    "IsValidForRead":true,"IsValidForCreate":true,"IsValidForUpdate":true,
                    "RequiredLevel":{"Value":"ApplicationRequired"}}]}
                    """,
                "/api/data/v9.2/EntityDefinitions(LogicalName='new_shipments')/Attributes" =>
                    changedField ? """
                    {"value":[{"LogicalName":"new_status","AttributeType":"Picklist",
                       "IsValidForRead":true,"IsValidForCreate":false,"IsValidForUpdate":false,"SourceType":1},
                       {"LogicalName":"new_name","AttributeType":"String","IsValidForRead":true}]}
                    """ : """
                    {"value":[{"LogicalName":"new_status","AttributeType":"Picklist",
                       "IsValidForRead":true,"IsValidForCreate":true,"IsValidForUpdate":true,"SourceType":0},
                       {"LogicalName":"new_name","AttributeType":"String","IsValidForRead":true}]}
                    """,
                var p when p.EndsWith("/Attributes(LogicalName='new_status')/Microsoft.Dynamics.CRM.PicklistAttributeMetadata", StringComparison.Ordinal) => globalChoiceWithEmptyLocal ? """
                    {"OptionSet":{"Options":[]},"GlobalOptionSet":{"Options":[{"Value":100000000,
                    "Label":{"LocalizedLabels":[{"LanguageCode":1033,"Label":"Enabled"}]}}]}}
                    """ : """
                    {"OptionSet":{"Options":[{"Value":100000002,"Label":{
                      "LocalizedLabels":[{"LanguageCode":3082,"Label":"Embarcado"},
                      {"LanguageCode":1033,"Label":"Shipped"}]}}]}}
                    """,
                var p when p.EndsWith("/Attributes(LogicalName='name')/Microsoft.Dynamics.CRM.StringAttributeMetadata", StringComparison.Ordinal)
                    || p.EndsWith("/Attributes(LogicalName='new_name')/Microsoft.Dynamics.CRM.StringAttributeMetadata", StringComparison.Ordinal) =>
                    """{"LogicalName":"name"}""",
                _ when uri.AbsolutePath.EndsWith("/Keys", StringComparison.Ordinal) =>
                    """{"value":[]}""",
                _ when uri.AbsolutePath.EndsWith("Relationships", StringComparison.Ordinal) =>
                    """{"value":[]}""",
                _ => throw new InvalidOperationException("Unexpected metadata query: " + uri.AbsolutePath)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task DiscoversNewCustomTableAndLocalizedChoiceWithoutEditingBackend()
    {
        var handler = new FixtureHandler();
        var reader = new DataverseMetadataDiscovery(new HttpClient(handler), new FakeTokens());
        var catalog = await reader.DiscoverAsync(Organization, CancellationToken.None);
        Assert.Contains(catalog.Tables, t => t.LogicalName == "new_shipments");
        Assert.All(catalog.Tables, t => Assert.Empty(t.Columns));
        Assert.Single(handler.Requests); // index never loops over every field/relationship
        var detail = await reader.DiscoverTableAsync(Organization, "new_shipments", CancellationToken.None,
            includeExtended: true);
        var column = detail.Columns.Single(c => c.LogicalName == "new_status");
        Assert.True(column.CanCreate);
        Assert.Equal("Embarcado", column.Choices.Single().Labels[3082]);
        Assert.Equal("Shipped", column.Choices.Single().Labels[1033]);
        Assert.All(handler.Requests, url => Assert.Equal("fixture.crm.dynamics.com", url.Host));
    }

    [Fact]
    public async Task ChoiceLookupFetchesOnlyOneApprovedFieldWithoutScanningAllAttributes()
    {
        var handler = new FixtureHandler();
        var reader = new DataverseMetadataDiscovery(new HttpClient(handler), new FakeTokens());
        var options = await reader.DiscoverChoicesAsync(
            Organization, "new_shipments", "new_status", "Picklist", CancellationToken.None);
        var choice = Assert.Single(options);
        Assert.Equal(100000002, choice.Value);
        Assert.Equal("Embarcado", choice.Labels[3082]);
        Assert.Equal("Shipped", choice.Labels[1033]);
        Assert.Single(handler.Requests);
        Assert.Contains("/Attributes(LogicalName='new_status')/", handler.Requests[0].AbsolutePath);
    }

    [Fact]
    public async Task ChoiceLookupReadsGlobalOptionsEvenWhenLocalSetIsEmpty()
    {
        var handler = new FixtureHandler(globalChoiceWithEmptyLocal: true);
        var reader = new DataverseMetadataDiscovery(new HttpClient(handler), new FakeTokens());
        var choices = await reader.DiscoverChoicesAsync(
            Organization, "new_shipments", "new_status", "Picklist", CancellationToken.None);
        var choice = Assert.Single(choices);
        Assert.Equal(100000000, choice.Value);
        Assert.Equal("Enabled", choice.Labels[1033]);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ChoiceLookupRejectsUntrustedFieldNamesAndUnsupportedTypes()
    {
        var handler = new FixtureHandler();
        var reader = new DataverseMetadataDiscovery(new HttpClient(handler), new FakeTokens());
        await Assert.ThrowsAsync<ArgumentException>(() => reader.DiscoverChoicesAsync(
            Organization, "new_shipments", "new_status')/other", "Picklist", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.DiscoverChoicesAsync(
            Organization, "new_shipments", "new_status", "String", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LightweightScanReturnsActualColumnsWithoutPerFieldRequests()
    {
        var handler = new FixtureHandler();
        var reader = new DataverseMetadataDiscovery(new HttpClient(handler), new FakeTokens());
        var table = await reader.DiscoverTableAsync(Organization, "new_shipments", CancellationToken.None);
        Assert.Contains(table.Columns, c => c.LogicalName == "new_status");
        Assert.Contains(table.Columns, c => c.LogicalName == "new_name");
        Assert.Empty(table.Relationships);
        Assert.Empty(table.AlternateKeys ?? []);
        Assert.Equal(3, handler.Requests.Count); // entity, Attributes, and registered alternate Keys
        Assert.Contains(handler.Requests, u => u.AbsolutePath.EndsWith("/Keys",StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, u => u.AbsolutePath.Contains("Attributes(LogicalName=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedCalculatedColumnLosesWriteCapabilityAndProducesDiff()
    {
        var original = new MetadataCatalog([await new DataverseMetadataDiscovery(
            new HttpClient(new FixtureHandler()), new FakeTokens())
            .DiscoverTableAsync(Organization, "new_shipments", CancellationToken.None,
                includeExtended: true)]);
        var changed = new MetadataCatalog([await new DataverseMetadataDiscovery(
            new HttpClient(new FixtureHandler(changedField: true)), new FakeTokens())
            .DiscoverTableAsync(Organization, "new_shipments", CancellationToken.None)]);
        var affected = changed.Tables.Single().Columns.Single(c => c.LogicalName == "new_status");
        Assert.True(affected.IsCalculated);
        Assert.False(affected.CanCreate);
        Assert.False(affected.CanUpdate);
        var diff = MetadataRepository.Diff(original, changed, 1, 2);
        Assert.Contains("new_shipments.new_status", diff.ChangedFields);
    }

    [Fact]
    public void DiffDetectsAddedAndRemovedTables()
    {
        var empty = new MetadataCatalog([]);
        var newer = new MetadataCatalog([new MetadataTable("new_custom", "new_customs",
            "new_customid", "new_name", true, new Dictionary<int, string>(), [], [])]);
        var added = MetadataRepository.Diff(empty, newer, 1, 2);
        Assert.Contains("new_custom", added.AddedEntities);
        var removed = MetadataRepository.Diff(newer, empty, 2, 3);
        Assert.Contains("new_custom", removed.RemovedEntities);
    }

    [Theory]
    [InlineData("new_port", true)]
    [InlineData("new_port;DROP", false)]
    [InlineData("accounts/records", false)]
    [InlineData("Account", false)]
    public void RejectsUnsafeIdentifiers(string input, bool expected) =>
        Assert.Equal(expected, DataverseMetadataDiscovery.SafeName(input));

    [Fact]
    public void NewActiveKeyFieldIsMergedWithoutOverwritingExistingMetadata()
    {
        var originalField = new MetadataColumn("name", "String", true, true, true,
            false, null, null, null, [], new Dictionary<int, string> { [3082] = "Nombre" }, []);
        var rateField = new MetadataColumn("cr933_dholerateid", "String", true, true, true,
            false, null, null, null, [], new Dictionary<int, string> { [1033] = "Dhole Rate ID" }, []);
        var current = new MetadataTable("quote", "quotes", "quoteid", "name", false,
            new Dictionary<int, string>(), [originalField], []);
        var fresh = current with { Columns = [
            originalField with { Labels = new Dictionary<int,string> { [1033] = "Name" } },
            rateField
        ] };
        var merged = MetadataRefreshService.MergeNewActiveKeyColumns(current, fresh,
            new HashSet<string>(StringComparer.Ordinal) { "cr933_dholerateid" });
        Assert.Same(originalField, merged.Columns[0]);
        Assert.Same(rateField, merged.Columns[1]);
        Assert.Equal("Nombre", merged.Columns[0].Labels[3082]);
        Assert.Empty(current.Columns.Where(c => c.LogicalName == "cr933_dholerateid"));
    }

    [Fact]
    public void MissingKeyFieldAndDifferentTableIdentityAreRejected()
    {
        var original = new MetadataTable("quote", "quotes", "quoteid", "name", false,
            new Dictionary<int, string>(), [], []);
        var required = new HashSet<string>(StringComparer.Ordinal) { "cr933_dholerateid" };
        Assert.Throws<InvalidOperationException>(() =>
            MetadataRefreshService.MergeNewActiveKeyColumns(original, original, required));
        Assert.Throws<InvalidOperationException>(() =>
            MetadataRefreshService.MergeNewActiveKeyColumns(original,
                original with { EntitySetName = "otherquotes" }, required));
    }
}
