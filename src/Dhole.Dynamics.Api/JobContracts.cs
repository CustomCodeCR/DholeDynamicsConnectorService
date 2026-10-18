namespace Dhole.Dynamics.Api;

public sealed record QueueJob(Guid Id, Guid ConnectionId, string Kind, string ResourceKey,
 string Status, int Attempts, int MaxAttempts, DateTimeOffset AvailableAt,
 Guid? LeaseOwner, long LeaseVersion, int PageCount, string? LastErrorCode,
 DateTimeOffset CreatedAt);
public sealed record SyncSettings(string EntityName, bool Enabled, string Mode,
 bool FallbackToWatermark, int IntervalMinutes);
public sealed record JobEnqueueRequest(string Kind, string? EntityName, string DedupeKey, bool Confirm = false);
public sealed record JobReplayRequest(bool Confirm, string Reason);
public sealed record RetentionPolicy(int JobDays, int ObservationDays, int ChangeDays);
public sealed record JobStatistics(long Pending, long Running, long Dead, long Completed,
 long UnacknowledgedAlerts, long Changes24h, long StaleLeases);
public sealed record SyncChange(Guid RecordId, string ChangeHash, string Action);
public sealed record SyncPage(IReadOnlyList<SyncChange> Changes, string? NextLink,
 string? DeltaLink, bool UnsupportedDelta = false);
public sealed class SyncRequestFailure(int statusCode, int? retrySeconds = null)
 : Exception("Dataverse sync request failed.")
{
 public int StatusCode { get; } = statusCode;
 public int? RetrySeconds { get; } = retrySeconds;
}

/// <summary>Read-only, privacy-minimized change manifest visible only through an M2M read grant.</summary>
public sealed record SyncChangeEntry(long Sequence,Guid RecordId,string ChangeHash,
    string Action,DateTimeOffset ObservedAt);
public sealed record SyncChangesPage(string EntityName,IReadOnlyList<SyncChangeEntry> Items,
    long NextAfter,bool HasMore);
