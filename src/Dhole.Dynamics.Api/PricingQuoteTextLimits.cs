using System.Text.Json;
using Dhole.Dynamics.Contracts;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Validates the final trusted Pricing quote payload against the *live*
/// Dataverse String/Memo MaxLength values BEFORE any quote POST. Only
/// presentation labels may be abbreviated; original immutable Pricing data
/// (including legal relationships, IDTRA and conditions) is untouched.
/// </summary>
public static class PricingQuoteTextLimits
{
    private const string DescriptionSuffix = " … [Detalle completo en Dhole]";
    private const string ShortSuffix = "…";

    // Only human-readable display summaries. NEVER customerid, IDTRA,
    // financial values, conditions, binding GUIDs or custom business fields.
    private static readonly HashSet<string> DisplaySummaries = new(StringComparer.Ordinal)
    {
        "name", "description",
        "new_pol", "new_poe", "new_pod", "new_tiempodetransitoaprox"
    };

    public static IReadOnlyDictionary<string, JsonElement> Prepare(
        IntegrationTemplate definition,
        IReadOnlyDictionary<string, JsonElement> input,
        IReadOnlyDictionary<string, JsonElement> payload,
        IReadOnlyDictionary<string, int> limits)
    {
        Dictionary<string, JsonElement>? adjusted = null;
        foreach (var (field, value) in payload)
        {
            if (!limits.TryGetValue(field, out var max) ||
                value.ValueKind != JsonValueKind.String ||
                value.GetString() is not string output ||
                output.Length <= max)
                continue;

            if (!DisplaySummaries.Contains(field) || max < 8)
                throw TooLong(field);

            // Do not modify a shared input used by another mapping: that
            // could accidentally change customer lookups or commercial data.
            var mappings = definition.Mappings.Where(m => m.Target == field).ToArray();
            if (mappings.Length != 1 ||
                mappings[0].Source is not string source ||
                (mappings[0].Steps?.Any(s => s.Op is not ("trim" or "upper" or "lower")) ?? false) ||
                definition.Mappings.Count(m => m.Source == source) != 1 ||
                !input.TryGetValue(source, out var original) ||
                original.ValueKind != JsonValueKind.String)
                throw TooLong(field);

            var suffix = field == "description" && max >= DescriptionSuffix.Length + 12
                ? DescriptionSuffix : ShortSuffix;
            var size = max - suffix.Length;
            if (size < 5) throw TooLong(field);
            var originalText = original.GetString() ?? "";
            var clip = Math.Min(originalText.Length, size);
            if (clip > 0 && clip < originalText.Length &&
                char.IsHighSurrogate(originalText[clip - 1]))
                clip--;
            var prefix = originalText[..clip].TrimEnd();
            // Prefer ending on a word boundary when enough context survives.
            var lastSpace = prefix.LastIndexOf(' ');
            if (lastSpace > size * 3 / 5 && lastSpace > 8)
                prefix = prefix[..lastSpace].TrimEnd();
            var shorter = prefix + suffix;
            if (shorter.Length > max || shorter.Length < suffix.Length + 3)
                throw TooLong(field);

            adjusted ??= input.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            adjusted[source] = JsonSerializer.SerializeToElement(shorter);
        }
        return adjusted ?? input;
    }

    public static void Check(IReadOnlyDictionary<string, JsonElement> payload,
        IReadOnlyDictionary<string, int> limits)
    {
        foreach (var (field, value) in payload)
        {
            if (value.ValueKind != JsonValueKind.String ||
                !limits.TryGetValue(field, out var max) ||
                value.GetString() is not string text)
                continue;
            if (text.Length > max)
                throw TooLong(field);
        }
    }

    private static ReadRejected TooLong(string field) =>
        new(422, "quote_text_exceeds_dataverse_limit", field, "MaxLength");
}
