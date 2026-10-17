using System.Globalization;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;

namespace Dhole.Dynamics.Api;

public sealed record CompiledRead(string RelativeUrl, IReadOnlyList<string> RequiredFields);

/// <summary>Compiles bounded, metadata-validated filters to OData 4 with encoded aliases.</summary>
public static class ReadQueryCompiler
{
    public const int MaxPageSize = 100;
    public const int MaxPages = 5;
    private const int MaxNodes = 24;
    private const int MaxDepth = 5;

    public static CompiledRead Compile(MetadataTable table, ReadSpec spec,
        IReadOnlySet<string> readable)
    {
        if (spec.PageSize is < 1 or > MaxPageSize ||
            spec.Select is not { Count: > 0 and <= 30 } ||
            (spec.Order?.Count ?? 0) > 4 || (spec.Expand?.Count ?? 0) > 2)
            throw new ArgumentException("Invalid query projection or limits.");
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in spec.Select) ValidateField(field);
        if (spec.Select.Distinct(StringComparer.Ordinal).Count() != spec.Select.Count)
            throw new ArgumentException("Duplicate $select field.");

        var query = new List<string>
        {
            "$select=" + Uri.EscapeDataString(string.Join(',', spec.Select))
        };
        if (spec.Filter is not null)
        {
            var aliases = new List<KeyValuePair<string,string>>();
            var count = 0;
            var expression = Parse(spec.Filter, 1, ref count, aliases);
            query.Add("$filter=" + Uri.EscapeDataString(expression));
            foreach (var pair in aliases) query.Add(pair.Key + "=" + Uri.EscapeDataString(pair.Value));
        }
        if (spec.Order is { Count: > 0 } sort)
        {
            foreach (var entry in sort) ValidateField(entry.Field);
            query.Add("$orderby=" + Uri.EscapeDataString(string.Join(',',
                sort.Select(x => x.Field + (x.Desc ? " desc" : " asc")))));
        }
        if (spec.Expand is { Count: > 0 })
            throw new ArgumentException("Expand requires a separately authorized relation plan.");
        if (spec.Count) query.Add("$count=true");
        if (query.Sum(x => x.Length) > 6000)
            throw new ArgumentException("Query is too large.");
        return new("/api/data/v9.2/" + table.EntitySetName + "?" + string.Join("&", query),
            all.Order(StringComparer.Ordinal).ToArray());

        void ValidateField(string field)
        {
            if (!DataverseMetadataDiscovery.SafeName(field)
                || table.Columns.All(c => c.LogicalName != field) || !readable.Contains(field))
                throw new UnauthorizedAccessException("Read field not approved.");
            all.Add(field);
        }

        string Parse(ReadPredicate node, int depth, ref int count,
            List<KeyValuePair<string,string>> aliases)
        {
            if (++count > MaxNodes || depth > MaxDepth)
                throw new ArgumentException("Filter depth or node limit exceeded.");
            if (node.Children is { Count: > 0 } children)
            {
                if (node.Field is not null || node.Value is not null || node.Parameter is not null
                    || node.Join is not ("and" or "or") || children.Count is < 2 or > 10)
                    throw new ArgumentException("Invalid logical filter.");
                var expressions = new List<string>();
                foreach (var child in children)
                    expressions.Add(Parse(child, depth + 1, ref count, aliases));
                return "(" + string.Join(" " + node.Join + " ", expressions) + ")";
            }

            if (node.Join is not null || node.Children is { Count: > 0 }
                || node.Parameter is not null || node.Field is null || node.Op is null)
                throw new ArgumentException("Invalid leaf predicate.");
            ValidateField(node.Field);
            var field = table.Columns.Single(c => c.LogicalName == node.Field);
            var op = node.Op;
            if (op is not ("eq" or "ne" or "gt" or "ge" or "lt" or "le" or "contains" or "in" or "null"))
                throw new ArgumentException("Unsupported comparison.");
            if (op == "null")
            {
                if (node.Value is not null) throw new ArgumentException("Null predicate must omit value.");
                return node.Field + " eq null";
            }
            var value = node.Value ?? throw new ArgumentException("Value is missing.");
            if (value.ValueKind == JsonValueKind.Null && op is "eq" or "ne")
                return node.Field + " " + op + " null";
            if (op == "in")
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 15)
                    throw new ArgumentException("IN requires 1–15 values.");
                var elems = new List<string>();
                foreach (var item in value.EnumerateArray())
                    elems.Add(node.Field + " eq " + Alias(field, item, op, aliases));
                return "(" + string.Join(" or ", elems) + ")";
            }
            if (op == "contains")
            {
                if (!StringType(field.Type) || value.ValueKind != JsonValueKind.String
                    || (value.GetString()?.Length ?? 0) is < 2 or > 100)
                    throw new ArgumentException("Contains supports 2–100 characters on text only.");
                return "contains(" + node.Field + "," + Alias(field, value, op, aliases) + ")";
            }
            if (op is "gt" or "ge" or "lt" or "le"
                && !OrderedType(field.Type))
                throw new ArgumentException("Comparison requires an ordered type.");
            return node.Field + " " + op + " " + Alias(field, value, op, aliases);
        }
    }

    public static string Alias(MetadataColumn column, JsonElement value, string operation,
        List<KeyValuePair<string,string>> aliases)
    {
        var literal = Literal(column, value);
        var key = "@p" + aliases.Count.ToString(CultureInfo.InvariantCulture);
        aliases.Add(new(key, literal));
        return key;
    }

    public static string Literal(MetadataColumn column, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            throw new ArgumentException("Null requires eq/ne or null operator.");
        switch (column.Type)
        {
            case "String":
            case "Memo":
            case "EntityName":
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 256 } s)
                    throw new ArgumentException("Invalid text value.");
                return "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
            case "Uniqueidentifier":
            case "Lookup":
            case "Customer":
            case "Owner":
                if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var guid))
                    throw new ArgumentException("Invalid GUID value.");
                return guid.ToString("D");
            case "Picklist":
            case "State":
            case "Status":
            case "Integer":
            case "BigInt":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var whole))
                    throw new ArgumentException("Invalid integer.");
                return whole.ToString(CultureInfo.InvariantCulture);
            case "Decimal":
            case "Double":
            case "Money":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
                    throw new ArgumentException("Invalid decimal.");
                return number.ToString(CultureInfo.InvariantCulture);
            case "Boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Invalid boolean.");
                return value.GetBoolean() ? "true" : "false";
            case "DateTime":
                if (value.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                    throw new ArgumentException("Invalid ISO timestamp.");
                return dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            default:
                throw new ArgumentException("Field type not eligible for generic filtering.");
        }
    }

    public static bool ValidParam(string? name) =>
        name is { Length: > 0 and <= 40 } && name[0] is >= 'a' and <= 'z' &&
        name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool StringType(string type) => type is "String" or "Memo" or "EntityName";
    private static bool OrderedType(string type) => type is "Integer" or "BigInt" or "Decimal" or "Double"
        or "Money" or "DateTime" or "Picklist" or "State" or "Status";
}
