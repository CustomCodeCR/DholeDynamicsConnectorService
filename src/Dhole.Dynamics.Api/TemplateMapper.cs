using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;

namespace Dhole.Dynamics.Api;

/// <summary>Small, finite declarative mapping DSL. No scripts, HTTP destinations, reflection or OData expressions.</summary>
public static class TemplateMapper
{
    private static readonly string[] Types=["string","guid","integer","decimal","boolean","datetime"];
    private static readonly HashSet<string> Steps=new(StringComparer.Ordinal)
    {
        "trim","upper","lower","date_utc","decimal","if_empty","concat",
        "choice_constant","unit","lookup_ref"
    };

    public static void ValidateDefinition(IntegrationTemplate definition, WritePolicy policy)
    {
        if(definition.Operation is not ("create" or "update" or "upsert" or "delete" or "read" or "find" or "action")
            || definition.Inputs is not { Count: <= 30 } || definition.Mappings is not { Count: <= 40 }
            || definition.EntityName!=policy.Table.LogicalName)
            throw new ReadRejected(400,"invalid_template_definition");
        if(definition.Inputs.Any(i=>!ReadQueryCompiler.ValidParam(i.Name) ||
            !Types.Contains(i.Type,StringComparer.Ordinal) ||
            i.Pattern is not (null or "email" or "alnum" or "identifier" or "guid") ||
            i.Min is not null && i.Max is not null && i.Min>i.Max) ||
            definition.Inputs.Select(i=>i.Name).Distinct(StringComparer.Ordinal).Count()!=definition.Inputs.Count)
            throw new ReadRejected(400,"invalid_template_inputs");
        if(definition.Mappings.Select(m=>m.Target).Distinct(StringComparer.Ordinal).Count()!=definition.Mappings.Count)
            throw new ReadRejected(400,"duplicate_template_target");
        if(definition.Operation is "create" or "update" or "upsert" or "action")
        {
            if(definition.Operation!="action" && definition.Mappings.Count<1 || definition.Select is { Count: > 0 } ||
                definition.SavedQuery is not null) throw new ReadRejected(400,"invalid_write_template");
        }
        else if(definition.Mappings.Count!=0)
            throw new ReadRejected(400,"readonly_template_cannot_map_writes");
        if(definition.Operation=="action" && !System.Text.RegularExpressions.Regex.IsMatch(
            definition.ActionName??"", @"^[A-Za-z][A-Za-z0-9_]{0,79}$"))
            throw new ReadRejected(400,"invalid_template_action");
        if(definition.Operation!="action" && definition.ActionName is not null)
            throw new ReadRejected(400,"unexpected_template_action");
        if(definition.Operation=="find" && (string.IsNullOrEmpty(definition.SavedQuery) ||
            !ReadQueryCompiler.ValidParam(definition.SavedQuery)))
            throw new ReadRejected(400,"invalid_find_template");
        if(definition.Operation=="read" && definition.Select is not { Count: > 0 and <= 30 })
            throw new ReadRejected(400,"read_projection_required");
        foreach(var mapping in definition.Mappings)
        {
            if(definition.Operation=="action")
            {
                if(!ReadQueryCompiler.ValidParam(mapping.Target) ||
                    mapping.Source is null && mapping.Constant is null ||
                    mapping.Source is not null && mapping.Constant is not null ||
                    mapping.Source is not null && definition.Inputs.All(i=>i.Name!=mapping.Source) ||
                    mapping.Steps is {Count: > 8} ||
                    (mapping.Steps??[]).Any(s=>!Steps.Contains(s.Op) || s.Argument?.Length>64 ||
                        s.Op=="lookup_ref"))
                    throw new ReadRejected(400,"invalid_action_mapping");
                continue;
            }
            var fieldName=mapping.Target.EndsWith("@odata.bind",StringComparison.Ordinal)
                ? policy.Table.Relationships.FirstOrDefault(r=>
                    r.Kind=="ManyToOneRelationships" && r.ReferencingEntity==definition.EntityName
                    && r.ReferencingNavigation==mapping.Target[..^"@odata.bind".Length])?.ReferencingAttribute
                : mapping.Target;
            var column=policy.Table.Columns.FirstOrDefault(c=>c.LogicalName==fieldName);
            var enabled=policy.Permissions.Fields.FirstOrDefault(p=>
                p.EntityName==definition.EntityName && p.FieldName==fieldName);
            if(column is null || enabled is null || !enabled.Enabled || column.IsCalculated ||
                enabled.MetadataFingerprint!=MetadataRepository.Fingerprint(column) ||
                (definition.Operation is "create" or "upsert") && (!column.CanCreate || !enabled.CanCreate) ||
                (definition.Operation is "update" or "upsert") && (!column.CanUpdate || !enabled.CanUpdate))
                throw new ReadRejected(403,"template_mapping_field_denied");
            if(mapping.Source is null && mapping.Constant is null ||
                mapping.Source is not null && mapping.Constant is not null ||
                mapping.Source is not null && definition.Inputs.All(i=>i.Name!=mapping.Source) ||
                mapping.Steps is { Count: > 8 })
                throw new ReadRejected(400,"invalid_template_mapping");
            foreach(var step in mapping.Steps??[])
            {
                if(!Steps.Contains(step.Op) || step.Argument?.Length>64 ||
                    step.OtherInput is not null && definition.Inputs.All(i=>i.Name!=step.OtherInput) ||
                    step.Op=="unit" && step.Argument is not ("lb_to_kg" or "kg_to_lb" or "in_to_m" or "m_to_in") ||
                    step.Op=="lookup_ref" && !ReadQueryCompiler.ValidParam(step.Argument) ||
                    step.Op=="choice_constant" && (step.Constant is not JsonElement val ||
                        val.ValueKind!=JsonValueKind.Number || !val.TryGetInt32(out _)))
                    throw new ReadRejected(400,"unsafe_template_transform");
            }
        }
        if(definition.Operation=="read" && definition.Select!.Any(f=>
            !policy.Permissions.Fields.Any(p=>p.EntityName==definition.EntityName &&
                p.FieldName==f && p.Enabled && p.CanRead &&
                policy.Table.Columns.Any(c=>c.LogicalName==f && c.CanRead &&
                    p.MetadataFingerprint==MetadataRepository.Fingerprint(c)))))
            throw new ReadRejected(403,"template_read_fields_denied");
    }

