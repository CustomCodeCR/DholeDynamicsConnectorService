using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dhole.Dynamics.Contracts;

namespace Dhole.Dynamics.Api;

/// <summary>Separate OIDC SecurityAdmin authorizer, never a fabricated M2M bearer identity.</summary>
public sealed class AdminRecordService(WriteAccessService policies, DeletePolicyRepository deletePolicies,
    DataverseWriteTransport transport, WriteLedger ledger)
{
    public async Task<WriteResult> ExecuteAsync(ClaimsPrincipal user, string code, string entity,
        string op, Guid? record, IReadOnlyDictionary<string, JsonElement>? values,
        string? etag, bool confirm, string? reason, string key, CancellationToken ct)
    {
        if (!ScopeSecurity.HasAdminRole(user, "dynamics.security.admin"))
            throw new ReadRejected(403, "security_admin_required");
        if (!WriteLedger.ValidKey(key)) throw new ReadRejected(400, "idempotency_key_required");
        if (op is not ("create" or "update" or "delete")) throw new ReadRejected(400, "operation_denied");
        var policy = await policies.ResolveAsync(code, entity, ct);
        WriteCommand command;
        if (op == "delete")
        {
            if (record is not Guid id || id == Guid.Empty || !confirm ||
                reason is not { Length: >= 10 and <= 500 })
                throw new ReadRejected(400, "delete_confirmation_required");
            GenericWriteService.RequireEtag(etag);
            var removal = await deletePolicies.GetAsync(policy.Connection.Id, entity, ct);
            if (!removal.Enabled) throw new ReadRejected(403, "delete_disabled");
            if (removal.MinAgeDays > 0)
            {
                var created = await transport.CreatedAtAsync(policy, id, ct);
                if (created is null || created > DateTimeOffset.UtcNow.AddDays(-removal.MinAgeDays))
                    throw new ReadRejected(403, "retention_denied");
            }
            command = new("delete", policy, GenericWriteService.ById(policy, id), null, IfMatch: etag);
        }
        else
        {
            if (op == "update" && (record is null || record == Guid.Empty))
                throw new ReadRejected(400, "record_id_required");
            if (op == "update") GenericWriteService.RequireEtag(etag);
            var compiled = await WriteValueCompiler.CompileAsync(policy, values,
                async (relation, _, token) =>
                {
                    if (relation.ReferencedEntity is null) throw new ReadRejected(403, "lookup_denied");
                    var related = await policies.ResolveAsync(code, relation.ReferencedEntity, token);
                    var primary = related.Table.Columns.FirstOrDefault(c => c.LogicalName == related.Table.PrimaryIdAttribute);
                    if (primary is null || !primary.CanRead || !related.Permissions.Fields.Any(f =>
                        f.EntityName == related.Table.LogicalName && f.FieldName == primary.LogicalName &&
                        f.Enabled && f.CanRead &&
                        f.MetadataFingerprint == MetadataRepository.Fingerprint(primary)))
                        throw new ReadRejected(403, "lookup_target_denied");
                }, ct);
            AssertFields(policy, compiled.Fields, op);
            command = op == "create"
                ? new("create", policy, GenericWriteService.BasePath(policy), compiled.Payload)
                : new("update", policy, GenericWriteService.ById(policy, record!.Value),
                    compiled.Payload, IfMatch: etag);
        }
        var actor = ScopeSecurity.ActorId(user);
        var client = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actor)));
        var hashBody = JsonSerializer.Serialize(new
        {
            policy.Connection.Id, policy.Snapshot.Version, op, command.Path, command.Payload,
            command.IfMatch, confirm, reason
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashBody)));
        var reserved = await ledger.ReserveAsync(policy.Connection.Id, "admin:oidc", client, key, hash,
            policy.Table.LogicalName, op, null, ct);
        if (!reserved.Created)
        {
            if (reserved.Previous is { } prior && reserved.Status is "succeeded" or "rejected")
                return prior with { Replay = true };
            throw new ReadRejected(409, "write_pending_reconciliation");
        }
        WriteTransportOutcome outcome;
        try { outcome = await transport.SendAsync(command, ct); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            return await ledger.CompleteAsync(reserved.Id, policy.Connection.Id, client,
                "ambiguous", null, null, null, "manual-reconciliation-required", CancellationToken.None);
        }
        var state = outcome.Ambiguous || outcome.HttpStatus >= 500 ? "ambiguous"
            : outcome.HttpStatus >= 400 ? "rejected" : "succeeded";
        return await ledger.CompleteAsync(reserved.Id, policy.Connection.Id, client,
            state, outcome.HttpStatus, outcome.RecordId, outcome.ETag, outcome.Reference,
            CancellationToken.None, outcome.RetryAfterSeconds);
    }

    public static void AssertFields(WritePolicy policy, IReadOnlyCollection<string> fields, string op)
    {
        if (fields.Count == 0) throw new ReadRejected(400, "empty_write");
        foreach (var name in fields)
        {
            var col = policy.Table.Columns.FirstOrDefault(f => f.LogicalName == name)
                ?? throw new ReadRejected(403, "unknown_field");
            var permission = policy.Permissions.Fields.FirstOrDefault(p =>
                p.EntityName == policy.Table.LogicalName && p.FieldName == name);
            if (permission is null || !permission.Enabled || col.IsCalculated ||
                permission.MetadataFingerprint != MetadataRepository.Fingerprint(col) ||
                (op == "create" && (!permission.CanCreate || !col.CanCreate)) ||
                (op == "update" && (!permission.CanUpdate || !col.CanUpdate)))
                throw new ReadRejected(403, "write_field_not_enabled");
        }
    }
}
