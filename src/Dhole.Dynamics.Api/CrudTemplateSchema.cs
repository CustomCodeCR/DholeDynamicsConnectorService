using Dhole.Dynamics.Contracts;

namespace Dhole.Dynamics.Api;

public sealed record CrudSchemaOption(string Label, object Value);
public sealed record CrudSchemaField(string Name, string Target, string Type,
    string DataverseType, bool Required, IReadOnlyList<CrudSchemaOption> Options);
public sealed record CrudTemplateSchema(string Name, int Version, string EntityName,
    string Operation, string ConnectionCode, IReadOnlyList<CrudSchemaField> Fields);

/// <summary>
/// Contract discovery for a specific published CRUD command. Uses Dataverse metadata
/// (not guessed global option-set IDs) and only the fields mapped by that command.
/// </summary>
public static class CrudTemplateSchemaBuilder
{
    public static CrudTemplateSchema Build(string name, string code, int version,
        IntegrationTemplate definition, WritePolicy policy)
    {
        TemplateMapper.ValidateDefinition(definition, policy);
        var fields = new List<CrudSchemaField>();
        foreach (var input in definition.Inputs)
        {
            var mapping = definition.Mappings.FirstOrDefault(m => m.Source == input.Name);
            var column = mapping is null ? null : policy.Table.Columns
                .FirstOrDefault(c => c.LogicalName == mapping.Target);
            var choices = new List<CrudSchemaOption>();
            if (column?.Type is "Picklist" or "State" or "Status" or "MultiSelectPicklist")
            {
                foreach (var item in column.Choices)
                    choices.Add(new(Label(item.Labels, item.Value.ToString()), item.Value));
            }
            else if (column?.Type == "EntityName")
            {
                var enabled = policy.Permissions.Entities.Where(e => e.Enabled)
                    .Select(e => e.LogicalName).ToHashSet(StringComparer.Ordinal);
                foreach (var table in policy.Snapshot.Catalog.Tables
                    .Where(t => enabled.Contains(t.LogicalName))
                    .OrderBy(t => Label(t.Labels, t.LogicalName), StringComparer.Ordinal)
                    .Take(500))
                    choices.Add(new(Label(table.Labels, table.LogicalName), table.LogicalName));
            }
            fields.Add(new CrudSchemaField(input.Name, mapping?.Target ?? "",
                input.Type, column?.Type ?? "", input.Required, choices));
        }
        return new(name, version, definition.EntityName, definition.Operation, code, fields);
    }

    public static string Label(IReadOnlyDictionary<int,string> labels, string fallback) =>
        labels.GetValueOrDefault(3082) ??
        labels.GetValueOrDefault(1033) ??
        labels.Values.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ??
        fallback;
}
