using System.Security.Claims;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dhole.Dynamics.Api;

/// <summary>Authoritative template entry point: revalidates active snapshot and policies for every invocation.</summary>
public sealed class TemplateOrchestrator(TemplateRepository templates,ExternalReferenceRepository references,
    WriteAccessService writerAccess,GenericWriteService writer,ReadSearchService reader,
    SavedQueryRepository saved,ReadAccessRepository readerAccess,
    ActionPolicyRepository actionPolicy,BoundActionExecutor actionRunner,
    DataverseMetadataDiscovery metadataDiscovery)
{
    public async Task<TemplateSummary> CreateDraftAsync(string code,string name,
        IntegrationTemplate definition,string actor,CancellationToken ct)
    {
        var context=await writerAccess.ResolveAsync(code,definition.EntityName,ct);
        await ValidateDefinitionAsync(context,definition,ct);
        return await templates.NewRevisionAsync(context.Connection.Id,name,definition,
            context.Snapshot.Version,actor,ct);
    }

    public async Task<TemplateSummary> ActivateAsync(string code,string name,int? version,
        string actor,CancellationToken ct)
    {
        var conn=await readerAccess.ConnectionAsync(code,ct);
        var template=await templates.FindAsync(conn.Id,name,ct)
            ?? throw new ReadRejected(404,"template_missing");
        if(version is int requested)
        {
            var revision=template.History.SingleOrDefault(r=>r.Version==requested)
                ?? throw new ReadRejected(404,"template_version_missing");
            var context=await writerAccess.ResolveAsync(code,revision.Definition.EntityName,ct);
            await ValidateDefinitionAsync(context,revision.Definition,ct);
        }
        if(!await templates.SetActiveAsync(conn.Id,name,version,actor,ct))
            throw new ReadRejected(409,"template_version_missing");
        return (await templates.FindAsync(conn.Id,name,ct))!;
    }

    public async Task<TemplatePreview> PreviewAsync(string code,string name,int version,
        IReadOnlyDictionary<string,JsonElement> inputs,CancellationToken ct,
        ClaimsPrincipal? actor = null, bool omitMissingQuoteCustomer = false)
    {
        var conn=await readerAccess.ConnectionAsync(code,ct);
        var template=await templates.FindAsync(conn.Id,name,ct)
            ?? throw new ReadRejected(404,"template_missing");
        var revision=template.History.SingleOrDefault(x=>x.Version==version)
            ?? throw new ReadRejected(404,"template_version_missing");
        var policy=await writerAccess.ResolveAsync(code,revision.Definition.EntityName,ct);
        await ValidateDefinitionAsync(policy,revision.Definition,ct);
        var bound=TemplateMapper.ValidateInputs(revision.Definition.Inputs,inputs);
        var data=await BuildAsync(revision.Definition,policy,bound,ct,actor,
            omitMissingQuoteCustomer);
        if(revision.Definition.Operation=="action")
        {
            var action=await actionPolicy.FindAsync(policy.Connection.Id,
                revision.Definition.EntityName,revision.Definition.ActionName!,ct)
                ?? throw new ReadRejected(403,"action_policy_denied");
            if(action.Parameters.Any(p=>!data.TryGetValue(p.Name,out var v) ||
                !TemplateMapper.Typed(p.Type,v)))
                throw new ReadRejected(400,"action_parameter_type");
        }
        if(revision.Definition.Operation is "create" or "update" or "upsert")
        {
            var typed=await WriteValueCompiler.CompileAsync(policy,data,async(rel,id,token)=>
            {
                // Preview validates relationship and approval but NEVER invokes Dataverse or consumes M2M quotas.
                if(rel.ReferencedEntity is null)throw new ReadRejected(400,"invalid_template_relation");
                var target=await writerAccess.ResolveAsync(code,rel.ReferencedEntity,token);
                if(!target.Permissions.Fields.Any(f=>f.EntityName==rel.ReferencedEntity
                    && f.FieldName==target.Table.PrimaryIdAttribute && f.Enabled && f.CanRead))
                    throw new ReadRejected(403,"preview_lookup_target_denied");
            },ct);
            return new(version,revision.Definition.Operation,revision.Definition.EntityName,
                typed.Payload.ToDictionary(x=>x.Key,x=>JsonSerializer.SerializeToElement(x.Value)),
                typed.Fields,policy.Snapshot.Version);
        }
        return new(version,revision.Definition.Operation,revision.Definition.EntityName,data,
            revision.Definition.Operation=="action" ? data.Keys.Order(StringComparer.Ordinal).ToArray()
                : revision.Definition.Select??[],policy.Snapshot.Version);
    }

    public async Task<object> ExecuteAsync(ClaimsPrincipal actor,string name,TemplateInputs request,
        string? idempotencyKey,CancellationToken ct,
        bool omitMissingQuoteCustomer = false)
    {
        var conn=await readerAccess.ConnectionAsync(request.ConnectionCode,ct);
        var template=await templates.FindAsync(conn.Id,name,ct);
        if(template?.ActiveVersion is not int active)
            throw new ReadRejected(404,"active_template_missing");
        var revision=template.History.SingleOrDefault(r=>r.Version==active)
            ?? throw new ReadRejected(409,"active_revision_missing");
        var definition=revision.Definition;
        var policy=await writerAccess.ResolveAsync(request.ConnectionCode,definition.EntityName,ct);
        await ValidateDefinitionAsync(policy,definition,ct);
        var input=definition.Operation=="find" && !string.IsNullOrEmpty(request.Cursor)
            ? new Dictionary<string,JsonElement>()
            : TemplateMapper.ValidateInputs(definition.Inputs,request.Inputs);
        var values=await BuildAsync(definition,policy,input,ct,actor,
            omitMissingQuoteCustomer);
        if(definition.Operation is "create" or "update" or "upsert" or "delete" or "action" &&
            !WriteLedger.ValidKey(idempotencyKey))
            throw new ReadRejected(400,"idempotency_key_required");
        return definition.Operation switch
        {
            "create" => await writer.CreateAsync(actor,definition.EntityName,
                new GenericCreateRequest(request.ConnectionCode,values),
                idempotencyKey!,ct),
            "update" => request.RecordId is Guid id
                ? await writer.PatchAsync(actor,definition.EntityName,id,
                    new GenericPatchRequest(request.ConnectionCode,values),
                    idempotencyKey!,request.ETag,ct)
                : throw new ReadRejected(400,"record_id_required"),
            "upsert" => await writer.UpsertAsync(actor,definition.EntityName,
                new GenericUpsertRequest(request.ConnectionCode,
                    request.Keys??throw new ReadRejected(400,"alternate_key_required"),values),
                idempotencyKey!,ct),
            "delete" => request.RecordId is Guid id
                ? await writer.DeleteAsync(actor,definition.EntityName,id,
                    new GenericDeleteRequest(request.ConnectionCode,request.ConfirmDelete,
                        request.DeleteReason??""),
                    idempotencyKey!,request.ETag,ct)
                : throw new ReadRejected(400,"record_id_required"),
            "read" => request.RecordId is Guid id
                ? await reader.ByIdAsync(actor,request.ConnectionCode,definition.EntityName,id,
                    definition.Select??[],ct)
                : throw new ReadRejected(400,"record_id_required"),
            "find" => await ExecuteFindAsync(actor,definition,request,input,ct),
            "action" => request.RecordId is Guid target
                ? await actionRunner.InvokeAsync(actor,policy,definition.ActionName!,target,values,
                    idempotencyKey!,active,ct)
                : throw new ReadRejected(400,"record_id_required"),
            _ => throw new ReadRejected(400,"unsupported_template_operation")
        };
    }

    private async Task<ReadSearchResponse> ExecuteFindAsync(ClaimsPrincipal actor,
        IntegrationTemplate definition,TemplateInputs req,
        IReadOnlyDictionary<string,JsonElement> parameters,CancellationToken ct)
    {
        var context=await readerAccess.ResolveAsync(req.ConnectionCode,definition.EntityName,ct);
        var stored=await saved.FindAsync(context.Connection.Id,definition.SavedQuery!,ct);
        if(stored is not { Enabled:true } || stored.EntityName!=definition.EntityName)
            throw new ReadRejected(403,"saved_view_disabled");
        var spec=string.IsNullOrWhiteSpace(req.Cursor)
            ? ReadSavedQueryBinder.Bind(stored.Query,stored.Parameters,parameters) : null;
        // Run the saved query through the same M2M fields, scope, quotas and cursor enforcement as Phase 05.
        return await reader.SearchAsync(actor,definition.EntityName,
            new ReadSearchRequest(req.ConnectionCode,spec,req.Cursor),ct,
            definition.SavedQuery);
    }

    // The published crear_oferta v13 has up to nine IDTRA relationships.
    // An incident may store its identifier in title or ticketnumber. Keep
    // exact matching for both columns, never select a fuzzy candidate.
    private static bool IsIdtraRelationship(string entity, string attribute) =>
        entity.Equals("incident", StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(attribute, @"^new_idtra(?:[1-9]|10)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string ResolveLookupNameField(string relatedEntity,
        string relationshipAttribute, string configuredField) =>
        IsIdtraRelationship(relatedEntity, relationshipAttribute) &&
        configuredField.Equals("ticketnumber", StringComparison.OrdinalIgnoreCase)
            ? "title" : configuredField;

    public static IReadOnlyList<string> ResolveLookupNameFields(string relatedEntity,
        string relationshipAttribute, string configuredField)
    {
        var primary = ResolveLookupNameField(relatedEntity, relationshipAttribute, configuredField);
        if (IsIdtraRelationship(relatedEntity, relationshipAttribute) &&
            (configuredField.Equals("ticketnumber", StringComparison.OrdinalIgnoreCase) ||
             configuredField.Equals("title", StringComparison.OrdinalIgnoreCase)))
            return ["title", "ticketnumber"];
        return [primary];
    }

    // Dataverse may keep the full legal form while Dhole uses "SRL" or
    // "S.A.". All account-name LOOKUP requests use OData contains (never
    // eq), and must still verify a unique legal-name-equivalent account.
    // This prevents a broad contains result from binding a wrong customer.
    public static string CustomerAccountNameKey(string name)
    {
        var unaccented = Regex.Replace(
            name.Normalize(NormalizationForm.FormD), @"\p{Mn}", "");
        var normalized = Regex.Replace(unaccented.ToUpperInvariant(),
            @"[^\p{L}\p{N}]+", " ").Trim();
        normalized = Regex.Replace(normalized, @"\s+", " ");
        // Only remove TRAILING corporate legal forms. Other words, including
        // "ON" in SOLUCIONES LUMINICAS ON, remain part of the identity.
        const string corporateSuffix =
            @"(?: SOCIEDAD DE RESPONSABILIDAD LIMITADA| SOCIEDAD ANONIMA" +
            @"| SOCIEDAD DE RESPONSABILIDAD LTDA| S DE R L| S R L| S A" +
            @"| SRL| SA| LTDA| LLC| INC| LIMITED| LIMITADA)$";
        string previous;
        do
        {
            previous = normalized;
            normalized = Regex.Replace(normalized, corporateSuffix, "",
                RegexOptions.CultureInvariant);
        } while (normalized != previous);
        return normalized.Trim();
    }

    public static bool CustomerAccountNamesMatch(string providedName, string recordName)
    {
        var requested = CustomerAccountNameKey(providedName);
        return requested.Length >= 3 &&
            string.Equals(requested, CustomerAccountNameKey(recordName),
                StringComparison.Ordinal);
    }

    public static IReadOnlyList<ReadPredicate> CustomerNameFallbackFilters(
        string searchField, string name)
    {
        var input = name.Trim();
        var core = CustomerAccountNameKey(input);
        var filters = new List<ReadPredicate>();
        if (core.Length < 3)
            return filters;
        // All queries are metadata-authorized contains. First search the
        // supplied name, then the legal-suffix-free name, then individual
        // distinguishing terms for punctuation/spacing differences.
        if (input.Length is >= 3 and <= 100)
            filters.Add(new ReadPredicate(Field:searchField, Op:"contains",
                Value:JsonSerializer.SerializeToElement(input)));
        if (core.Length is >= 3 and <= 100 &&
            !string.Equals(input, core, StringComparison.OrdinalIgnoreCase))
            filters.Add(new ReadPredicate(Field:searchField, Op:"contains",
                Value:JsonSerializer.SerializeToElement(core)));
        var words = core.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length is >= 3 and <= 100)
            .Distinct(StringComparer.Ordinal)
            .Take(4).ToArray();
        if (words.Length >= 2)
            filters.Add(new ReadPredicate(Join:"and", Children:
                words.Select(x => new ReadPredicate(Field:searchField,
                    Op:"contains", Value:JsonSerializer.SerializeToElement(x))).ToArray()));
        return filters;
    }

    private async Task<IReadOnlyDictionary<string,JsonElement>> BuildAsync(
        IntegrationTemplate definition,WritePolicy policy,
        IReadOnlyDictionary<string,JsonElement> inputs,CancellationToken ct,
        ClaimsPrincipal? actor = null, bool omitMissingQuoteCustomer = false) =>
        await TemplateMapper.BuildAsync(definition,inputs,policy.Connection.Id,
            async(space,internalId,navigation,token)=>
            {
                var relation=policy.Table.Relationships.FirstOrDefault(r=>
                    r.Kind=="ManyToOneRelationships" &&
                    r.ReferencingNavigation+"@odata.bind"==navigation &&
                    r.ReferencingEntity==policy.Table.LogicalName);
                if(relation?.ReferencedEntity is null)
                    throw new ReadRejected(400,"unapproved_reference_relation");
                var entry=await references.FindAsync(policy.Connection.Id,space,internalId,
                    relation.ReferencedEntity,token);
                return entry?.DataverseId;
            },ct,
            async(navigation,field,name,token)=>
            {
                if(actor is null)throw new ReadRejected(403,"lookup_name_requires_authorized_reader");
                var rel=policy.Table.Relationships.FirstOrDefault(r=>
                    r.Kind=="ManyToOneRelationships" &&
                    r.ReferencingNavigation+"@odata.bind"==navigation &&
                    r.ReferencingEntity==policy.Table.LogicalName);
                if(rel?.ReferencedEntity is not string target)
                    throw new ReadRejected(403,"lookup_relation_not_approved");
                var lookupPolicy=await readerAccess.ResolveAsync(policy.Connection.Code,target,token);
                var candidates=ResolveLookupNameFields(
                    target,rel.ReferencingAttribute??"",field);
                var anyAuthorized=false;
                foreach(var searchField in candidates)
                {
                    var nameColumn=lookupPolicy.Table.Columns.FirstOrDefault(c=>c.LogicalName==searchField);
                    if(nameColumn is null || nameColumn.Type is not ("String" or "Memo") ||
                        !lookupPolicy.Allowed.Contains(searchField) ||
                        !lookupPolicy.Allowed.Contains(lookupPolicy.Table.PrimaryIdAttribute))
                        continue;
                    anyAuthorized=true;
                    var accountNameLookup =
                        target.Equals("account", StringComparison.OrdinalIgnoreCase) &&
                        searchField.Equals("name", StringComparison.OrdinalIgnoreCase);
                    // Account customer references MUST use contains, not eq.
                    // Incident and other lookup types remain strict/exact.
                    var predicates = accountNameLookup
                        ? CustomerNameFallbackFilters(searchField, name).ToList()
                        : new List<ReadPredicate>
                        {
                            new(Field:searchField, Op:"eq",
                                Value:JsonSerializer.SerializeToElement(name.Trim()))
                        };
                    var isIdtraTitle = IsIdtraRelationship(target,
                        rel.ReferencingAttribute ?? "") &&
                        searchField.Equals("title", StringComparison.OrdinalIgnoreCase) &&
                        name.Length is >= 8 and <= 100 &&
                        Regex.IsMatch(name.Trim(), @"^IDTRA[\p{L}\p{N}.\-]+$",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (isIdtraTitle)
                        predicates.Add(new ReadPredicate(Field: searchField, Op: "contains",
                            Value: JsonSerializer.SerializeToElement(name.Trim())));
                    foreach(var predicate in predicates)
                    {
                        var suffixSearch = predicate.Op == "contains" && isIdtraTitle;
                        var query = new ReadSpec(suffixSearch || accountNameLookup
                            ? [lookupPolicy.Table.PrimaryIdAttribute, searchField]
                            : [lookupPolicy.Table.PrimaryIdAttribute],
                            predicate, PageSize:suffixSearch || accountNameLookup ? 25 : 2);
                        // All search operations are metadata-validated and
                        // restricted to the account's approved readable fields.
                        var result=await reader.SearchAsync(actor,target,
                            new ReadSearchRequest(policy.Connection.Code,query),token,
                            admin: ScopeSecurity.HasAdminRole(actor,"dynamics.security.admin"));
                        if (suffixSearch)
                        {
                            // Pricing removes '-QUO-...' from its IDTRA display
                            // string. Search this documented suffix form only,
                            // then verify the full value and uniqueness locally.
                            // No partial/fuzzy account or incident association.
                            var valid = result.Items.Where(item =>
                                item.TryGetProperty(searchField, out var value) &&
                                value.ValueKind == JsonValueKind.String &&
                                Regex.IsMatch(value.GetString() ?? "", "^" +
                                    Regex.Escape(name.Trim()) +
                                    @"\s*[-–—]\s*QUO(?:[-#\s].*)?$",
                                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                                .ToArray();
                            if (valid.Length > 1 || result.NextCursor is not null)
                                throw new ReadRejected(409, "lookup_name_ambiguous",
                                    rel.ReferencingAttribute, searchField);
                            if (valid.Length == 0) continue;
                            var match = valid[0];
                            return match.TryGetProperty(lookupPolicy.Table.PrimaryIdAttribute,
                                out var candidateId) &&
                                Guid.TryParse(candidateId.GetString(), out var checkedId) &&
                                checkedId != Guid.Empty
                                ? checkedId : throw new ReadRejected(502, "lookup_name_invalid_record");
                        }
                        if (accountNameLookup)
                        {
                            // A contains filter alone is not sufficient to
                            // identify a customer: confirm its entire legal-
                            // suffix-normalized name, and reject duplicates.
                            var matches = result.Items.Where(item =>
                                item.TryGetProperty(searchField, out var value) &&
                                value.ValueKind == JsonValueKind.String &&
                                CustomerAccountNamesMatch(name, value.GetString() ?? ""))
                                .ToArray();
                            if (matches.Length > 1 || result.NextCursor is not null)
                                throw new ReadRejected(409, "lookup_name_ambiguous",
                                    rel.ReferencingAttribute, searchField);
                            if (matches.Length == 0) continue;
                            return matches[0].TryGetProperty(
                                    lookupPolicy.Table.PrimaryIdAttribute, out var customerIdElement) &&
                                customerIdElement.ValueKind == JsonValueKind.String &&
                                Guid.TryParse(customerIdElement.GetString(), out var customerId) &&
                                customerId != Guid.Empty
                                ? customerId : throw new ReadRejected(502, "lookup_name_invalid_record");
                        }
                        if(result.Items.Count>1 || result.NextCursor is not null)
                            throw new ReadRejected(409,"lookup_name_ambiguous",
                                rel.ReferencingAttribute,searchField);
                        if(result.Items.Count==0)continue;
                        var item=result.Items[0];
                        return item.TryGetProperty(lookupPolicy.Table.PrimaryIdAttribute,out var idElement)
                            && Guid.TryParse(idElement.GetString(),out var id) && id!=Guid.Empty
                            ? id : throw new ReadRejected(502,"lookup_name_invalid_record");
                    }
                }
                if(!anyAuthorized)
                    throw new ReadRejected(403,"lookup_name_field_not_authorized");
                throw new ReadRejected(404,"lookup_name_not_found",
                    rel.ReferencingAttribute,candidates[0]);
            },
            async(target,label,token)=>
            {
                var col=policy.Table.Columns.FirstOrDefault(c=>c.LogicalName==target);
                var grant=policy.Permissions.Fields.FirstOrDefault(p=>
                    p.EntityName==policy.Table.LogicalName && p.FieldName==target);
                if(col is null || col.Type is not ("Picklist" or "State" or "Status") ||
                    grant is null || !grant.Enabled ||
                    grant.MetadataFingerprint!=MetadataRepository.Fingerprint(col))
                    throw new ReadRejected(403,"choice_label_field_denied");
                var options=await metadataDiscovery.DiscoverChoicesAsync(
                    policy.Connection,policy.Table.LogicalName,target,col.Type,token);
                // Metadata is authoritative. Numeric-looking strings are
                // accepted only if that exact choice is actually present.
                var named=options.Where(choice=>choice.Labels.Values.Any(x=>
                    NormalizeChoiceLabel(x)==NormalizeChoiceLabel(label)) ||
                    choice.Value.ToString(CultureInfo.InvariantCulture)==label.Trim()).ToArray();
                if(named.Length>1)throw new ReadRejected(409,"choice_label_ambiguous");
                return named.Length==1?named[0].Value:(int?)null;
            }, omitMissingQuoteCustomer: omitMissingQuoteCustomer);

    private static string NormalizeChoiceLabel(string value)
    {
        var decomp=value.Trim().Normalize(NormalizationForm.FormD);
        var text=new StringBuilder(decomp.Length);
        foreach(var c in decomp)
            if(CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark &&
                char.IsLetterOrDigit(c)) text.Append(char.ToUpperInvariant(c));
        return text.ToString();
    }

    private async Task ValidateDefinitionAsync(WritePolicy policy,
        IntegrationTemplate definition,CancellationToken ct)
    {
        TemplateMapper.ValidateDefinition(definition,policy);
        if(definition.Operation=="action")
        {
            var named=await actionPolicy.FindAsync(policy.Connection.Id,definition.EntityName,
                definition.ActionName!,ct);
            if(named is not {Enabled:true} ||
                definition.Mappings.Count!=named.Parameters.Count ||
                definition.Mappings.Any(m=>!named.Parameters.Any(p=>p.Name==m.Target)))
                
                throw new ReadRejected(403,"action_policy_denied");
        }
        if(definition.Operation=="find")
        {
            var existing=await saved.FindAsync(policy.Connection.Id,definition.SavedQuery!,ct);
            if(existing is not { Enabled:true } || existing.EntityName!=definition.EntityName)
                throw new ReadRejected(403,"saved_view_disabled");
            // Preserve exact type/required match, don't silently treat arbitrary parameters as fields.
            if(existing.Parameters.Count!=definition.Inputs.Count ||
                existing.Parameters.Any(p=>!definition.Inputs.Any(i=>i.Name==p.Name &&
                    i.Type==p.Type && i.Required==p.Required)))
                throw new ReadRejected(400,"saved_view_parameters_mismatch");
        }
    }
}
