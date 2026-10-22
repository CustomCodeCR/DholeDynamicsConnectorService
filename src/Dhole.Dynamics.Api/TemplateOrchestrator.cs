using System.Security.Claims;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;
using System.Globalization;
using System.Text;

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
        ClaimsPrincipal? actor = null)
    {
        var conn=await readerAccess.ConnectionAsync(code,ct);
        var template=await templates.FindAsync(conn.Id,name,ct)
            ?? throw new ReadRejected(404,"template_missing");
        var revision=template.History.SingleOrDefault(x=>x.Version==version)
            ?? throw new ReadRejected(404,"template_version_missing");
        var policy=await writerAccess.ResolveAsync(code,revision.Definition.EntityName,ct);
        await ValidateDefinitionAsync(policy,revision.Definition,ct);
        var bound=TemplateMapper.ValidateInputs(revision.Definition.Inputs,inputs);
        var data=await BuildAsync(revision.Definition,policy,bound,ct,actor);
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
        string? idempotencyKey,CancellationToken ct)
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
        var values=await BuildAsync(definition,policy,input,ct,actor);
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

    private async Task<IReadOnlyDictionary<string,JsonElement>> BuildAsync(
        IntegrationTemplate definition,WritePolicy policy,
        IReadOnlyDictionary<string,JsonElement> inputs,CancellationToken ct,
        ClaimsPrincipal? actor = null) =>
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
                var nameColumn=lookupPolicy.Table.Columns.FirstOrDefault(c=>c.LogicalName==field);
                if(nameColumn is null || nameColumn.Type is not ("String" or "Memo") ||
                    !lookupPolicy.Allowed.Contains(field) ||
                    !lookupPolicy.Allowed.Contains(lookupPolicy.Table.PrimaryIdAttribute))
                    throw new ReadRejected(403,"lookup_name_field_not_authorized");
                var query=new ReadSpec(
                    [lookupPolicy.Table.PrimaryIdAttribute],
                    new ReadPredicate(Field:field,Op:"eq",
                        Value:JsonSerializer.SerializeToElement(name.Trim())),
                    PageSize:2);
                // ReadSearchService enforces the related entity's read scope and
                // independently approved client grant. Never a wildcard search.
                var result=await reader.SearchAsync(actor,target,
                    new ReadSearchRequest(policy.Connection.Code,query),token,
                    admin: ScopeSecurity.HasAdminRole(actor,"dynamics.security.admin"));
                if(result.Items.Count>1)throw new ReadRejected(409,"lookup_name_ambiguous");
                if(result.Items.Count==0)return null;
                var item=result.Items[0];
                return item.TryGetProperty(lookupPolicy.Table.PrimaryIdAttribute,out var idElement)
                    && Guid.TryParse(idElement.GetString(),out var id) && id!=Guid.Empty
                    ? id : throw new ReadRejected(502,"lookup_name_invalid_record");
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
            });

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
