using Npgsql;
using StackExchange.Redis;

namespace Dhole.Dynamics.Api;

/// <summary>
/// F6.10: Redis publishing runs only after PostgreSQL commits the job and
/// Outbox row. Multiple workers lease with SKIP LOCKED; eventId is stable
/// across at-least-once retries. Never publishes Dataverse business payloads.
/// </summary>
public sealed class DynamicsRealtimeOutboxPublisher(
    NpgsqlDataSource data, IConfiguration config,
    ILogger<DynamicsRealtimeOutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!DynamicsRealtimeJobEvent.IsEnabled(config))
            return;

        var redisConnection = config["Realtime:RedisConnectionString"];
        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            logger.LogError("Dynamics realtime publisher is disabled: Redis connection not configured.");
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var redis = await ConnectionMultiplexer.ConnectAsync(redisConnection);
                await PumpAsync(redis.GetDatabase(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Dynamics realtime publisher retry: {FailureType}",
                    exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task PumpAsync(IDatabase redis, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var connection = await data.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            var pending = new List<(Guid Id, string Json)>();
            await using (var query = new NpgsqlCommand("""
                SELECT event_id,payload_json::text
                FROM dynamics.realtime_job_outbox
                WHERE status='pending'
                ORDER BY created_at,event_id
                LIMIT 25 FOR UPDATE SKIP LOCKED
                """, connection, tx))
            await using (var reader = await query.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    pending.Add((reader.GetGuid(0), reader.GetString(1)));

            foreach (var (eventId, json) in pending)
            {
                ct.ThrowIfCancellationRequested();
                await redis.StreamAddAsync(DynamicsRealtimeJobEvent.Stream, new[]
                {
                    new NameValueEntry("event_id", eventId.ToString("D")),
                    new NameValueEntry("message_type", DynamicsRealtimeJobEvent.EventType),
                    new NameValueEntry("payload_json", json)
                });
                await using var done = new NpgsqlCommand("""
                    UPDATE dynamics.realtime_job_outbox
                    SET status='processed',processed_at=now()
                    WHERE event_id=@event AND status='pending'
                    """, connection, tx);
                done.Parameters.AddWithValue("event", eventId);
                await done.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            if (pending.Count == 0)
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }
}