    public static IReadOnlyDictionary<string,JsonElement> ValidateInputs(
        IReadOnlyList<TemplateInput> specs,IReadOnlyDictionary<string,JsonElement> given)
    {
        if(given.Count>30 || given.Keys.Any(k=>specs.All(s=>s.Name!=k)))
            throw new ReadRejected(400,"unexpected_template_input");
        var output=new Dictionary<string,JsonElement>(StringComparer.Ordinal);
        foreach(var spec in specs)
        {
            if(!given.TryGetValue(spec.Name,out var val))
            {
                if(spec.Default is JsonElement configured)val=configured;
                else if(spec.Required)throw new ReadRejected(400,"template_input_required");
                else continue;
            }
            if(!Typed(spec.Type,val))throw new ReadRejected(400,"template_input_type");
            if(spec.Pattern is string pat && val.ValueKind==JsonValueKind.String)
            {
                var text=val.GetString()!;
                bool valid=pat switch
                {
                    "guid" => Guid.TryParse(text,out _),
                    "alnum" => Regex.IsMatch(text,@"^[a-zA-Z0-9]+$"),
                    "identifier" => Regex.IsMatch(text,@"^[a-zA-Z_][a-zA-Z0-9_-]*$"),
                    "email" => text.Length<=254 && Regex.IsMatch(text,@"^[^@\s]+@[^@\s]+\.[^@\s]+$"),
                    _ => false
                };
                if(!valid)throw new ReadRejected(400,"template_input_pattern");
            }
            if(val.ValueKind==JsonValueKind.String && val.GetString()!.Length>2000)
                throw new ReadRejected(400,"template_input_size");
            if(spec.Min is decimal min || spec.Max is decimal)
            {
                decimal number=val.ValueKind==JsonValueKind.String?val.GetString()!.Length :
                    val.ValueKind==JsonValueKind.Number && val.TryGetDecimal(out var decimalValue)
                    ? decimalValue : 0;
                if(spec.Min is decimal lower && number<lower ||
                    spec.Max is decimal upper && number>upper)
                    throw new ReadRejected(400,"template_input_bounds");
            }
            output[spec.Name]=val.Clone();
        }
        return output;
    }

