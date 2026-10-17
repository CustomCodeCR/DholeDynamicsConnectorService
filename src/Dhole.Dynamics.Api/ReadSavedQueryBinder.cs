using System.Text.Json;
using Dhole.Dynamics.Contracts;

namespace Dhole.Dynamics.Api;

/// <summary>Binds typed parameters to filter AST nodes; parameter values never become OData syntax.</summary>
public static class ReadSavedQueryBinder
{
    public static ReadSpec Bind(ReadSpec template, IReadOnlyList<ReadParamDefinition> definitions,
        IReadOnlyDictionary<string, JsonElement> values, bool validateOnly = false)
    {
        if (definitions.Count > 20 || definitions.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != definitions.Count ||
            definitions.Any(d => !ReadQueryCompiler.ValidParam(d.Name) || d.Type is not
                ("string" or "guid" or "integer" or "decimal" or "boolean" or "datetime")))
            throw new ArgumentException("Invalid parameter schema.");
        var schema = definitions.ToDictionary(x => x.Name, StringComparer.Ordinal);
        if (values.Keys.Any(k => !schema.ContainsKey(k)))
            throw new ArgumentException("Unknown saved query parameter.");
        var used = new HashSet<string>(StringComparer.Ordinal);
        var predicates = 0;
        ReadPredicate? filter = template.Filter is null ? null : BindNode(template.Filter, 1);
        if (schema.Keys.Except(used).Any() || !validateOnly &&
            schema.Values.Any(d => d.Required && !values.ContainsKey(d.Name)))
            throw new ArgumentException("Missing or unused query parameter.");
        return template with { Filter = filter };

        ReadPredicate BindNode(ReadPredicate node, int depth)
        {
            if (++predicates > 24 || depth > 5) throw new ArgumentException("Template too complex.");
            if (node.Children is { Count: > 0 })
                return node with { Children = node.Children.Select(n => BindNode(n, depth+1)).ToArray() };
            if (node.Parameter is null) return node;
            if (node.Value is not null || !schema.TryGetValue(node.Parameter, out var definition))
                throw new ArgumentException("Invalid query parameter binding.");
            // Reusing the same named parameter in multiple filters is safe.
            used.Add(node.Parameter);
            JsonElement supplied;
            if (values.TryGetValue(node.Parameter, out var actual))
                supplied = actual;
            else if (validateOnly)
                supplied = Sample(definition!.Type, node.Op == "in");
            else if (definition!.Required)
                throw new ArgumentException("Required saved query parameter missing.");
            else
                supplied = JsonSerializer.SerializeToElement<object?>(null);
            if (!CheckType(definition!.Type, supplied, node.Op == "in"))
                throw new ArgumentException("Query parameter has wrong type.");
            return node with { Parameter = null, Value = supplied.Clone() };
        }
    }

    private static JsonElement Sample(string type, bool asArray)
    {
        object example = type switch
        {
            "string" => "example",
            "guid" => Guid.Empty.ToString("D"),
            "integer" => 1,
            "decimal" => 1.5m,
            "boolean" => true,
            "datetime" => "2026-01-01T00:00:00Z",
            _ => throw new ArgumentException("Bad type.")
        };
        return asArray ? JsonSerializer.SerializeToElement(new[] { example })
            : JsonSerializer.SerializeToElement(example);
    }

    private static bool CheckType(string type, JsonElement value, bool array)
    {
        if (array)
            return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is > 0 and <= 15 &&
                value.EnumerateArray().All(v => CheckType(type, v, false));
        if (value.ValueKind == JsonValueKind.Null) return true;
        return type switch
        {
            "string" => value.ValueKind == JsonValueKind.String && value.GetString()?.Length <= 256,
            "guid" => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out _),
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "decimal" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "datetime" => value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out _),
            _ => false
        };
    }
}
