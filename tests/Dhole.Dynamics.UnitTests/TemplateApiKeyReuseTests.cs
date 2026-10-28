using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class TemplateApiKeyReuseTests
{
    private static (IntegrationTemplate Template, WritePolicy Policy) Prepare(
        string operation, bool allowUpdates = true, string identityName = "externalrateid")
    {
        var identity = new MetadataColumn(identityName,"String",true,true,true,
            false,"None",null,null,[],new Dictionary<int,string>(),[]);
        var freight = new MetadataColumn("new_flete","Money",true,true,allowUpdates,
            false,"None",null,null,[],new Dictionary<int,string>(),[]);
        var quote = new MetadataTable("quote","quotes","quoteid","name",false,
            new Dictionary<int,string>(),[identity,freight],[],
            [new MetadataAlternateKey("ak_quote_externalrateid",[identityName])]);
        var snapshot = new MetadataSnapshot(Guid.NewGuid(),Guid.NewGuid(),1,
            DateTimeOffset.UtcNow,"ci","test",new MetadataCatalog([quote]));
        var permissions = new MetadataPolicies(
            [new MetadataEntityPolicy("quote",true)],
            [
                new MetadataFieldPolicy("quote",identityName,true,true,true,true,
                    MetadataRepository.Fingerprint(identity)),
                new MetadataFieldPolicy("quote","new_flete",true,true,true,allowUpdates,
                    MetadataRepository.Fingerprint(freight))
            ]);
        var template = new IntegrationTemplate("quote",operation,
            [new TemplateInput("new_flete","decimal")],
            [new TemplateMapping("new_flete","new_flete")]);
        return (template,new WritePolicy(null!,snapshot,quote,permissions));
    }

    [Fact]
    public void UpsertReuseRequiresAnExistingApprovedDataverseAlternateKey()
    {
        var (template, policy) = Prepare("upsert");
        var (fields, keys) = TemplateApiKeyRepository.AuthorizeFields(
            template,policy,["externalrateid"]);
        Assert.Equal(["new_flete","externalrateid"],fields);
        Assert.Equal(["externalrateid"],keys);

        var missing=Assert.Throws<ReadRejected>(()=>
            TemplateApiKeyRepository.AuthorizeFields(template,policy,[]));
        Assert.Equal("approved_alternate_key_required",missing.Code);

        var unknown=Assert.Throws<ReadRejected>(()=>
            TemplateApiKeyRepository.AuthorizeFields(template,policy,["name"]));
        Assert.Equal("approved_alternate_key_required",unknown.Code);
    }

    [Fact]
    public void UpsertReuseRejectsFieldsWithoutUpdatePermission()
    {
        var (template,policy)=Prepare("upsert",allowUpdates:false);
        var exception=Assert.Throws<ReadRejected>(()=>
            TemplateApiKeyRepository.AuthorizeFields(
                template,policy,["externalrateid"]));
        Assert.Equal("crud_field_denied",exception.Code);
    }

    [Fact]
    public void CreateReuseKeepsFieldsAndDoesNotRequireAnAlternateKey()
    {
        var (template,policy)=Prepare("create");
        var (fields,keys)=TemplateApiKeyRepository.AuthorizeFields(
            template,policy,null);
        Assert.Equal(["new_flete"],fields);
        Assert.Empty(keys);

        var exception=Assert.Throws<ReadRejected>(()=>
            TemplateApiKeyRepository.AuthorizeFields(
                template,policy,["externalrateid"]));
        Assert.Equal("unexpected_alternate_key",exception.Code);
    }
    [Fact]
    public void PublishedPricingUpsertRecoversActiveAuthorizedRateIdKeyOnRebind()
    {
        var (template, policy) = Prepare("upsert", identityName: "cr933_dholerateid");
        var resolved = TemplateApiKeyRepository.ResolvePricingQuoteUpsertKeys(
            "principal", "crear_oferta", template, policy, []);
        Assert.Equal(["cr933_dholerateid"], resolved);
        var (fields, keys) = TemplateApiKeyRepository.AuthorizeFields(
            template, policy, resolved);
        Assert.Contains("new_flete", fields);
        Assert.Contains("cr933_dholerateid", fields);
        Assert.Equal(["cr933_dholerateid"], keys);
    }

    [Fact]
    public void AutomaticKeyRecoveryNeverGuessesAUnrelatedOrInactiveKey()
    {
        var (template, policy) = Prepare("upsert");
        var rejected = Assert.Throws<ReadRejected>(() =>
            TemplateApiKeyRepository.ResolvePricingQuoteUpsertKeys(
                "principal", "crear_oferta", template, policy, []));
        Assert.Equal("pricing_quote_rate_alternate_key_unavailable", rejected.Code);

        // Other CRUD integrations still require the operator's explicit key.
        Assert.Empty(TemplateApiKeyRepository.ResolvePricingQuoteUpsertKeys(
            "principal", "generic_offer_api", template, policy, [])!);
        Assert.Equal(["externalrateid"],
            TemplateApiKeyRepository.ResolvePricingQuoteUpsertKeys(
                "principal", "crear_oferta", template, policy,
                ["externalrateid"]));
    }

    [Fact]
    public void AutomaticKeyRecoveryAllowsLegacyReservedDholeGuidField()
    {
        var (template, policy) = Prepare("upsert", identityName: "new_idtraasignado");
        Assert.Equal(["new_idtraasignado"],
            TemplateApiKeyRepository.ResolvePricingQuoteUpsertKeys(
                "principal", "crear_oferta", template, policy, []));
    }

}
