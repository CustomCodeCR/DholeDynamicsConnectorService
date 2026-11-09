using System.Text.Json;
using Npgsql;

namespace Dhole.Dynamics.Api;

/// <summary>
/// F6.10: owner-scoped job invalidation; never exposes Dataverse records,
/// metadata mappings, tenants, OAuth tokens, secrets, URLs or job payloads.
/// </summary>
public static class DynamicsRealtimeJobEvent
{
    public const string ReleaseId = "rt-20261107-01";
    public const string EventType = "dynamics.job.changed.v1";
    public const string Stream = "dhole.dynamics.realtime.v1";
    private static long _lastMicrosecond;

    public static bool IsEnabled(IConfiguration? flags) =>
        flags?.GetValue<bool>("Features:UniversalRealtimeEnabled") == true &&
        flags.GetValue<bool>("Realtime:Outbox:Enabled") &&
        flags.GetValue<bool>("Realtime:BatchApproved") &&
        string.Equals(flags["Realtime:ReleaseId"], ReleaseId, StringComparison.Ordinal);

    public static long NextVersionHint()
    {
        while (true)
        {
            var now = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
            var prior = Volatile.Read(ref _lastMicrosecond);
            var next = Math.Max(now, prior + 1);
            if (Interlocked.CompareExchange(ref _lastMicrosecond, next, prior) == prior)
                return next;
        }
    }

    public static string Serialize(Guid eventId, Guid jobId, long version,
        DateTimeOffset at, string correlationId)
    {
        if (eventId == Guid.Empty || jobId == Guid.Empty ||
            version <= 0 || at.Offset != TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Invalid Dynamics job invalidation.");
        return JsonSerializer.Serialize(new
        {
            eventId,
            eventType = EventType,
            schemaVersion = 1,
            aggregateType = "job",
            aggregateId = jobId.ToString("D"),
            aggregateVersion = version,
            occurredAtUtc = at,
            producer = "DholeDynamicsConnectorService",
            correlationId,
            audienceRef = $"dynamics:job:{jobId:D}",
            payload = new { changedFields = new[] { "job" } }
        });
    }

    public static async Task AppendAsync(NpgsqlConnection db,
        NpgsqlTransaction tx, Guid jobId, Guid connectionId, CancellationToken ct)
    {
        var eventId = Guid.NewGuid();
        var json = Serialize(eventId, jobId, NextVersionHint(),
            DateTimeOffset.UtcNow, eventId.ToString("D"));
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.realtime_job_outbox(
                event_id,connection_id,job_id,payload_json,status)
            VALUES (@event,@connection,@job,@json::jsonb,'pending')
            """, db, tx);
        cmd.Parameters.AddWithValue("event", eventId);
        cmd.Parameters.AddWithValue("connection", connectionId);
        cmd.Parameters.AddWithValue("job", jobId);
        cmd.Parameters.AddWithValue("json", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
