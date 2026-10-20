using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Npgsql;

namespace Dhole.Dynamics.Api;

/// <summary>Connection-bound immutable catalogs and explicit allowlists. Every edit is audited.</summary>
public sealed class MetadataRepository(NpgsqlDataSource database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Fingerprint(MetadataColumn field) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(field, Json))));

    public static MetadataDiff Diff(MetadataCatalog? prior, MetadataCatalog current, int? oldVersion, int version)
    {
        var left = (prior?.Tables ?? []).ToDictionary(t => t.LogicalName, StringComparer.Ordinal);
        var right = current.Tables.ToDictionary(t => t.LogicalName, StringComparer.Ordinal);
        var oldFields = left.Values.SelectMany(t => t.Columns.Select(c => (name: t.LogicalName + "." + c.LogicalName, c)))
            .ToDictionary(x => x.name, x => x.c, StringComparer.Ordinal);
        var newFields = right.Values.SelectMany(t => t.Columns.Select(c => (name: t.LogicalName + "." + c.LogicalName, c)))
            .ToDictionary(x => x.name, x => x.c, StringComparer.Ordinal);
        return new(oldVersion, version,
            prior is not null && JsonSerializer.Serialize(prior, Json) == JsonSerializer.Serialize(current, Json),
            right.Keys.Except(left.Keys).Order(StringComparer.Ordinal).ToArray(),
            left.Keys.Except(right.Keys).Order(StringComparer.Ordinal).ToArray(),
            left.Keys.Intersect(right.Keys).Where(k => JsonSerializer.Serialize(left[k], Json)
                != JsonSerializer.Serialize(right[k], Json)).Order(StringComparer.Ordinal).ToArray(),
            newFields.Keys.Except(oldFields.Keys).Order(StringComparer.Ordinal).ToArray(),
            oldFields.Keys.Except(newFields.Keys).Order(StringComparer.Ordinal).ToArray(),
            oldFields.Keys.Intersect(newFields.Keys).Where(k => Fingerprint(oldFields[k]) != Fingerprint(newFields[k]))
                .Order(StringComparer.Ordinal).ToArray());
    }

    public async Task<MetadataSnapshot?> LatestAsync(Guid connectionId, CancellationToken ct)
    {
        await using var db = await database.OpenConnectionAsync(ct);
        return await ReadLatestAsync(db, null, connectionId, ct);
    }

    private static async Task<MetadataSnapshot?> ReadLatestAsync(
        NpgsqlConnection db, NpgsqlTransaction? transaction, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, connection_id, version, captured_at, checksum, source, catalog::text
            FROM dynamics.metadata_snapshots WHERE connection_id=@id
            ORDER BY version DESC LIMIT 1
            """, db, transaction);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2),
            reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4), reader.GetString(5),
            JsonSerializer.Deserialize<MetadataCatalog>(reader.GetString(6), Json)
                ?? throw new InvalidOperationException("Persisted metadata is invalid."));
    }

    public Task<MetadataRefreshResult> SaveTableAsync(Guid connectionId, MetadataTable table,
        string actor, CancellationToken ct) =>
        SaveAsync(connectionId, new MetadataCatalog([table]), "manual", actor, ct, table.LogicalName);

    public async Task<MetadataRefreshResult> SaveAsync(Guid connectionId, MetadataCatalog catalog,
        string source, string actor, CancellationToken ct, string? mergeTable = null,
        bool preserveDetails = false)
    {
        // Validate and merge under the advisory lock to avoid concurrent table refreshes
        // overwriting one another or unreviewed schema drift silently granting fields.
        await using var db = await database.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var locker = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext(@id), 4004)", db, tx))
        {
            locker.Parameters.AddWithValue("id", connectionId.ToString("D"));
            await locker.ExecuteNonQueryAsync(ct);
        }
        var previous = await ReadLatestAsync(db, tx, connectionId, ct);
        if (mergeTable is not null)
        {
            if (previous is null || catalog.Tables.Count != 1 ||
                catalog.Tables[0].LogicalName != mergeTable)
                throw new InvalidOperationException("Table discovery requires an existing indexed table.");
            var indexed = previous.Catalog.Tables.SingleOrDefault(t => t.LogicalName == mergeTable)
                ?? throw new InvalidOperationException("Table was removed from the latest metadata index.");
            var discovered = catalog.Tables[0];
            if (indexed.EntitySetName != discovered.EntitySetName ||
                indexed.PrimaryIdAttribute != discovered.PrimaryIdAttribute)
                throw new InvalidOperationException("Dataverse table identity changed. Refresh the table index first.");
            catalog = new MetadataCatalog(previous.Catalog.Tables.Select(t =>
                t.LogicalName == mergeTable ? discovered : t).ToArray());
        }
        if (preserveDetails && previous is not null)
        {
            var prior = previous.Catalog.Tables.ToDictionary(t => t.LogicalName, StringComparer.Ordinal);
            catalog = new MetadataCatalog(catalog.Tables.Select(t =>
                prior.TryGetValue(t.LogicalName, out var old) &&
                old.EntitySetName == t.EntitySetName &&
                old.PrimaryIdAttribute == t.PrimaryIdAttribute
                    ? t with { Columns = old.Columns, Relationships = old.Relationships,
                        AlternateKeys = old.AlternateKeys }
                    : t).ToArray());
        }
        var body = JsonSerializer.Serialize(catalog, Json);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        if (previous?.Checksum == checksum)
        {
            await tx.CommitAsync(ct);
            return new(previous, Diff(previous.Catalog, catalog, previous.Version, previous.Version));
        }
        var version = (previous?.Version ?? 0) + 1;
        var diff = Diff(previous?.Catalog, catalog, previous?.Version, version);
        var current = new MetadataSnapshot(Guid.NewGuid(), connectionId, version,
            DateTimeOffset.UtcNow, checksum, source, catalog);
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.metadata_snapshots
              (id,connection_id,version,captured_at,checksum,source,catalog)
            VALUES(@id,@connection,@version,@date,@hash,@source,CAST(@catalog AS jsonb))
            """, db, tx))
        {
            cmd.Parameters.AddWithValue("id", current.Id);
            cmd.Parameters.AddWithValue("connection", connectionId);
            cmd.Parameters.AddWithValue("version", version);
            cmd.Parameters.AddWithValue("date", current.CapturedAt.UtcDateTime);
            cmd.Parameters.AddWithValue("hash", checksum);
            cmd.Parameters.AddWithValue("source", source);
            cmd.Parameters.AddWithValue("catalog", body);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // Revocation on schema drift is permanent until an administrator explicitly reviews/enables.
        // New entities and fields are intentionally NEVER enabled automatically.
        foreach (var name in diff.RemovedEntities)
        {
            await using var cmd = new NpgsqlCommand("""
                UPDATE dynamics.metadata_allowed_entities SET enabled=false,updated_at=now()
                WHERE connection_id=@id AND logical_name=@name
                """, db, tx);
            cmd.Parameters.AddWithValue("id", connectionId);
            cmd.Parameters.AddWithValue("name", name);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var qualified in diff.RemovedFields.Concat(diff.ChangedFields))
        {
            var dot = qualified.IndexOf('.');
            await using var cmd = new NpgsqlCommand("""
                UPDATE dynamics.metadata_allowed_fields SET enabled=false,
                  can_read=false,can_create=false,can_update=false,updated_at=now()
                WHERE connection_id=@id AND entity_name=@entity AND field_name=@field
                """, db, tx);
            cmd.Parameters.AddWithValue("id", connectionId);
            cmd.Parameters.AddWithValue("entity", qualified[..dot]);
            cmd.Parameters.AddWithValue("field", qualified[(dot + 1)..]);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(db, tx, connectionId, actor, "metadata.snapshot.refresh", ct);
        await tx.CommitAsync(ct);
        return new(current, diff);
    }

    public async Task<MetadataDiff?> CompareLatestAsync(Guid connectionId, CancellationToken ct)
    {
        await using var db = await database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT version, catalog::text FROM dynamics.metadata_snapshots
            WHERE connection_id=@id ORDER BY version DESC LIMIT 2
            """, db);
        cmd.Parameters.AddWithValue("id", connectionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var version = r.GetInt32(0);
        var latest = JsonSerializer.Deserialize<MetadataCatalog>(r.GetString(1), Json)!;
        if (!await r.ReadAsync(ct)) return Diff(null, latest, null, version);
        return Diff(JsonSerializer.Deserialize<MetadataCatalog>(r.GetString(1), Json),
            latest, r.GetInt32(0), version);
    }

    public async Task<MetadataPolicies> PoliciesAsync(Guid connectionId, CancellationToken ct)
    {
        var entities = new List<MetadataEntityPolicy>();
        var fields = new List<MetadataFieldPolicy>();
        await using var db = await database.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            SELECT logical_name, enabled FROM dynamics.metadata_allowed_entities
            WHERE connection_id=@id ORDER BY logical_name
            """, db))
        {
            cmd.Parameters.AddWithValue("id", connectionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                entities.Add(new(reader.GetString(0), reader.GetBoolean(1)));
        }
        await using (var cmd = new NpgsqlCommand("""
            SELECT entity_name,field_name,enabled,can_read,can_create,can_update,metadata_fingerprint
            FROM dynamics.metadata_allowed_fields WHERE connection_id=@id
            ORDER BY entity_name,field_name
            """, db))
        {
            cmd.Parameters.AddWithValue("id", connectionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                fields.Add(new(reader.GetString(0), reader.GetString(1),
                    reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4),
                    reader.GetBoolean(5), reader.GetString(6)));
        }
        return new(entities, fields);
    }

    public async Task<bool> SetEntityAsync(Guid connectionId, string entity, bool enabled,
        string actor, CancellationToken ct)
    {
        var snapshot = await LatestAsync(connectionId, ct);
        if (snapshot?.Catalog.Tables.All(t => t.LogicalName != entity) != false)
            return false;
        await using var db = await database.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.metadata_allowed_entities(connection_id,logical_name,enabled)
            VALUES(@id,@name,@enabled)
            ON CONFLICT(connection_id,logical_name) DO UPDATE SET
                enabled=EXCLUDED.enabled,updated_at=now()
            """, db, tx))
        {
            cmd.Parameters.AddWithValue("id", connectionId);
            cmd.Parameters.AddWithValue("name", entity);
            cmd.Parameters.AddWithValue("enabled", enabled);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(db, tx, connectionId, actor, "metadata.entity.policy", ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> SetFieldAsync(Guid connectionId, string entity, string field,
        MetadataFieldToggle value, string actor, CancellationToken ct)
    {
        var snapshot = await LatestAsync(connectionId, ct);
        var metadata = snapshot?.Catalog.Tables.FirstOrDefault(t => t.LogicalName == entity)
            ?.Columns.FirstOrDefault(c => c.LogicalName == field);
        if (metadata is null || (value.Enabled &&
            (!value.CanRead && !value.CanCreate && !value.CanUpdate
             || value.CanRead && !metadata.CanRead
             || value.CanCreate && !metadata.CanCreate
             || value.CanUpdate && !metadata.CanUpdate
             || (metadata.IsCalculated && (value.CanCreate || value.CanUpdate)))))
            return false;

        await using var db = await database.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.metadata_allowed_fields
              (connection_id,entity_name,field_name,enabled,can_read,can_create,can_update,metadata_fingerprint)
            SELECT @id,@entity,@field,@enabled,@read,@create,@update,@hash
            FROM dynamics.metadata_allowed_entities e
            WHERE e.connection_id=@id AND e.logical_name=@entity AND e.enabled=true
            ON CONFLICT(connection_id,entity_name,field_name) DO UPDATE SET
                enabled=EXCLUDED.enabled,can_read=EXCLUDED.can_read,
                can_create=EXCLUDED.can_create,can_update=EXCLUDED.can_update,
                metadata_fingerprint=EXCLUDED.metadata_fingerprint,updated_at=now()
            RETURNING field_name
            """, db, tx);
        cmd.Parameters.AddWithValue("id", connectionId);
        cmd.Parameters.AddWithValue("entity", entity);
        cmd.Parameters.AddWithValue("field", field);
        cmd.Parameters.AddWithValue("enabled", value.Enabled);
        cmd.Parameters.AddWithValue("read", value.Enabled && value.CanRead);
        cmd.Parameters.AddWithValue("create", value.Enabled && value.CanCreate);
        cmd.Parameters.AddWithValue("update", value.Enabled && value.CanUpdate);
        cmd.Parameters.AddWithValue("hash", Fingerprint(metadata));
        if (await cmd.ExecuteScalarAsync(ct) is null) return false;
        await AuditAsync(db, tx, connectionId, actor, "metadata.field.policy", ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Enables metadata-approved fields atomically for one explicitly approved table.
    /// Read-only bulk approval is additive: it never silently revokes an existing write grant.
    /// Actual Dataverse writes remain subject to production approvals and client grants.
    /// </summary>
    public async Task<MetadataBulkFieldResult> EnableFieldsBulkAsync(Guid connectionId,
        string entity, MetadataBulkFieldToggle request, string actor, CancellationToken ct)
    {
        if (!request.Confirm)
            throw new ReadRejected(400, "bulk_field_confirmation_required");
        if (request.SnapshotVersion <= 0)
            throw new ReadRejected(400, "invalid_metadata_version");

        await using var db = await database.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        // Serialize against refresh and per-table schema merges; a stale catalog never grants access.
        await using (var locker = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext(@id), 4004)", db, tx))
        {
            locker.Parameters.AddWithValue("id", connectionId.ToString("D"));
            await locker.ExecuteNonQueryAsync(ct);
        }

        var snapshot = await ReadLatestAsync(db, tx, connectionId, ct);
        if (snapshot is null || snapshot.Version != request.SnapshotVersion)
            throw new ReadRejected(409, "metadata_snapshot_changed");
        var table = snapshot.Catalog.Tables.SingleOrDefault(t => t.LogicalName == entity)
            ?? throw new ReadRejected(409, "entity_not_in_snapshot");
        if (table.Columns.Count == 0 || table.Columns.Count > 2500)
            throw new ReadRejected(409, "metadata_fields_not_discovered");

        await using (var guard = new NpgsqlCommand("""
            SELECT 1 FROM dynamics.metadata_allowed_entities
            WHERE connection_id=@id AND logical_name=@entity AND enabled=true FOR UPDATE
            """, db, tx))
        {
            guard.Parameters.AddWithValue("id", connectionId);
            guard.Parameters.AddWithValue("entity", entity);
            if (await guard.ExecuteScalarAsync(ct) is null)
                throw new ReadRejected(409, "entity_not_enabled");
        }

        var fields = table.Columns
            .Where(f => f.CanRead || request.IncludeWrite && !f.IsCalculated &&
                (f.CanCreate || f.CanUpdate))
            .ToArray();
        if (fields.Length == 0)
            throw new ReadRejected(409, "no_eligible_metadata_fields");

        var names = fields.Select(f => f.LogicalName).ToArray();
        var reads = fields.Select(f => f.CanRead).ToArray();
        var creates = fields.Select(f => request.IncludeWrite && f.CanCreate && !f.IsCalculated).ToArray();
        var updates = fields.Select(f => request.IncludeWrite && f.CanUpdate && !f.IsCalculated).ToArray();
        var fingerprints = fields.Select(Fingerprint).ToArray();

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.metadata_allowed_fields
              (connection_id,entity_name,field_name,enabled,can_read,can_create,can_update,metadata_fingerprint)
            SELECT @id,@entity,f.name,true,f.can_read,f.can_create,f.can_update,f.fingerprint
            FROM unnest(@names::text[],@reads::boolean[],@creates::boolean[],
                        @updates::boolean[],@fingerprints::text[])
                 AS f(name,can_read,can_create,can_update,fingerprint)
            ON CONFLICT(connection_id,entity_name,field_name) DO UPDATE SET
              enabled=true,
              can_read=EXCLUDED.can_read OR
                (dynamics.metadata_allowed_fields.can_read AND
                 dynamics.metadata_allowed_fields.metadata_fingerprint=EXCLUDED.metadata_fingerprint),
              can_create=EXCLUDED.can_create OR
                (dynamics.metadata_allowed_fields.can_create AND
                 dynamics.metadata_allowed_fields.metadata_fingerprint=EXCLUDED.metadata_fingerprint),
              can_update=EXCLUDED.can_update OR
                (dynamics.metadata_allowed_fields.can_update AND
                 dynamics.metadata_allowed_fields.metadata_fingerprint=EXCLUDED.metadata_fingerprint),
              metadata_fingerprint=EXCLUDED.metadata_fingerprint,updated_at=now()
            """, db, tx))
        {
            cmd.Parameters.AddWithValue("id", connectionId);
            cmd.Parameters.AddWithValue("entity", entity);
            cmd.Parameters.AddWithValue("names", names);
            cmd.Parameters.AddWithValue("reads", reads);
            cmd.Parameters.AddWithValue("creates", creates);
            cmd.Parameters.AddWithValue("updates", updates);
            cmd.Parameters.AddWithValue("fingerprints", fingerprints);
            if (await cmd.ExecuteNonQueryAsync(ct) != fields.Length)
                throw new InvalidOperationException("Bulk metadata field approval was incomplete.");
        }

        await AuditAsync(db, tx, connectionId, actor, request.IncludeWrite
            ? "metadata.fields.bulk.read_write" : "metadata.fields.bulk.read", ct);
        await tx.CommitAsync(ct);
        return new MetadataBulkFieldResult(snapshot.Version, fields.Length, reads.Count(x => x),
            creates.Count(x => x), updates.Count(x => x), request.IncludeWrite);
    }

    public async Task<MetadataSchedule> GetScheduleAsync(Guid id, CancellationToken ct)
    {
        await using var db = await database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT enabled,interval_minutes,next_run_at
            FROM dynamics.metadata_refresh_schedules WHERE connection_id=@id
            """, db);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(reader.GetBoolean(0), reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2))
            : new(false, 1440, null);
    }

    public async Task SetScheduleAsync(Guid id, MetadataSchedule schedule, string actor, CancellationToken ct)
    {
        await using var db = await database.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.metadata_refresh_schedules
                (connection_id,enabled,interval_minutes,next_run_at)
            VALUES(@id,@enabled,@minutes,now() + @minutes * interval '1 minute')
            ON CONFLICT(connection_id) DO UPDATE SET
                enabled=EXCLUDED.enabled,interval_minutes=EXCLUDED.interval_minutes,
                next_run_at=EXCLUDED.next_run_at,updated_at=now()
            """, db, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("enabled", schedule.Enabled);
            cmd.Parameters.AddWithValue("minutes", schedule.IntervalMinutes);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await AuditAsync(db, tx, id, actor, "metadata.schedule.update", ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ClaimDueAsync(CancellationToken ct)
    {
        var codes = new List<string>();
        await using var db = await database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            WITH due AS (
                SELECT connection_id FROM dynamics.metadata_refresh_schedules
                WHERE enabled AND next_run_at <= now()
                ORDER BY next_run_at FOR UPDATE SKIP LOCKED LIMIT 3
            ), claimed AS (
                UPDATE dynamics.metadata_refresh_schedules s
                SET next_run_at=now() + s.interval_minutes * interval '1 minute'
                FROM due WHERE s.connection_id=due.connection_id
                RETURNING s.connection_id
            )
            SELECT c.code FROM claimed JOIN dynamics.connections c ON c.id=claimed.connection_id
            """, db);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) codes.Add(reader.GetString(0));
        return codes;
    }

    private static async Task AuditAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid connectionId, string actor, string action, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
            VALUES(@id,@connection,@actor,@action)
            """, db, tx);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("connection", connectionId);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("action", action);
        await command.ExecuteNonQueryAsync(ct);
    }
}
