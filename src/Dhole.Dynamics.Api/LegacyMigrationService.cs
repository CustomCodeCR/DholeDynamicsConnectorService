using System.Reflection;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Npgsql;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Migration definitions are an embedded, source-reviewed release artifact. No legacy assembly,
/// credentials, arbitrary HTTP filters or external URLs are ever loaded into the import path.
/// </summary>
public sealed class LegacyMigrationService(ReadAccessRepository access,
    WriteAccessService writes, SavedQueryRepository savedQueries,
    TemplateRepository templates, NpgsqlDataSource database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<LegacyBundle> Release = new(ReadRelease);

    public static LegacyBundle Bundle => Release.Value;

    private static LegacyBundle ReadRelease()
    {
        using var stream = typeof(LegacyMigrationService).Assembly
            .GetManifestResourceStream("Dhole.Legacy.Phase09.Bundle")
            ?? throw new InvalidOperationException("Phase09 bundle missing from API release.");
        var bundle = JsonSerializer.Deserialize<LegacyBundle>(stream, Json)
            ?? throw new InvalidOperationException("Phase09 bundle is invalid.");
        if (bundle.Format != "dhole.legacy.phase09.v1" ||
            bundle.Entries.Count != 88 ||
            bundle.Entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != 88 ||
            bundle.Entries.Count(e => e.Method == "GET") != 87 ||
            bundle.Entries.Count(e => e.Method == "PATCH") != 1 ||
            bundle.Entries.Any(e => !e.Route.StartsWith("/dynamics/", StringComparison.Ordinal)
                || !System.Text.RegularExpressions.Regex.IsMatch(e.Id, @"^LEG-[0-9]{3}$")))
            throw new InvalidOperationException("Phase09 bundle does not match the audited 88-route inventory.");
        return bundle;
    }

    /// <summary>A read-only dry-run with exact currently enabled metadata; never authorizes unknown fields.</summary>
    public async Task<LegacyMigrationReport> PreviewAsync(string connectionCode, CancellationToken ct)
    {
        var conn = await access.ConnectionAsync(connectionCode, ct);
        var result = new List<LegacyMigrationItem>();
        var policies = new Dictionary<string, ReadPolicy?>(StringComparer.Ordinal);
        foreach (var item in Bundle.Entries)
        {
            if (item.Status == "blocked_source" || item.Query is null || item.Template is null)
            {
                result.Add(Item(item, "blocked_source", item.Reasons, [], null));
                continue;
            }
            if (!policies.TryGetValue(item.EntityName, out var policy))
            {
                try { policy = await access.ResolveAsync(connectionCode, item.EntityName, ct); }
                catch (ReadRejected) { policy = null; }
                policies[item.EntityName] = policy;
            }
            if (policy is null)
            {
                result.Add(Item(item, "needs_configuration",
                    ["entity_not_enabled_or_metadata_missing"], [], null));
                continue;
            }
            var columns = new List<string>();
            try
            {
                var bound = ReadSavedQueryBinder.Bind(item.Query, item.ParameterSchema,
                    new Dictionary<string, JsonElement>(), validateOnly: true);
                var compiled = ReadQueryCompiler.Compile(policy.Table, bound, policy.Allowed);
                columns.AddRange(compiled.RequiredFields);
                if (item.Template.EntityName != item.EntityName ||
                    item.Template.Operation != "find" ||
                    item.Template.SavedQuery != item.QueryName ||
                    item.Template.Inputs.Count != item.ParameterSchema.Count ||
                    item.Template.Inputs.Any(t => !item.ParameterSchema.Any(p =>
                        p.Name == t.Name && p.Type == t.Type && p.Required == t.Required)))
                    throw new ReadRejected(400, "invalid_legacy_template");
                var storedQuery = await savedQueries.FindAsync(conn.Id, item.QueryName!, ct);
                if (storedQuery is not null && !Same(storedQuery, item))
                {
                    result.Add(Item(item, "name_conflict", ["query_name_conflict"],
                        compiled.RequiredFields, policy.Snapshot.Version));
                    continue;
                }
                var existing = await templates.FindAsync(conn.Id, item.TemplateName, ct);
                if (existing is not null && !existing.History.Any(rev =>
                    JsonSerializer.Serialize(rev.Definition, Json) ==
                    JsonSerializer.Serialize(item.Template, Json)))
                {
                    result.Add(Item(item, "name_conflict", ["template_name_conflict"],
                        compiled.RequiredFields, policy.Snapshot.Version));
                    continue;
                }
                result.Add(Item(item, storedQuery is not null && existing is not null ?
                    "already_staged" : "ready_to_stage", [],
                    compiled.RequiredFields, policy.Snapshot.Version));
            }
            catch (Exception error) when (error is ReadRejected or ArgumentException
                or UnauthorizedAccessException or InvalidOperationException)
            {
                // No direct source values or parser internals leak into errors.
                result.Add(Item(item, "needs_configuration",
                    ["missing_or_unapproved_fields_or_unsupported_dataverse_type"],
                    columns, policy.Snapshot.Version));
            }
        }
        return Report(result);
    }

    /// <summary>Explicitly stage safe configuration drafts. Neither saved queries nor templates are enabled.</summary>
    public async Task<LegacyMigrationReport> ImportAsync(string code, bool confirmed,
        string actor, CancellationToken ct)
    {
        if (!confirmed) throw new ReadRejected(400, "legacy_import_confirmation_required");
        var conn = await access.ConnectionAsync(code, ct);
        var preview = await PreviewAsync(code, ct);
        var results = new List<LegacyMigrationItem>(preview.Items.Count);
        foreach (var item in preview.Items)
        {
            var result = item;
            if (item.Status == "ready_to_stage")
            {
                var entry = Bundle.Entries.Single(x => x.Id == item.Id);
                try
                {
                    // A race or external Admin edit must not cause a forced overwrite.
                    var current = await savedQueries.FindAsync(conn.Id, entry.QueryName!, ct);
                    if (current is not null && !Same(current, entry))
                        throw new ReadRejected(409, "query_changed_since_preview");
                    if (current is null)
                        await savedQueries.SaveAsync(conn.Id,
                            new SavedQueryWrite(entry.QueryName!, entry.EntityName, entry.Query!,
                                entry.ParameterSchema), actor, ct);

                    var active = await templates.FindAsync(conn.Id, entry.TemplateName, ct);
                    if (active is not null && !active.History.Any(v =>
                        JsonSerializer.Serialize(v.Definition, Json) ==
                        JsonSerializer.Serialize(entry.Template, Json)))
                        throw new ReadRejected(409, "template_changed_since_preview");
                    if (active is null)
                    {
                        // Validate a configuration-only draft; activation still requires enabled saved
                        // query, current metadata and explicit M2M grants.
                        var policy = await writes.ResolveAsync(code, entry.EntityName, ct);
                        TemplateMapper.ValidateDefinition(entry.Template!, policy);
                        await templates.NewRevisionAsync(conn.Id, entry.TemplateName,
                            entry.Template!, policy.Snapshot.Version, actor, ct);
                    }
                    result = item with { Status = "staged" };
                }
                catch (Exception error) when (error is ReadRejected or ArgumentException
                    or UnauthorizedAccessException)
                {
                    result = item with { Status = "needs_configuration",
                        Reasons = ["stage_validation_or_concurrent_change"] };
                }
            }
            await PersistStatusAsync(conn.Id, result, actor, ct);
            results.Add(result);
        }
        return Report(results);
    }

    public async Task<IReadOnlyList<LegacyMigrationItem>> HistoryAsync(string code,
        CancellationToken ct)
    {
        var conn = await access.ConnectionAsync(code, ct);
        var known = Bundle.Entries.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var results = new List<LegacyMigrationItem>();
        await using var db = await database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT source_id,status,reasons::text,metadata_version
              FROM dynamics.legacy_migration_items
              WHERE connection_id=@conn ORDER BY source_id LIMIT 88
            """, db);
        cmd.Parameters.AddWithValue("conn", conn.Id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            if (!known.TryGetValue(id, out var source)) continue;
            results.Add(Item(source, reader.GetString(1),
                JsonSerializer.Deserialize<string[]>(reader.GetString(2), Json) ?? [],
                [], reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }
        return results;
    }

    private async Task PersistStatusAsync(Guid conn, LegacyMigrationItem result,
        string actor, CancellationToken ct)
    {
        await using var db = await database.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO dynamics.legacy_migration_items
                (connection_id,source_id,package_format,status,reasons,metadata_version)
            VALUES(@connection,@id,@format,@status,CAST(@reasons AS jsonb),@version)
            ON CONFLICT(connection_id,source_id)
                DO UPDATE SET status=EXCLUDED.status,
                    reasons=EXCLUDED.reasons,metadata_version=EXCLUDED.metadata_version,
                    updated_at=now()
            """, db, tx);
        cmd.Parameters.AddWithValue("connection", conn);
        cmd.Parameters.AddWithValue("id", result.Id);
        cmd.Parameters.AddWithValue("format", Bundle.Format);
        cmd.Parameters.AddWithValue("status", result.Status);
        cmd.Parameters.AddWithValue("reasons", JsonSerializer.Serialize(result.Reasons, Json));
        cmd.Parameters.AddWithValue("version", (object?)result.MetadataVersion ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        await using var audit = new NpgsqlCommand("""
            INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
            VALUES(@id,@connection,@actor,'legacy.import.reviewed')
            """, db, tx);
        audit.Parameters.AddWithValue("id", Guid.NewGuid());
        audit.Parameters.AddWithValue("connection", conn);
        audit.Parameters.AddWithValue("actor", actor);
        await audit.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static bool Same(SavedQuerySummary existing, LegacyEndpoint entry) =>
        existing.EntityName == entry.EntityName &&
        JsonSerializer.Serialize(existing.Query, Json) ==
            JsonSerializer.Serialize(entry.Query, Json) &&
        JsonSerializer.Serialize(existing.Parameters, Json) ==
            JsonSerializer.Serialize(entry.ParameterSchema, Json);

    private static LegacyMigrationItem Item(LegacyEndpoint e, string state,
        IReadOnlyList<string> errors, IReadOnlyList<string> fields, int? version) =>
        new(e.Id, e.Route, e.Family, e.EntityName, state, e.QueryName ?? "",
            e.TemplateName, errors, fields, version);

    private static LegacyMigrationReport Report(IReadOnlyList<LegacyMigrationItem> records) =>
        new(Bundle.Format, Bundle.Source.Ref, records.Count,
            records.Count(x => x.Status == "ready_to_stage"),
            records.Count(x => x.Status is "staged" or "already_staged"),
            records.Count(x => x.Status is "blocked_source" or "needs_configuration" or "name_conflict"),
            records);
}
