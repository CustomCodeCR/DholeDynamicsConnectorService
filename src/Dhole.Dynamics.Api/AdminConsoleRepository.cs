using Dhole.Dynamics.Application;
using Npgsql;

namespace Dhole.Dynamics.Api;

/// <summary>Bounded, sanitized operational views; never returns Dataverse data or stored secrets.</summary>
public sealed class AdminConsoleRepository(NpgsqlDataSource database)
{
    public async Task<object> OverviewAsync(CancellationToken ct)
    {
        await using var db = await database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM dynamics.connections),
              (SELECT count(*) FROM dynamics.api_clients WHERE enabled),
              (SELECT count(*) FROM dynamics.integration_templates WHERE active_version IS NOT NULL),
              (SELECT count(*) FROM dynamics.write_operations WHERE status IN ('ambiguous','reserved')),
              (SELECT count(*) FROM dynamics.write_operations WHERE status='rejected'
                 AND started_at > now()-interval '24 hours'),
              (SELECT coalesce(sum(request_count),0) FROM dynamics.api_quota_windows
                 WHERE window_minute >= extract(epoch from now())::bigint/60-60),
              (SELECT max(created_at) FROM dynamics.audit_events)
            """, db);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new { connections=r.GetInt64(0), clients=r.GetInt64(1), activeTemplates=r.GetInt64(2),
            needsReconciliation=r.GetInt64(3), rejectedWrites24h=r.GetInt64(4),
            requestsLastHour=Convert.ToInt64(r.GetDecimal(5)),
            lastEventAt=r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6),
            dynamicsQuotaNote="These are connector-local request counts, not the Dataverse tenant quota." };
    }

    public async Task<object> AuditAsync(Guid? connection, int limit, CancellationToken ct)
    {
        var rows = new List<object>();
        await using var db = await database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT id,connection_id,actor_id,action,created_at
            FROM dynamics.audit_events
            WHERE (CAST(@connection AS uuid) IS NULL OR connection_id=@connection)
            ORDER BY created_at DESC,id DESC LIMIT @limit
            """, db);
        cmd.Parameters.AddWithValue("connection",(object?)connection??DBNull.Value);
        cmd.Parameters.AddWithValue("limit",Math.Clamp(limit,1,100));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
            rows.Add(new { id=r.GetGuid(0),connectionId=r.IsDBNull(1)?(Guid?)null:r.GetGuid(1),
                actor=r.GetString(2), action=r.GetString(3), at=r.GetFieldValue<DateTimeOffset>(4) });
        return rows;
    }

    public async Task<object> JobsAsync(Guid? connection,int limit,CancellationToken ct)
    {
        var jobs=new List<object>();
        await using var db=await database.OpenConnectionAsync(ct);
        await using(var cmd=new NpgsqlCommand("""
            SELECT id,connection_id,entity_name,operation,status,started_at,finished_at,
                   http_status,record_id
            FROM dynamics.write_operations
            WHERE (CAST(@connection AS uuid) IS NULL OR connection_id=@connection)
            ORDER BY started_at DESC,id DESC LIMIT @limit
            """,db))
        {
            cmd.Parameters.AddWithValue("connection",(object?)connection??DBNull.Value);
            cmd.Parameters.AddWithValue("limit",Math.Clamp(limit,1,100));
            await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                jobs.Add(new {id=r.GetGuid(0),connectionId=r.GetGuid(1),entity=r.GetString(2),
                    operation=r.GetString(3),state=r.GetString(4),
                    startedAt=r.GetFieldValue<DateTimeOffset>(5),
                    finishedAt=r.IsDBNull(6)?(DateTimeOffset?)null:r.GetFieldValue<DateTimeOffset>(6),
                    httpStatus=r.IsDBNull(7)?(int?)null:r.GetInt32(7),
                    recordId=r.IsDBNull(8)?(Guid?)null:r.GetGuid(8),kind="write"});
        }
        return jobs;
    }

    public async Task<object> SchedulesAsync(Guid? connection,CancellationToken ct)
    {
        var schedules=new List<object>();
        await using var db=await database.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT s.connection_id,c.code,s.enabled,s.interval_minutes,s.next_run_at
            FROM dynamics.metadata_refresh_schedules s JOIN dynamics.connections c
              ON c.id=s.connection_id
            WHERE (CAST(@connection AS uuid) IS NULL OR s.connection_id=@connection)
            ORDER BY c.code LIMIT 200
            """,db);
        cmd.Parameters.AddWithValue("connection",(object?)connection??DBNull.Value);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
            schedules.Add(new {connectionId=r.GetGuid(0),code=r.GetString(1),
                enabled=r.GetBoolean(2),intervalMinutes=r.GetInt32(3),
                nextRunAt=r.GetFieldValue<DateTimeOffset>(4)});
        return schedules;
    }
}
