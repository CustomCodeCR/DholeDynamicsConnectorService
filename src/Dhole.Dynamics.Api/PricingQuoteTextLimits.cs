using System.Text.Json;
using Dhole.Dynamics.Contracts;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Field-length preflight against LIVE Dataverse metadata, for the narrow
/// trusted Pricing quote route. Original Pricing snapshot is immutable.
/// Only a generated description is shortened, with a visible pointer back
/// to the complete original quote; customer, IDTRA and business terms are
/// NEVER silently truncated.
/// </summary>
public static class PricingQuoteTextLimits
{
    private const string Suffix = " … [Detalle completo en Dhole]";

    public static IReadOnlyDictionary<string, JsonElement> Prepare(
        IntegrationTemplate definition, IReadOnlyDictionary<string, JsonElement> input,
        IReadOnlyDictionary<string, JsonElement> payload,
        IReadOnlyDictionary<string, int> limits)
    {
        if (!payload.TryGetValue("description", out var description) ||
            description.ValueKind != JsonValueKind.String ||
            !limits.TryGetValue("description", out var max) ||
            description.GetString() is not string text || text.Length <= max)
            return input;

        var mappings = definition.Mappings
            .Where(m => m.Target == "description" && m.Source is not null)
            .ToArray();
        if (mappings.Length != 1 ||
            (mappings[0].Steps?.Any(step => step.Op != "trim") ?? false) ||
            !input.TryGetValue(mappings[0].Source!, out var original) ||
            original.ValueKind != JsonValueKind.String ||
            max < Suffix.Length + 10)
            throw TooLong("description");

        var originalText = original.GetString() ?? "";
        var room = max - Suffix.Length;
        // Avoid splitting a surrogate pair at the Dataverse length boundary.
        var prefix = originalText.Length <= room ? originalText :
            originalText[..(room > 0 && char.IsHighSurrogate(originalText[room - 1])
                ? room - 1 : room)];
        var shorter = prefix.TrimEnd() + Suffix;
        if (shorter.Length > max) throw TooLong("description");
        var normalized = input.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
        {
            [mappings[0].Source!] = JsonSerializer.SerializeToElement(shorter)
        };
        return normalized;
    }

    public static void Check(IReadOnlyDictionary<string, JsonElement> payload,
        IReadOnlyDictionary<string, int> limits)
    {
        foreach (var (field, value) in payload)
        {
            if (value.ValueKind != JsonValueKind.String ||
                !limits.TryGetValue(field, out var limit) ||
                value.GetString() is not string text)
                continue;
            if (text.Length > limit)
                throw TooLong(field);
        }
    }

    private static ReadRejected TooLong(string field) =>
        new(422, "quote_text_exceeds_dataverse_limit", field, "MaxLength");
}
