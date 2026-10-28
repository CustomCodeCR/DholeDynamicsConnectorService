using System.Text.Json;
using Dhole.Dynamics.Contracts;

namespace Dhole.Dynamics.Api;

/// <summary>
/// The dedicated Pricing -> Dynamics QUO integration omits the Potential
/// Customer lookup and three informational commercial terms. Pricing also
/// omits the customer name from the name/description input values. Only this route opts into
/// filtering; generic CRUD templates and manual Quotes remain unchanged.
/// Dataverse can accept ApplicationRequired fields omitted by API, although
/// tenant-specific synchronous rules or plugins may still reject the creation.
/// </summary>
public static class PricingQuoteTermOmission
{
    private static readonly HashSet<string> ExcludedTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "new_noincluye", "new_tarifasujetaa", "new_tarifaincluye"
    };

    public static bool IsExcludedTarget(string target) =>
        ExcludedTargets.Contains(target) ||
        target.Equals("customerid", StringComparison.OrdinalIgnoreCase) ||
        target.Equals("customeridtype", StringComparison.OrdinalIgnoreCase) ||
        (target.StartsWith("customerid_", StringComparison.OrdinalIgnoreCase) &&
         target.EndsWith("@odata.bind", StringComparison.Ordinal)) ||
        (target.StartsWith("msdyn_account", StringComparison.OrdinalIgnoreCase) &&
         target.EndsWith("@odata.bind", StringComparison.Ordinal));

    public static IntegrationTemplate FilterDefinition(IntegrationTemplate original)
    {
        // This scoped filter applies to both accepted-rate writes:
        // CREATE the header, or UPSERT that same header by its Dhole RateId.
        // Do not widen it to generic CRUD, UPDATE, or other entities.
        if (original.EntityName != "quote" ||
            original.Operation is not ("create" or "upsert"))
            throw new ReadRejected(400, "pricing_quote_terms_only_for_quote_write");
        var remaining = original.Mappings
            .Where(mapping => !IsExcludedTarget(mapping.Target)).ToArray();
        var usedSources = remaining.Where(mapping => mapping.Source is not null)
            .Select(mapping => mapping.Source!).ToHashSet(StringComparer.Ordinal);
        var removedSources = original.Mappings
            .Where(mapping => IsExcludedTarget(mapping.Target) &&
                mapping.Source is not null && !usedSources.Contains(mapping.Source))
            .Select(mapping => mapping.Source!).ToHashSet(StringComparer.Ordinal);
        return original with
        {
            Mappings = remaining,
            Inputs = original.Inputs
                .Where(input => !removedSources.Contains(input.Name)).ToArray()
        };
    }

    public static IReadOnlyDictionary<string, JsonElement> FilterInputs(
        IntegrationTemplate originalDefinition,
        IntegrationTemplate effectiveDefinition,
        IReadOnlyDictionary<string, JsonElement> inputs)
    {
        // Drop ONLY sources belonging exclusively to omitted quote targets.
        // An unexpected input must still be rejected by ValidateInputs rather
        // than being silently discarded from an authenticated API request.
        var retained = effectiveDefinition.Inputs.Select(i => i.Name)
            .ToHashSet(StringComparer.Ordinal);
        var removed = originalDefinition.Inputs
            .Where(input => !retained.Contains(input.Name))
            .Select(input => input.Name)
            .ToHashSet(StringComparer.Ordinal);
        return inputs.Where(item => !removed.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }
}