    public static bool Typed(string type,JsonElement value) => type switch
    {
        "string" => value.ValueKind==JsonValueKind.String,
        "guid" => value.ValueKind==JsonValueKind.String && Guid.TryParse(value.GetString(),out _),
        "integer" => value.ValueKind==JsonValueKind.Number && value.TryGetInt64(out _),
        "decimal" => value.ValueKind==JsonValueKind.Number && value.TryGetDecimal(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "datetime" => value.ValueKind==JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(),CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,out _),
        _ => false
    };

    public static async Task<IReadOnlyDictionary<string,JsonElement>> BuildAsync(
        IntegrationTemplate definition, IReadOnlyDictionary<string,JsonElement> values,
        Guid connection, Func<string,string,string,CancellationToken,Task<Guid?>> resolve,
        CancellationToken ct)
    {
        var result=new Dictionary<string,JsonElement>(StringComparer.Ordinal);
        foreach(var mapping in definition.Mappings)
        {
            JsonElement current;
            if(mapping.Source is not null)
            {
                if(!values.TryGetValue(mapping.Source,out current))
                    throw new ReadRejected(400,"template_source_missing");
            }
            else current=mapping.Constant!.Value;
            foreach(var step in mapping.Steps??[])
            {
                switch(step.Op)
                {
                    case "trim": current=JsonSerializer.SerializeToElement(Str(current).Trim()); break;
                    case "upper": current=JsonSerializer.SerializeToElement(Str(current).ToUpperInvariant()); break;
                    case "lower": current=JsonSerializer.SerializeToElement(Str(current).ToLowerInvariant()); break;
                    case "decimal":
                        if(!decimal.TryParse(current.ToString(),NumberStyles.Number,
                            CultureInfo.InvariantCulture,out var dec))
                            throw new ReadRejected(400,"invalid_template_decimal");
                        current=JsonSerializer.SerializeToElement(dec);break;
                    case "date_utc":
                        if(!DateTimeOffset.TryParse(current.ToString(),CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,out var dt))
                            throw new ReadRejected(400,"invalid_template_date");
                        current=JsonSerializer.SerializeToElement(dt.UtcDateTime.ToString("O",CultureInfo.InvariantCulture));break;
                    case "concat":
                        var suffix=step.OtherInput is not null && values.TryGetValue(step.OtherInput,out var other)
                            ? Str(other) : step.Argument??"";
                        current=JsonSerializer.SerializeToElement(Str(current)+suffix);break;
                    case "if_empty":
                        if(current.ValueKind==JsonValueKind.Null ||
                            current.ValueKind==JsonValueKind.String && string.IsNullOrWhiteSpace(current.GetString()))
                        {
                            if(step.OtherInput is not null && values.TryGetValue(step.OtherInput,out var fallback))
                                current=fallback;
                            else current=step.Constant??throw new ReadRejected(400,"fallback_missing");
                        }
                        break;
                    case "choice_constant":
                        current=step.Constant!.Value;break;
                    case "unit":
                        if(!decimal.TryParse(current.ToString(),NumberStyles.Number,
                            CultureInfo.InvariantCulture,out var amount))
                            throw new ReadRejected(400,"unit_requires_number");
                        decimal converted=step.Argument switch
                        {
                            "lb_to_kg" => amount*0.45359237m,
                            "kg_to_lb" => amount/0.45359237m,
                            "in_to_m" => amount*0.0254m,
                            "m_to_in" => amount/0.0254m,
                            _ => throw new ReadRejected(400,"invalid_conversion")
                        };
                        current=JsonSerializer.SerializeToElement(decimal.Round(converted,4,MidpointRounding.AwayFromZero));break;
                    case "lookup_ref":
                        var space=step.Argument!;
                        // Target entity is a static part of the lookup operation, never inferred from user input.
                        // Resolved by callback from known relationship metadata.
                        var id=await resolve(space,Str(current),mapping.Target,ct);
                        if(id is null)throw new ReadRejected(404,"external_reference_missing");
                        current=JsonSerializer.SerializeToElement(id.Value.ToString("D"));break;
                    default:throw new ReadRejected(400,"unsupported_transform");
                }
            }
            result.Add(mapping.Target,current.Clone());
        }
        return result;
    }

    private static string Str(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.String)throw new ReadRejected(400,"transform_requires_text");
        return value.GetString()!;
    }
}
