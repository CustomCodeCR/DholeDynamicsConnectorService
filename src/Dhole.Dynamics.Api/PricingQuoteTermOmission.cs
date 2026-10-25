using System.Text.Json;
using Dhole.Dynamics.Contracts;

namespace Dhole.Dynamics.Api;

/// <summary>
/// The dedicated Pricing -> Dynamics QUO integration intentionally leaves
/// three informational commercial-terms fields BLANK in Dataverse. Only this
/// route opts into filtering; administrators' ordinary CRUD templates remain
/// unchanged. Pricing retains the full original text and PDF.
/// </summary>
public static class PricingQuoteTermOmission
{
    private static readonly HashSet<string> ExcludedTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "new_noincluye", "new_tarifasujetaa", "new_tarifaincluye"
    };

    public static bool IsExcludedTarget(string target) =>
        ExcludedTargets.Contains(target);

    public static IntegrationTemplate FilterDefinition(IntegrationTemplate original)
    {
        if (original.EntityName != "quote" || original.Operation != "create")
            throw new ReadRejected(400, "pricing_quote_terms_only_for_quote_create");
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
        IntegrationTemplate effectiveDefinition,
        IReadOnlyDictionary<string, JsonElement> inputs)
    {
        var allowed = effectiveDefinition.Inputs.Select(i => i.Name)
            .ToHashSet(StringComparer.Ordinal);
        return inputs.Where(item => allowed.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }
}
