using System.Text.Json;
namespace Dhole.Dynamics.Contracts;

public sealed record GenericCreateRequest(string ConnectionCode, IReadOnlyDictionary<string, JsonElement> Values);
public sealed record GenericPatchRequest(string ConnectionCode, IReadOnlyDictionary<string, JsonElement> Values);
public sealed record GenericUpsertRequest(string ConnectionCode, IReadOnlyDictionary<string, JsonElement> Keys,
    IReadOnlyDictionary<string, JsonElement> Values);
public sealed record GenericDeleteRequest(string ConnectionCode, bool Confirm, string Reason);
public sealed record GenericBatchItem(string Operation, string Entity, Guid? RecordId,
    IReadOnlyDictionary<string, JsonElement>? Keys, IReadOnlyDictionary<string, JsonElement>? Values,
    string? ETag = null);
public sealed record GenericBatchRequest(string ConnectionCode, IReadOnlyList<GenericBatchItem> Items);
public sealed record WriteResult(Guid OperationId, string State, int? HttpStatus,
    Guid? RecordId, string? ETag, string? Reference, bool Replay = false, int? RetryAfterSeconds = null);
public sealed record WriteOperationDetail(Guid Id, string Entity, string Operation, string State,
    int? HttpStatus, Guid? RecordId, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    string? Reference);
public sealed record DeletePolicyRequest(bool Enabled, int MinAgeDays);
public sealed record DeletePolicyState(string Entity, bool Enabled, int MinAgeDays);
public sealed record WriteReconcileRequest(string? RecordId, bool ConfirmedNotApplied,
    string Reason);
