using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dhole.Dynamics.Contracts;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Checks metadata/permissions before reservation, durably reserves once, then dispatches a write once.
/// An ambiguous outcome is never automatically resent with the same key.
/// </summary>
public sealed class GenericWriteService(WriteAccessService access, WriteLedger ledger,
    DataverseWriteTransport sender, DeletePolicyRepository deletes)
{
    public async Task<WriteResult> CreateAsync(ClaimsPrincipal actor, string entity,
        GenericCreateRequest request, string key, CancellationToken ct)
    {
        var policy = await access.ResolveAsync(request.ConnectionCode, entity, ct);
        var body = await ValuesAsync(actor, policy, request.Values, ct);
        await access.PermitAsync(actor, policy, "create", body.Fields, ct);
        var command = new WriteCommand("create", policy, BasePath(policy), body.Payload);
        return await DispatchAsync(actor, policy, "create", key, [command], ct);
    }

    public async Task<WriteResult> PatchAsync(ClaimsPrincipal actor, string entity, Guid id,
        GenericPatchRequest request, string key, string? etag, CancellationToken ct)
    {
        if (id == Guid.Empty) throw new ReadRejected(400, "invalid_record_id");
        RequireEtag(etag);
        var policy = await access.ResolveAsync(request.ConnectionCode, entity, ct);
        var body = await ValuesAsync(actor, policy, request.Values, ct);
        await access.PermitAsync(actor, policy, "update", body.Fields, ct);
        return await DispatchAsync(actor, policy, "update", key, [
            new WriteCommand("update", policy, ById(policy, id), body.Payload, IfMatch: etag)
        ], ct);
    }

    public async Task<WriteResult> DeleteAsync(ClaimsPrincipal actor, string entity, Guid id,
        GenericDeleteRequest request, string key, string? etag, CancellationToken ct)
    {
        if (id == Guid.Empty || !request.Confirm || string.IsNullOrWhiteSpace(request.Reason)
            || request.Reason.Length is < 10 or > 500)
            throw new ReadRejected(400, "delete_confirmation_required");
        RequireEtag(etag);
        var policy = await access.ResolveAsync(request.ConnectionCode, entity, ct);
        var current = await deletes.GetAsync(policy.Connection.Id, entity, ct);
        if (!current.Enabled) throw new ReadRejected(403, "delete_disabled");
        await access.PermitAsync(actor, policy, "delete", [policy.Table.PrimaryIdAttribute], ct);
        if (current.MinAgeDays > 0)
        {
            // Retention eligibility must be positively verified: errors or absent createdon fail closed.
            var created = await sender.CreatedAtAsync(policy, id, ct);
            if (created is null || created.Value > DateTimeOffset.UtcNow.AddDays(-current.MinAgeDays))
                throw new ReadRejected(403, "retention_policy_denied");
        }
        // Hash the confirmation and reason too; do not persist them in the ledger.
        return await DispatchAsync(actor, policy, "delete", key, [
            new WriteCommand("delete", policy, ById(policy, id), null, IfMatch: etag)
        ], ct, request.Reason);
    }

    public async Task<WriteResult> UpsertAsync(ClaimsPrincipal actor, string entity,
        GenericUpsertRequest request, string key, CancellationToken ct)
    {
        var policy = await access.ResolveAsync(request.ConnectionCode, entity, ct);
        var target = AlternativeKey(policy, request.Keys);
        var body = await ValuesAsync(actor, policy, request.Values, ct);
        var fields = body.Fields.Concat(request.Keys.Keys).Distinct(StringComparer.Ordinal).ToArray();
        await access.PermitAsync(actor, policy, "upsert", fields, ct);
        return await DispatchAsync(actor, policy, "upsert", key, [
            new WriteCommand("upsert", policy, target, body.Payload)
        ], ct);
    }

    public async Task<WriteResult> BatchAsync(ClaimsPrincipal actor,
        GenericBatchRequest request, string key, CancellationToken ct)
    {
        if (request.Items is not { Count: >= 1 and <= 5 })
            throw new ReadRejected(400, "invalid_batch_count");
        var items = new List<WriteCommand>();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in request.Items)
        {
            if (item.Operation is not ("create" or "update" or "delete" or "upsert"))
                throw new ReadRejected(400, "invalid_batch_operation");
            var policy = await access.ResolveAsync(request.ConnectionCode, item.Entity, ct);
            WriteCommand command;
            if (item.Operation == "delete")
            {
                var retention = await deletes.GetAsync(policy.Connection.Id, item.Entity, ct);
                if (!retention.Enabled || retention.MinAgeDays > 0 ||
                    !item.ConfirmDelete || string.IsNullOrWhiteSpace(item.DeleteReason) ||
                    item.DeleteReason.Length is < 10 or > 500)
                    throw new ReadRejected(403, "delete_batch_requires_explicit_confirmation");
                if (item.RecordId is not Guid id || id == Guid.Empty)
                    throw new ReadRejected(400, "invalid_batch_record_id");
                RequireEtag(item.ETag);
                await access.PermitAsync(actor, policy, "delete", [policy.Table.PrimaryIdAttribute], ct);
                command = new("delete", policy, ById(policy, id), null, IfMatch: item.ETag);
            }
            else
            {
                var values = await ValuesAsync(actor, policy, item.Values, ct);
                var target = item.Operation switch
                {
                    "create" => BasePath(policy),
                    "update" when item.RecordId is Guid id && id != Guid.Empty => ById(policy, id),
                    "upsert" => AlternativeKey(policy, item.Keys),
                    _ => throw new ReadRejected(400, "invalid_batch_target")
                };
                if (item.Operation == "update") RequireEtag(item.ETag);
                var fields = item.Operation == "upsert" ? values.Fields.Concat(item.Keys!.Keys)
                    .Distinct(StringComparer.Ordinal).ToArray() : values.Fields;
                await access.PermitAsync(actor, policy, item.Operation, fields, ct);
                command = new(item.Operation, policy, target, values.Payload,
                    IfMatch: item.Operation == "update" ? item.ETag : null);
            }
            // A single changeset must not combine unrelated targets or duplicate create intent.
            if (!declared.Add(command.Operation + ":" + command.Path))
                throw new ReadRejected(400, "duplicate_batch_target");
            items.Add(command);
        }
        // Confirmation/reason are part of the idempotency intent, even when
        // DELETE has no HTTP body inside the Dataverse changeset.
        var confirmations = JsonSerializer.Serialize(request.Items.Select(item =>
            new { item.ConfirmDelete, item.DeleteReason }).ToArray());
        return await DispatchAsync(actor, items[0].Policy, "batch", key, items, ct, confirmations);
    }

    private async Task<(Dictionary<string, object?> Payload, string[] Fields)> ValuesAsync(
        ClaimsPrincipal actor, WritePolicy policy, IReadOnlyDictionary<string, JsonElement>? values,
        CancellationToken ct) =>
        await WriteValueCompiler.CompileAsync(policy, values,
            (relationship, id, token) => access.EnsureLookupAsync(actor, policy, relationship, id, token), ct);

    private async Task<WriteResult> DispatchAsync(ClaimsPrincipal actor, WritePolicy policy,
        string operation, string key, IReadOnlyList<WriteCommand> commands, CancellationToken ct,
        string? confirmation = null)
    {
        var issuer = actor.FindFirst("connector:issuer")?.Value ??
            throw new ReadRejected(403, "client_issuer_missing");
        var client = ScopeSecurity.ClientId(actor) ??
            throw new ReadRejected(403, "client_id_missing");
        // Hash the exact normalized outbound intent, including concurrency precondition and metadata version.
        var intent = JsonSerializer.Serialize(new
        {
            version = policy.Snapshot.Version, op = operation,
            commands = commands.Select(c => new { c.Operation, c.Policy.Table.LogicalName,
                c.Path, c.Payload, c.IfMatch, c.IfNoneMatch }).ToArray(),
            confirmation
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(intent)));
        var reservation = await ledger.ReserveAsync(policy.Connection.Id, issuer, client, key,
            hash, commands.Count == 1 ? commands[0].Policy.Table.LogicalName : "_batch",
            operation, operation == "batch" ? commands.Count : null, ct);
        if (!reservation.Created)
        {
            if (reservation.Previous is { } previous &&
                reservation.Status is "succeeded" or "rejected")
                return previous with { Replay = true };
            // The original request might have reached Dataverse. Never try a second send.
            throw new ReadRejected(409, "write_pending_reconciliation");
        }
        WriteTransportOutcome outcome;
        try
        {
            // Never retry a dispatched unsafe request, even for 429/5xx.
            outcome = operation == "batch"
                ? await sender.SendBatchAsync(commands, ct)
                : await sender.SendAsync(commands[0], ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or IOException or System.Text.Json.JsonException)
        {
            // Cancellation of HTTP is ambiguous. Use an independent token for the ledger update.
            return await ledger.CompleteAsync(reservation.Id, policy.Connection.Id, client,
                "ambiguous", null, null, null, "manual-reconciliation-required", CancellationToken.None);
        }
        var state = outcome.Ambiguous || outcome.HttpStatus >= 500
            ? "ambiguous" : outcome.HttpStatus is >= 200 and < 300 ? "succeeded" : "rejected";
        return await ledger.CompleteAsync(reservation.Id, policy.Connection.Id, client,
            state, outcome.HttpStatus, outcome.RecordId, outcome.ETag,
            outcome.Reference, CancellationToken.None, outcome.RetryAfterSeconds);
    }

    public static string BasePath(WritePolicy policy) =>
        "/api/data/v9.2/" + policy.Table.EntitySetName;

    public static string ById(WritePolicy policy, Guid id) =>
        BasePath(policy) + "(" + id.ToString("D") + ")";

    public static string AlternativeKey(WritePolicy policy,
        IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (values is not { Count: >= 1 and <= 3 })
            throw new ReadRejected(400, "invalid_alternate_key");
        var key = (policy.Table.AlternateKeys ?? []).FirstOrDefault(k =>
            k.Attributes.Count == values.Count && k.Attributes.All(values.ContainsKey))
            ?? throw new ReadRejected(400, "unregistered_alternate_key");
        var literals = new List<string>();
        foreach (var name in key.Attributes)
        {
            var column = policy.Table.Columns.First(c => c.LogicalName == name);
            string encoded;
            try { encoded = Uri.EscapeDataString(ReadQueryCompiler.Literal(column, values[name])); }
            catch (ArgumentException) { throw new ReadRejected(400, "invalid_alternate_key_literal"); }
            literals.Add(name + "=" + encoded);
        }
        return BasePath(policy) + "(" + string.Join(",", literals) + ")";
    }

    public static void RequireEtag(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag) || etag.Length > 100 ||
            !Regex.IsMatch(etag, """^(?:W/)?"[0-9a-zA-Z_-]{1,80}"$""", RegexOptions.CultureInvariant))
            throw new ReadRejected(428, "etag_required");
    }
}
