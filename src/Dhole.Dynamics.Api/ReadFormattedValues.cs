using System.Text.Json;
using Dhole.Dynamics.Contracts;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Preserve Dataverse raw values and add the standard OData formatted annotation
/// for authorized Choice fields if Dataverse omitted it.
/// </summary>
public static class ReadFormattedValues
{
    public const string AnnotationSuffix = "@OData.Community.Display.V1.FormattedValue";

    public static JsonElement AddChoiceLabels(
        JsonElement record, MetadataTable table, IReadOnlyList<string> selected)
    {
        if (record.ValueKind != JsonValueKind.Object || selected.Count == 0)
            return record.Clone();

        var additional = new List<KeyValuePair<string, string>>();
        foreach (var field in selected.Distinct(StringComparer.Ordinal))
        {
            var annotation = field + AnnotationSuffix;
            if (record.TryGetProperty(annotation, out _) ||
                !record.TryGetProperty(field, out var value))
                continue;

            var column = table.Columns.FirstOrDefault(c => c.LogicalName == field);
            if (column is null || column.Choices.Count == 0 ||
                column.Type is not ("Picklist" or "State" or "Status" or "MultiSelectPicklist"))
                continue;

            string? display = null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                display = LabelFor(column.Choices, number);
            else if (column.Type == "MultiSelectPicklist" && value.ValueKind == JsonValueKind.String)
            {
                var parts = value.GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length is > 0 and <= 50)
                {
                    var labels = parts.Select(part => int.TryParse(part, out var valueCode)
                        ? LabelFor(column.Choices, valueCode) : null).ToArray();
                    if (labels.All(label => !string.IsNullOrWhiteSpace(label)))
                        display = string.Join("; ", labels);
                }
            }

            if (!string.IsNullOrWhiteSpace(display))
                additional.Add(new KeyValuePair<string, string>(annotation, display));
        }

        if (additional.Count == 0) return record.Clone();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in record.EnumerateObject())
                property.WriteTo(writer);
            foreach (var label in additional)
                writer.WriteString(label.Key, label.Value);
            writer.WriteEndObject();
        }
        using var annotated = JsonDocument.Parse(stream.ToArray());
        return annotated.RootElement.Clone();
    }

    private static string? LabelFor(IReadOnlyList<MetadataChoice> choices, int value)
    {
        var choice = choices.FirstOrDefault(c => c.Value == value);
        if (choice is null) return null;
        if (choice.Labels.TryGetValue(3082, out var spanish) && !string.IsNullOrWhiteSpace(spanish))
            return spanish;
        if (choice.Labels.TryGetValue(1033, out var english) && !string.IsNullOrWhiteSpace(english))
            return english;
        return choice.Labels.OrderBy(entry => entry.Key).Select(entry => entry.Value)
            .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label));
    }
}
