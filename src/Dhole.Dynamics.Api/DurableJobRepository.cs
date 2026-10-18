using System.Security.Cryptography;
using System.Text;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Workers;
using Npgsql;

namespace Dhole.Dynamics.Api;

/// <summary>
/// PostgreSQL durable queue with lease fencing, connection isolation, redacted results and
/// transactionally persisted checkpoint + change manifest. No arbitrary serialized job payloads.
/// </summary>
public sealed class DurableJobRepository(NpgsqlDataSource data)
{
    private static QueueJob Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4),
        r.GetInt32(5),r.GetInt32(6),r.GetFieldValue<DateTimeOffset>(7),
        r.IsDBNull(8)?null:r.GetGuid(8),r.GetInt64(9),r.GetInt32(10),
        r.IsDBNull(11)?null:r.GetString(11),r.GetFieldValue<DateTimeOffset>(12));

    private const string Projection = """
        id,connection_id,kind,resource_key,status,attempts,max_attempts,available_at,
        lease_owner,lease_version,page_count,last_error_code,created_at
        """;

    public async Task<QueueJob?> EnqueueAsync(Guid connection,JobEnqueueRequest request,
        string actor,CancellationToken ct)
    {
        if(connection==Guid.Empty || !WriteLedger.ValidKey(request.DedupeKey) ||
            request.Kind is not ("delta_sync" or "metadata_refresh" or "legacy_preview" or
                "legacy_import" or "reconcile_scan") ||
            request.Kind=="delta_sync" && !Dhole.Dynamics.Infrastructure.DataverseMetadataDiscovery.SafeName(request.EntityName) ||
            request.Kind!="delta_sync" && !string.IsNullOrEmpty(request.EntityName))
            throw new ReadRejected(400,"invalid_job_definition");
        var entity = request.EntityName ?? "";
        await using var db=await data.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        if(request.Kind=="delta_sync")
        {
            await using var guard=new NpgsqlCommand("""
                SELECT 1 FROM dynamics.sync_config
                WHERE connection_id=@connection AND entity_name=@entity AND enabled FOR UPDATE
                """,db,tx);
            guard.Parameters.AddWithValue("connection",connection);
            guard.Parameters.AddWithValue("entity",entity);
            if(await guard.ExecuteScalarAsync(ct) is null)
                throw new ReadRejected(409,"sync_not_enabled");
        }
        await using var cmd=new NpgsqlCommand($"""
            INSERT INTO dynamics.job_queue
              (id,connection_id,kind,resource_key,dedupe_key,created_by)
            VALUES(@id,@connection,@kind,@resource,@dedupe,@actor)
            ON CONFLICT DO NOTHING
            RETURNING {Projection}
            """,db,tx);
        cmd.Parameters.AddWithValue("id",Guid.NewGuid());
        cmd.Parameters.AddWithValue("connection",connection);
        cmd.Parameters.AddWithValue("kind",request.Kind);
        cmd.Parameters.AddWithValue("resource",entity);
        cmd.Parameters.AddWithValue("dedupe",request.DedupeKey);
        cmd.Parameters.AddWithValue("actor",actor.Length>128?actor[..128]:actor);
        QueueJob? item;
        await using(var r=await cmd.ExecuteReaderAsync(ct))
            item=await r.ReadAsync(ct)?Read(r):null;
        if(item is null)
        {
            // A different active dedupe key is not silently accepted as the caller's job.
            await using var existing=new NpgsqlCommand($"""
                SELECT {Projection} FROM dynamics.job_queue
                WHERE connection_id=@connection AND kind=@kind AND resource_key=@resource
                  AND dedupe_key=@dedupe
                """,db,tx);
            existing.Parameters.AddWithValue("connection",connection);
            existing.Parameters.AddWithValue("kind",request.Kind);
            existing.Parameters.AddWithValue("resource",entity);
            existing.Parameters.AddWithValue("dedupe",request.DedupeKey);
            await using var r=await existing.ExecuteReaderAsync(ct);
            item=await r.ReadAsync(ct)?Read(r):null;
            if(item is null || item.Status=="dead")
                throw new ReadRejected(409,"job_already_queued_or_dead");
        }
        await tx.CommitAsync(ct);
        return item;
    }


    public async Task<string> ConnectionCodeAsync(Guid connection,CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT code FROM dynamics.connections WHERE id=@id
            """,db);
        cmd.Parameters.AddWithValue("id",connection);
        return (await cmd.ExecuteScalarAsync(ct) as string)
            ??throw new ReadRejected(404,"job_connection_missing");
    }

    public async Task<bool> RenewAsync(QueueJob claim,CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            UPDATE dynamics.job_queue SET lease_until=now()+interval '120 seconds',
              updated_at=now()
            WHERE id=@id AND status='running' AND lease_owner=@owner
              AND lease_version=@version AND lease_until>now()
            """,db);
        BindLease(cmd,claim);
        return await cmd.ExecuteNonQueryAsync(ct)==1;
    }

    public async Task<QueueJob?> ClaimAsync(Guid worker,CancellationToken ct)
    {
        if(worker==Guid.Empty)throw new ArgumentException("Invalid worker lease.");
        await using var db=await data.OpenConnectionAsync(ct);
        // Exhausted leases cannot spin forever after repeated process crashes.
        await using(var reap=new NpgsqlCommand("""
            WITH abandoned AS (
              UPDATE dynamics.job_queue SET status='dead',lease_owner=NULL,lease_until=NULL,
                last_error_code='lease_exhausted',finished_at=now(),updated_at=now()
              WHERE status='running' AND lease_until<now() AND attempts>=max_attempts
              RETURNING id,connection_id
            )
            INSERT INTO dynamics.job_alerts(id,connection_id,job_id,code)
            SELECT gen_random_uuid(),connection_id,id,'job_dead_letter' FROM abandoned
            """,db))
            await reap.ExecuteNonQueryAsync(ct);
        await using var cmd=new NpgsqlCommand($"""
            WITH claimed AS (
             SELECT q.id FROM dynamics.job_queue q
             LEFT JOIN dynamics.job_circuits c ON c.connection_id=q.connection_id
             WHERE (q.status='pending' AND q.available_at<=now() OR
                    q.status='running' AND q.lease_until<now())
               AND q.attempts<q.max_attempts
               AND (q.kind NOT IN ('delta_sync','metadata_refresh') OR
                    c.open_until IS NULL OR c.open_until<=now())
             ORDER BY q.available_at,q.created_at,q.id LIMIT 1 FOR UPDATE OF q SKIP LOCKED
            )
            UPDATE dynamics.job_queue q SET
              status='running',attempts=q.attempts+1,lease_owner=@worker,
              lease_until=now()+interval '120 seconds',lease_version=q.lease_version+1,
              updated_at=now()
            FROM claimed WHERE q.id=claimed.id RETURNING
            q.id,q.connection_id,q.kind,q.resource_key,q.status,q.attempts,
            q.max_attempts,q.available_at,q.lease_owner,q.lease_version,
            q.page_count,q.last_error_code,q.created_at
            """,db);
        cmd.Parameters.AddWithValue("worker",worker);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)?Read(reader):null;
    }

    public async Task<IReadOnlyList<QueueJob>> ListAsync(Guid? connection,int limit,CancellationToken ct)
    {
        var items=new List<QueueJob>();
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand($"""
            SELECT {Projection} FROM dynamics.job_queue
            WHERE (CAST(@conn AS uuid) IS NULL OR connection_id=@conn)
            ORDER BY created_at DESC,id DESC LIMIT @limit
            """,db);
        cmd.Parameters.AddWithValue("conn",(object?)connection??DBNull.Value);
        cmd.Parameters.AddWithValue("limit",Math.Clamp(limit,1,100));
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))items.Add(Read(r));
        return items;
    }

    public async Task<QueueJob?> FindAsync(Guid connection,Guid id,CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand($"SELECT {Projection} FROM dynamics.job_queue WHERE connection_id=@conn AND id=@id",db);
        cmd.Parameters.AddWithValue("conn",connection);
        cmd.Parameters.AddWithValue("id",id);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)?Read(r):null;
    }

    public async Task<QueueJob> ReplayAsync(Guid connection,Guid id,string actor,string reason,CancellationToken ct)
    {
        if(reason?.Trim().Length is < 15 or > 400)throw new ReadRejected(400,"replay_evidence_required");
        await using var db=await data.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand($"""
            UPDATE dynamics.job_queue SET status='pending',attempts=0,available_at=now(),
              last_error_code=NULL,finished_at=NULL,updated_at=now(),lease_owner=NULL,lease_until=NULL
            WHERE id=@id AND connection_id=@conn AND status='dead'
            RETURNING {Projection}
            """,db,tx);
        cmd.Parameters.AddWithValue("conn",connection);
        cmd.Parameters.AddWithValue("id",id);
        QueueJob? changed;
        try
        {
            await using var r=await cmd.ExecuteReaderAsync(ct);
            changed=await r.ReadAsync(ct)?Read(r):null;
        }
        catch(PostgresException ex) when(ex.SqlState==PostgresErrorCodes.UniqueViolation)
        {
            throw new ReadRejected(409,"job_conflicting_active_replay");
        }
        if(changed is null)throw new ReadRejected(409,"job_not_dead_or_conflicting");
        await AuditAsync(db,tx,connection,actor,"job.replayed.manual",ct);
        await tx.CommitAsync(ct);
        return changed;
    }

    public async Task FinishAsync(QueueJob claim,CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            UPDATE dynamics.job_queue SET status='completed',lease_owner=NULL,lease_until=NULL,
              finished_at=now(),updated_at=now()
            WHERE id=@id AND status='running' AND lease_owner=@owner
              AND lease_version=@version AND lease_until>now()
            """,db);
        BindLease(cmd,claim);
        if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new ReadRejected(409,"job_lease_lost");
        if(claim.Kind is "delta_sync" or "metadata_refresh")
            await ResetCircuitAsync(claim.ConnectionId,ct);
    }

    public async Task FailAsync(QueueJob claim,bool retryable,string errorCode,int? retrySeconds,
        CancellationToken ct)
    {
        if(errorCode.Length is < 1 or > 64 || errorCode.Any(c=>!(char.IsLower(c)||c=='_'||char.IsDigit(c))))
            throw new ArgumentException("Failure codes must be redacted constants.");
        var again=retryable && claim.Attempts<claim.MaxAttempts;
        int backoff=JobRetryPolicy.DelaySeconds(claim.Attempts,retrySeconds,Random.Shared.Next(0,12));
        await using var db=await data.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            UPDATE dynamics.job_queue SET status=@state,available_at=now()+make_interval(secs=>@backoff),
              last_error_code=@code,lease_owner=NULL,lease_until=NULL,updated_at=now(),
              finished_at=CASE WHEN @state='dead' THEN now() ELSE NULL END
            WHERE id=@id AND status='running' AND lease_owner=@owner
              AND lease_version=@version AND lease_until>now() RETURNING id
            """,db,tx);
        BindLease(cmd,claim);
        cmd.Parameters.AddWithValue("state",again?"pending":"dead");
        cmd.Parameters.AddWithValue("backoff",backoff);
        cmd.Parameters.AddWithValue("code",errorCode);
        if(await cmd.ExecuteScalarAsync(ct) is null)
            throw new ReadRejected(409,"job_lease_lost");
        if(!again)
            await AlertAsync(db,tx,claim.ConnectionId,claim.Id,"job_dead_letter",ct);
        if(retryable && claim.Kind is "delta_sync" or "metadata_refresh")
        {
            await using var circuit=new NpgsqlCommand("""
                INSERT INTO dynamics.job_circuits(connection_id,consecutive_failures,updated_at)
                VALUES(@conn,1,now())
                ON CONFLICT(connection_id) DO UPDATE SET
                  consecutive_failures=dynamics.job_circuits.consecutive_failures+1,
                  open_until=CASE WHEN dynamics.job_circuits.consecutive_failures+1>=3
                    THEN now()+interval '5 minutes' ELSE NULL END,
                  updated_at=now()
                """,db,tx);
            circuit.Parameters.AddWithValue("conn",claim.ConnectionId);
            await circuit.ExecuteNonQueryAsync(ct);
            await using var verify=new NpgsqlCommand("""
                SELECT open_until FROM dynamics.job_circuits WHERE connection_id=@conn
                """,db,tx);
            verify.Parameters.AddWithValue("conn",claim.ConnectionId);
            if(await verify.ExecuteScalarAsync(ct) is DateTimeOffset)
                await AlertAsync(db,tx,claim.ConnectionId,claim.Id,"circuit_open",ct);
        }
        await tx.CommitAsync(ct);
    }

    private async Task ResetCircuitAsync(Guid connection,CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            UPDATE dynamics.job_circuits SET consecutive_failures=0,open_until=NULL,updated_at=now()
            WHERE connection_id=@conn
            """,db);
        cmd.Parameters.AddWithValue("conn",connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SavePageAsync(QueueJob claim,string mode,SyncPage page,
        DateTimeOffset scanStarted,CancellationToken ct)
    {
        if(claim.Kind!="delta_sync" || mode is not ("delta" or "watermark") ||
            page.Changes.Count>100 || page.NextLink?.Length>4096 || page.DeltaLink?.Length>4096)
            throw new ReadRejected(400,"invalid_sync_page");
        await using var db=await data.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var owned=new NpgsqlCommand("""
            UPDATE dynamics.job_queue SET
              status=CASE WHEN CAST(@next AS text) IS NULL THEN 'completed' ELSE 'pending' END,
              available_at=now(),lease_owner=NULL,lease_until=NULL,
              finished_at=CASE WHEN CAST(@next AS text) IS NULL THEN now() ELSE NULL END,
              page_count=page_count+1,updated_at=now()
            WHERE id=@id AND lease_owner=@owner AND lease_version=@version AND
              status='running' AND lease_until>now() RETURNING id
            """,db,tx);
        BindLease(owned,claim);
        owned.Parameters.AddWithValue("next",(object?)page.NextLink??DBNull.Value);
        if(await owned.ExecuteScalarAsync(ct) is null)throw new ReadRejected(409,"job_lease_lost");

        foreach(var item in page.Changes)
        {
            if(item.RecordId==Guid.Empty || item.ChangeHash.Length!=64 ||
                item.Action is not ("delete" or "upsert"))
                throw new ReadRejected(502,"invalid_sync_event");
            await using var ev=new NpgsqlCommand("""
                INSERT INTO dynamics.sync_changes(connection_id,entity_name,record_id,change_hash,action)
                VALUES(@conn,@entity,@id,@hash,@action)
                ON CONFLICT DO NOTHING
                """,db,tx);
            ev.Parameters.AddWithValue("conn",claim.ConnectionId);
            ev.Parameters.AddWithValue("entity",claim.ResourceKey);
            ev.Parameters.AddWithValue("id",item.RecordId);
            ev.Parameters.AddWithValue("hash",item.ChangeHash);
            ev.Parameters.AddWithValue("action",item.Action);
            await ev.ExecuteNonQueryAsync(ct);
        }
        await using var update=new NpgsqlCommand("""
            INSERT INTO dynamics.sync_checkpoints
              (connection_id,entity_name,mode,next_link,delta_link,watermark_utc,scan_started_at,
               generation,pages_processed,events_processed,last_success_at)
            VALUES(@conn,@entity,@mode,@next,@delta,@watermark,@started,1,1,@count,
                   CASE WHEN CAST(@next AS text) IS NULL THEN now() ELSE NULL END)
            ON CONFLICT(connection_id,entity_name) DO UPDATE SET
              mode=EXCLUDED.mode,next_link=EXCLUDED.next_link,
              delta_link=COALESCE(EXCLUDED.delta_link,dynamics.sync_checkpoints.delta_link),
              watermark_utc=CASE WHEN @mode='watermark' AND CAST(@next AS text) IS NULL
                THEN @started ELSE dynamics.sync_checkpoints.watermark_utc END,
              scan_started_at=EXCLUDED.scan_started_at,
              generation=dynamics.sync_checkpoints.generation+1,
              pages_processed=dynamics.sync_checkpoints.pages_processed+1,
              events_processed=dynamics.sync_checkpoints.events_processed+@count,
              last_success_at=CASE WHEN CAST(@next AS text) IS NULL
                THEN now() ELSE dynamics.sync_checkpoints.last_success_at END
            """,db,tx);
        update.Parameters.AddWithValue("conn",claim.ConnectionId);
        update.Parameters.AddWithValue("entity",claim.ResourceKey);
        update.Parameters.AddWithValue("mode",mode);
        update.Parameters.AddWithValue("next",(object?)page.NextLink??DBNull.Value);
        update.Parameters.AddWithValue("delta",(object?)page.DeltaLink??DBNull.Value);
        update.Parameters.AddWithValue("watermark",mode=="watermark" && page.NextLink is null
            ? (object)scanStarted : DBNull.Value);
        update.Parameters.AddWithValue("started",scanStarted);
        update.Parameters.AddWithValue("count",page.Changes.Count);
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        if(page.NextLink is null)await ResetCircuitAsync(claim.ConnectionId,ct);
    }

    public async Task<SyncSettings?> GetConfigAsync(Guid connection,string entity,CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT entity_name,enabled,mode,fallback_to_watermark,interval_minutes
            FROM dynamics.sync_config WHERE connection_id=@conn AND entity_name=@entity
            """,db);
        cmd.Parameters.AddWithValue("conn",connection);
        cmd.Parameters.AddWithValue("entity",entity);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetString(0),r.GetBoolean(1),
            r.GetString(2),r.GetBoolean(3),r.GetInt32(4)) : null;
    }

    public async Task<IReadOnlyList<SyncSettings>> ConfigurationsAsync(Guid connection,CancellationToken ct)
    {
        var result=new List<SyncSettings>();
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT entity_name,enabled,mode,fallback_to_watermark,interval_minutes
            FROM dynamics.sync_config WHERE connection_id=@conn ORDER BY entity_name LIMIT 100
            """,db);
        cmd.Parameters.AddWithValue("conn",connection);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))result.Add(new(r.GetString(0),r.GetBoolean(1),r.GetString(2),
            r.GetBoolean(3),r.GetInt32(4)));
        return result;
    }

    public async Task SetConfigAsync(Guid connection,SyncSettings input,string actor,CancellationToken ct)
    {
        if(input.Mode is not ("delta" or "watermark") || input.IntervalMinutes is < 15 or > 10080 ||
           !Dhole.Dynamics.Infrastructure.DataverseMetadataDiscovery.SafeName(input.EntityName))
            throw new ReadRejected(400,"invalid_sync_configuration");
        await using var db=await data.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        // Serialize configuration mutation against manual enqueue and scheduled sync jobs.
        await using(var locked=new NpgsqlCommand("""
            SELECT 1 FROM dynamics.sync_config WHERE connection_id=@conn AND entity_name=@entity
            FOR UPDATE
            """,db,tx))
        {
            locked.Parameters.AddWithValue("conn",connection);
            locked.Parameters.AddWithValue("entity",input.EntityName);
            await locked.ExecuteScalarAsync(ct);
        }
        await using(var busy=new NpgsqlCommand("""
            SELECT 1 FROM dynamics.job_queue WHERE connection_id=@conn AND kind='delta_sync'
              AND resource_key=@entity AND status IN ('pending','running') LIMIT 1
            """,db,tx))
        {
            busy.Parameters.AddWithValue("conn",connection);
            busy.Parameters.AddWithValue("entity",input.EntityName);
            if(await busy.ExecuteScalarAsync(ct) is not null)
                throw new ReadRejected(409,"sync_job_in_progress");
        }
        await using(var cfg=new NpgsqlCommand("""
            INSERT INTO dynamics.sync_config(connection_id,entity_name,enabled,mode,
              fallback_to_watermark,interval_minutes,next_run_at)
            VALUES(@conn,@entity,@enabled,@mode,@fallback,@interval,now())
            ON CONFLICT(connection_id,entity_name) DO UPDATE SET
              enabled=EXCLUDED.enabled,mode=EXCLUDED.mode,
              fallback_to_watermark=EXCLUDED.fallback_to_watermark,
              interval_minutes=EXCLUDED.interval_minutes,next_run_at=now()
            """,db,tx))
        {
            cfg.Parameters.AddWithValue("conn",connection);
            cfg.Parameters.AddWithValue("entity",input.EntityName);
            cfg.Parameters.AddWithValue("enabled",input.Enabled);
            cfg.Parameters.AddWithValue("mode",input.Mode);
            cfg.Parameters.AddWithValue("fallback",input.FallbackToWatermark);
            cfg.Parameters.AddWithValue("interval",input.IntervalMinutes);
            await cfg.ExecuteNonQueryAsync(ct);
        }
        // Mode/config changes reset the cursor rather than attempt to interpret foreign delta tokens.
        await using(var reset=new NpgsqlCommand("""
            INSERT INTO dynamics.sync_checkpoints(connection_id,entity_name,mode)
            VALUES(@conn,@entity,@mode)
            ON CONFLICT(connection_id,entity_name) DO UPDATE SET
               mode=EXCLUDED.mode,
               next_link=CASE WHEN dynamics.sync_checkpoints.mode=EXCLUDED.mode
                  THEN dynamics.sync_checkpoints.next_link ELSE NULL END,
               delta_link=CASE WHEN dynamics.sync_checkpoints.mode=EXCLUDED.mode
                  THEN dynamics.sync_checkpoints.delta_link ELSE NULL END,
               scan_started_at=CASE WHEN dynamics.sync_checkpoints.mode=EXCLUDED.mode
                  THEN dynamics.sync_checkpoints.scan_started_at ELSE NULL END,
               watermark_utc=CASE WHEN dynamics.sync_checkpoints.mode=EXCLUDED.mode
                  THEN dynamics.sync_checkpoints.watermark_utc ELSE NULL END,
               generation=dynamics.sync_checkpoints.generation+1
            """,db,tx))
        {
            reset.Parameters.AddWithValue("conn",connection);
            reset.Parameters.AddWithValue("entity",input.EntityName);
            reset.Parameters.AddWithValue("mode",input.Mode);
            await reset.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(db,tx,connection,actor,"sync.config.changed",ct);
        await tx.CommitAsync(ct);
    }

    public async Task<(string Mode,string? Next,string? Delta,DateTimeOffset? Watermark,
        DateTimeOffset? Started)> CursorAsync(Guid connection,string entity,CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT mode,next_link,delta_link,watermark_utc,scan_started_at
            FROM dynamics.sync_checkpoints WHERE connection_id=@conn AND entity_name=@entity
            """,db);
        cmd.Parameters.AddWithValue("conn",connection);
        cmd.Parameters.AddWithValue("entity",entity);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct))return ("delta",null,null,null,null);
        return (r.GetString(0),r.IsDBNull(1)?null:r.GetString(1),
            r.IsDBNull(2)?null:r.GetString(2),
            r.IsDBNull(3)?null:r.GetFieldValue<DateTimeOffset>(3),
            r.IsDBNull(4)?null:r.GetFieldValue<DateTimeOffset>(4));
    }

    public async Task UseWatermarkAsync(QueueJob claim,CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            UPDATE dynamics.sync_checkpoints SET mode='watermark',next_link=NULL,delta_link=NULL,
              scan_started_at=NULL,watermark_utc=COALESCE(watermark_utc,now()-interval '7 days'),
              generation=generation+1
            WHERE connection_id=@conn AND entity_name=@entity
              AND EXISTS(SELECT 1 FROM dynamics.job_queue WHERE id=@id AND
                lease_owner=@owner AND lease_version=@version AND lease_until>now() AND status='running')
            """,db);
        cmd.Parameters.AddWithValue("conn",claim.ConnectionId);
        cmd.Parameters.AddWithValue("entity",claim.ResourceKey);
        BindLease(cmd,claim);
        if(await cmd.ExecuteNonQueryAsync(ct)!=1)throw new ReadRejected(409,"job_lease_lost");
    }

    public async Task ScheduleDueAsync(CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var select=new NpgsqlCommand("""
            SELECT connection_id,entity_name,next_run_at,interval_minutes
            FROM dynamics.sync_config WHERE enabled AND next_run_at<=now()
            ORDER BY next_run_at LIMIT 20 FOR UPDATE SKIP LOCKED
            """,db,tx);
        var due=new List<(Guid Conn,string Entity,DateTimeOffset Due,int Interval)>();
        await using(var r=await select.ExecuteReaderAsync(ct))
            while(await r.ReadAsync(ct))due.Add((r.GetGuid(0),r.GetString(1),
                r.GetFieldValue<DateTimeOffset>(2),r.GetInt32(3)));
        foreach(var item in due)
        {
            await using var insert=new NpgsqlCommand("""
                INSERT INTO dynamics.job_queue
                  (id,connection_id,kind,resource_key,dedupe_key,created_by)
                VALUES(@id,@conn,'delta_sync',@entity,@dedupe,'system:job-scheduler')
                ON CONFLICT DO NOTHING RETURNING id
                """,db,tx);
            insert.Parameters.AddWithValue("id",Guid.NewGuid());
            insert.Parameters.AddWithValue("conn",item.Conn);
            insert.Parameters.AddWithValue("entity",item.Entity);
            insert.Parameters.AddWithValue("dedupe","sync-"+Guid.NewGuid().ToString("N"));
            if(await insert.ExecuteScalarAsync(ct) is null)continue;
            await using var update=new NpgsqlCommand("""
                UPDATE dynamics.sync_config SET next_run_at=now()+make_interval(mins=>@interval)
                WHERE connection_id=@conn AND entity_name=@entity
                """,db,tx);
            update.Parameters.AddWithValue("conn",item.Conn);
            update.Parameters.AddWithValue("entity",item.Entity);
            update.Parameters.AddWithValue("interval",item.Interval);
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<JobStatistics> StatsAsync(CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM dynamics.job_queue WHERE status='pending'),
              (SELECT count(*) FROM dynamics.job_queue WHERE status='running'),
              (SELECT count(*) FROM dynamics.job_queue WHERE status='dead'),
              (SELECT count(*) FROM dynamics.job_queue WHERE status='completed'),
              (SELECT count(*) FROM dynamics.job_alerts WHERE acknowledged_at IS NULL),
              (SELECT count(*) FROM dynamics.sync_changes WHERE observed_at>now()-interval '24 hours'),
              (SELECT count(*) FROM dynamics.job_queue
                WHERE status='running' AND lease_until<now())
            """,db);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),r.GetInt64(3),
            r.GetInt64(4),r.GetInt64(5),r.GetInt64(6));
    }

    public async Task<object> CheckpointsAsync(Guid connection,CancellationToken ct)
    {
        var rows=new List<object>();
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT entity_name,mode,generation,pages_processed,events_processed,
              next_link IS NOT NULL,delta_link IS NOT NULL,last_success_at
            FROM dynamics.sync_checkpoints WHERE connection_id=@conn ORDER BY entity_name LIMIT 100
            """,db);
        cmd.Parameters.AddWithValue("conn",connection);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
            rows.Add(new {entity=r.GetString(0),mode=r.GetString(1),version=r.GetInt64(2),
                pages=r.GetInt64(3),changes=r.GetInt64(4),resumable=r.GetBoolean(5),
                deltaReady=r.GetBoolean(6),lastSuccessAt=r.IsDBNull(7)?(DateTimeOffset?)null:
                    r.GetFieldValue<DateTimeOffset>(7)});
        return rows;
    }

    public async Task<IReadOnlyList<object>> AlertsAsync(Guid? connection,CancellationToken ct)
    {
        var items=new List<object>();
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT id,connection_id,job_id,code,created_at,acknowledged_at
            FROM dynamics.job_alerts WHERE (CAST(@conn AS uuid) IS NULL OR connection_id=@conn)
            ORDER BY created_at DESC LIMIT 100
            """,db);
        cmd.Parameters.AddWithValue("conn",(object?)connection??DBNull.Value);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))items.Add(new {id=r.GetGuid(0),connectionId=r.GetGuid(1),
          jobId=r.IsDBNull(2)?(Guid?)null:r.GetGuid(2),code=r.GetString(3),
          createdAt=r.GetFieldValue<DateTimeOffset>(4),
          acknowledgedAt=r.IsDBNull(5)?(DateTimeOffset?)null:r.GetFieldValue<DateTimeOffset>(5)});
        return items;
    }

    public async Task AckAsync(Guid id,string actor,CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            UPDATE dynamics.job_alerts SET acknowledged_at=now()
            WHERE id=@id AND acknowledged_at IS NULL RETURNING connection_id
            """,db,tx);
        cmd.Parameters.AddWithValue("id",id);
        if(await cmd.ExecuteScalarAsync(ct) is not Guid connection)
            throw new ReadRejected(404,"alert_missing");
        await AuditAsync(db,tx,connection,actor,"job.alert.acknowledged",ct);
        await tx.CommitAsync(ct);
    }

    public async Task ReconciliationScanAsync(QueueJob job,CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var count=new NpgsqlCommand("""
            SELECT count(*) FROM dynamics.write_operations WHERE connection_id=@conn AND
              status IN ('ambiguous','reserved') AND started_at<now()-interval '5 minutes'
            """,db,tx);
        count.Parameters.AddWithValue("conn",job.ConnectionId);
        if((long)(await count.ExecuteScalarAsync(ct))!>0)
            await AlertAsync(db,tx,job.ConnectionId,job.Id,"write_reconciliation_required",ct);
        await tx.CommitAsync(ct);
    }

    public async Task PurgeAsync(CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        // Retain unsettled jobs, unacknowledged alerts and write-operation ledger indefinitely
        // (ledger is the safeguard against duplicate Dataverse writes).
        await using var cmd=new NpgsqlCommand("""
            DELETE FROM dynamics.http_observations WHERE observed_at<
                now()-make_interval(days=>(SELECT observation_days FROM dynamics.operational_retention WHERE id=1));
            DELETE FROM dynamics.sync_changes WHERE observed_at<
                now()-make_interval(days=>(SELECT change_days FROM dynamics.operational_retention WHERE id=1));
            DELETE FROM dynamics.job_alerts WHERE acknowledged_at IS NOT NULL AND acknowledged_at<
                now()-make_interval(days=>(SELECT job_days FROM dynamics.operational_retention WHERE id=1));
            DELETE FROM dynamics.job_queue WHERE status='completed' AND finished_at<
                now()-make_interval(days=>(SELECT job_days FROM dynamics.operational_retention WHERE id=1));
            """,db,tx);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<RetentionPolicy> RetentionAsync(CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT job_days,observation_days,change_days FROM dynamics.operational_retention WHERE id=1
            """,db);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2));
    }

    public async Task SetRetentionAsync(RetentionPolicy input,string actor,CancellationToken ct)
    {
        if(new[]{input.JobDays,input.ObservationDays,input.ChangeDays}
            .Any(n=>n is < 7 or > 365))throw new ReadRejected(400,"invalid_retention_policy");
        await using var db=await data.OpenConnectionAsync(ct);
        await using var tx=await db.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            UPDATE dynamics.operational_retention SET job_days=@jobs,
              observation_days=@observations,change_days=@changes,updated_at=now() WHERE id=1
            """,db,tx);
        cmd.Parameters.AddWithValue("jobs",input.JobDays);
        cmd.Parameters.AddWithValue("observations",input.ObservationDays);
        cmd.Parameters.AddWithValue("changes",input.ChangeDays);
        await cmd.ExecuteNonQueryAsync(ct);
        await using var audit=new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,actor_id,action) VALUES(@id,@actor,'operations.retention.changed')
            """,db,tx);
        audit.Parameters.AddWithValue("id",Guid.NewGuid());
        audit.Parameters.AddWithValue("actor",actor);
        await audit.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<byte[]> ExportAuditAsync(Guid? connection,CancellationToken ct)
    {
        await using var db=await data.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT id,connection_id,action,created_at
            FROM dynamics.audit_events
            WHERE (CAST(@conn AS uuid) IS NULL OR connection_id=@conn)
            ORDER BY created_at DESC,id DESC LIMIT 1000
            """,db);
        cmd.Parameters.AddWithValue("conn",(object?)connection??DBNull.Value);
        var sb=new StringBuilder("id,connection_id,action,created_at_utc\r\n");
        await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            // Structural action codes only; do not export actor IDs, payloads, names or tokens.
            var action=r.GetString(2);
            var normalized=action.All(c=>char.IsLetterOrDigit(c)||c is '.' or '_' or '-')
                ?action:"redacted";
            sb.Append(r.GetGuid(0)).Append(',')
              .Append(r.IsDBNull(1)?"":r.GetGuid(1).ToString()).Append(',')
              .Append(normalized).Append(',')
              .Append(r.GetFieldValue<DateTimeOffset>(3).ToUniversalTime().ToString("O"))
              .Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void BindLease(NpgsqlCommand cmd,QueueJob claim)
    {
        cmd.Parameters.AddWithValue("id",claim.Id);
        cmd.Parameters.AddWithValue("owner",claim.LeaseOwner??Guid.Empty);
        cmd.Parameters.AddWithValue("version",claim.LeaseVersion);
    }
    private static async Task AlertAsync(NpgsqlConnection db,NpgsqlTransaction tx,
        Guid conn,Guid id,string code,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.job_alerts(id,connection_id,job_id,code)
            VALUES(@id,@conn,@job,@code)
            """,db,tx);
        cmd.Parameters.AddWithValue("id",Guid.NewGuid());
        cmd.Parameters.AddWithValue("conn",conn);
        cmd.Parameters.AddWithValue("job",id);
        cmd.Parameters.AddWithValue("code",code);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task AuditAsync(NpgsqlConnection db,NpgsqlTransaction tx,
        Guid conn,string actor,string action,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
            VALUES(@id,@conn,@actor,@action)
            """,db,tx);
        cmd.Parameters.AddWithValue("id",Guid.NewGuid());
        cmd.Parameters.AddWithValue("conn",conn);
        cmd.Parameters.AddWithValue("actor",actor);
        cmd.Parameters.AddWithValue("action",action);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
