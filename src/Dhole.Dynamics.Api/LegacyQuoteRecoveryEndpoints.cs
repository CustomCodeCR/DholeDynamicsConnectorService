using System.Security.Claims;
using System.Text.Json;
using Dhole.Dynamics.Api;
using Dhole.Dynamics.Contracts;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Recover a successful legacy CREATE quote without creating another Quote.
/// Historical idempotency evidence is only a candidate: the existing quote
/// must be verified live and linked using an ETag-guarded PATCH.
/// </summary>
public static class LegacyQuoteRecoveryEndpoints
{
    public sealed record LinkRequest(Guid QuoteId, bool ConfirmExistingQuote);

    private sealed record HistoricalQuote(Guid QuoteId, bool CurrentRevisionUnsent);

    public static IEndpointRouteBuilder MapLegacyQuoteRecoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/keyed/templates/crear_oferta/legacy-link-status", Status)
            .AllowAnonymous().WithGroupName("v1").RequireRateLimiting("AdminRate");
        var admin = app.MapGroup("/api/admin/v1/connections/{code}")
            .WithGroupName("admin-v1").RequireAuthorization("SecurityAdmin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        admin.MapPost("/quote-sync-ledger/{rateId:guid}/{revision:int}/link-legacy", Link);
        return app;
    }

    private static async Task<HistoricalQuote> ResolveHistoricalAsync(
        NpgsqlDataSource data, Guid connectionId, Guid rateId, int revision,
        CancellationToken ct)
    {
        if (rateId == Guid.Empty || revision < 1)
            throw new ReadRejected(400, "legacy_quote_invalid_identity");
        var candidates = new HashSet<Guid>();
        var hasCreate = false;
        var currentRevisionUnsent = true;
        var pattern = "dhole-pricing-quote-" + rateId.ToString("N") + "-v%-header";
        await using var db = await data.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT idempotency_key, operation, status, record_id
            FROM dynamics.write_operations
            WHERE connection_id=@connection AND issuer=@issuer
              AND entity_name='quote' AND idempotency_key LIKE @pattern
            ORDER BY started_at
            LIMIT 101
            """, db);
        cmd.Parameters.AddWithValue("connection", connectionId);
        cmd.Parameters.AddWithValue("issuer", TemplateApiKeyRepository.Issuer);
        cmd.Parameters.AddWithValue("pattern", pattern);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var count = 0;
        while (await rows.ReadAsync(ct))
        {
            if (++count > 100) throw new ReadRejected(409, "legacy_quote_history_truncated");
            var parsedRevision = PricingQuoteTotalEndpoints.TryParseLegacyHeaderRevision(
                rows.GetString(0), rateId, revision);
            if (parsedRevision is null)
                throw new ReadRejected(409, "legacy_quote_history_unrecognized");
            var operation = rows.GetString(1);
            var state = rows.GetString(2);
            var id = rows.IsDBNull(3) ? Guid.Empty : rows.GetGuid(3);
            if (parsedRevision == revision)
                currentRevisionUnsent = false;
            if (state is "reserved" or "ambiguous" or "reconciled")
                throw new ReadRejected(409, "legacy_quote_unresolved_header");
            if (state != "succeeded") continue;
            if (operation != "create" || id == Guid.Empty)
                throw new ReadRejected(409, "legacy_quote_multiple_write_modes");
            hasCreate = true;
            candidates.Add(id);
        }
        if (!hasCreate || candidates.Count != 1)
            throw new ReadRejected(409, "legacy_quote_not_unique");

        // A previous total-line write or recovery-generation reservation for
        // THIS revision also makes it unsafe to replay. The header-only scan
        // above intentionally cannot prove this condition by itself.
        await using (var checkDb = await data.OpenConnectionAsync(ct))
        await using (var check = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM dynamics.write_operations
                WHERE connection_id=@connection AND issuer=@issuer
                  AND idempotency_key LIKE @revisionPrefix
            )
            """, checkDb))
        {
            check.Parameters.AddWithValue("connection", connectionId);
            check.Parameters.AddWithValue("issuer", TemplateApiKeyRepository.Issuer);
            check.Parameters.AddWithValue("revisionPrefix",
                "dhole-pricing-quote-" + rateId.ToString("N") + "-v" +
                revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-%");
            if (await check.ExecuteScalarAsync(ct) is true)
                currentRevisionUnsent = false;
        }
        return new(candidates.Single(), currentRevisionUnsent);
    }

    private static string KeyField(IReadOnlyList<string> keys)
    {
        if (keys.Count != 1 || keys[0] is not
            ("cr933_dholerateid" or "new_idtraasignado"))
            throw new ReadRejected(409, "legacy_quote_rate_key_not_ready");
        return keys[0];
    }

    private static async Task<IResult> Status(
        string connectionCode, Guid dholeRateId, int revisionNumber,
        TemplateApiKeyRepository keys, NpgsqlDataSource data,
        ReadSearchService reader, HttpContext http, CancellationToken ct)
    {
        try
        {
            if (connectionCode != "principal" || revisionNumber < 1)
                throw new ReadRejected(400, "legacy_quote_invalid_query");
            var key = await keys.ValidateAndChargeAsync(
                http.Request.Headers["X-Dhole-Api-Key"].ToString(),
                connectionCode, "crear_oferta", ct, chargeQuota: false);
            if (key.EntityName != "quote" || key.Operation != "upsert")
                throw new ReadRejected(409, "legacy_quote_upsert_required");
            var field = KeyField(key.KeyFields);
            var history = await ResolveHistoricalAsync(
                data, key.ConnectionId, dholeRateId, revisionNumber, ct);
            var actor = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("connector:issuer", TemplateApiKeyRepository.Issuer),
                new Claim("client_id", key.ClientId),
                new Claim("scope", "dynamics.records.read"),
                new Claim("sub", key.Id.ToString("D"))
            ], "DholePricingKey"));
            bool linked = false;
            if (history.CurrentRevisionUnsent)
            {
                try
                {
                    var found = await reader.ByKeyAsync(actor, connectionCode, "quote",
                        new Dictionary<string, JsonElement> {
                            [field] = JsonSerializer.SerializeToElement(dholeRateId.ToString("D"))
                        }, ["quoteid"], ct);
                    linked = found.Item.TryGetProperty("quoteid", out var quote) &&
                        quote.ValueKind == JsonValueKind.String &&
                        Guid.TryParse(quote.GetString(), out var quoteId) &&
                        quoteId == history.QuoteId;
                }
                catch (ReadRejected ex) when (ex.Status == 404) { }
            }
            return Results.Ok(new {
                linked,
                quoteId = history.QuoteId,
                currentRevisionUnsent = history.CurrentRevisionUnsent,
                noPostExecuted = true
            });
        }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http, ex.Status, ex.Code,
                "No se pudo confirmar un vínculo histórico seguro para esta QUO.");
        }
    }

    private static async Task<IResult> Link(
        string code, Guid rateId, int revision, LinkRequest request,
        NpgsqlDataSource data, WriteAccessService access,
        ReadSearchService reader, DataverseWriteTransport transport,
        ProductionWritePolicy production, ClaimsPrincipal administrator,
        HttpContext http, CancellationToken ct)
    {
        try
        {
            if (code != "principal" || rateId == Guid.Empty ||
                revision < 1 || request.QuoteId == Guid.Empty ||
                !request.ConfirmExistingQuote)
                throw new ReadRejected(400, "legacy_quote_confirmation_required");
            production.Demand("update");

            // Historical evidence must be unique and prove that this revision
            // has not reserved its own header. Never trust a client-supplied ID.
            var quote = await access.ResolveAsync(code, "quote", ct);
            var history = await ResolveHistoricalAsync(
                data, quote.Connection.Id, rateId, revision, ct);
            if (history.QuoteId != request.QuoteId || !history.CurrentRevisionUnsent)
                throw new ReadRejected(409, "legacy_quote_candidate_changed");

            // Restrict the operation to the single active UPSERT alternate key.
            string? keyField = null;
            await using (var db = await data.OpenConnectionAsync(ct))
            await using (var command = new NpgsqlCommand("""
                SELECT DISTINCT k.key_fields
                FROM dynamics.template_api_keys k
                JOIN dynamics.integration_templates t ON t.id=k.template_id
                WHERE k.connection_id=@connection AND k.enabled=true
                  AND k.expires_at>now() AND k.operation='upsert'
                  AND k.entity_name='quote' AND t.name='crear_oferta'
                  AND t.active_version=k.template_version
                """, db))
            {
                command.Parameters.AddWithValue("connection", quote.Connection.Id);
                await using var rows = await command.ExecuteReaderAsync(ct);
                while (await rows.ReadAsync(ct))
                {
                    var field = KeyField(rows.GetFieldValue<string[]>(0));
                    if (keyField is not null && keyField != field)
                        throw new ReadRejected(409, "legacy_quote_multiple_rate_keys");
                    keyField = field;
                }
            }
            if (keyField is null ||
                !(quote.Table.AlternateKeys ?? []).Any(k =>
                    k.Attributes.Count == 1 && k.Attributes[0] == keyField))
                throw new ReadRejected(409, "legacy_quote_alternate_key_not_active");
            var column = quote.Table.Columns.FirstOrDefault(c =>
                c.LogicalName == keyField);
            var permission = quote.Permissions.Fields.FirstOrDefault(f =>
                f.EntityName == "quote" && f.FieldName == keyField);
            if (column is not { Type: "String", CanUpdate: true, CanRead: true } ||
                permission is null || !permission.Enabled ||
                !permission.CanRead || !permission.CanUpdate ||
                permission.MetadataFingerprint != MetadataRepository.Fingerprint(column))
                throw new ReadRejected(409, "legacy_quote_rate_field_not_authorized");

            var current = await reader.ByIdAsync(administrator, code, "quote",
                request.QuoteId, ["quoteid", keyField], ct, admin: true);
            if (!current.Item.TryGetProperty("quoteid", out var identity) ||
                !Guid.TryParse(identity.GetString(), out var observed) ||
                observed != request.QuoteId)
                throw new ReadRejected(409, "legacy_quote_live_record_mismatch");
            var existing = current.Item.TryGetProperty(keyField, out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (!string.IsNullOrWhiteSpace(existing) &&
                !string.Equals(existing, rateId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                throw new ReadRejected(409, "legacy_quote_rate_key_already_assigned");
            if (string.Equals(existing, rateId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new { linked = true, alreadyLinked = true,
                    quoteId = request.QuoteId, noPostExecuted = true });
            if (string.IsNullOrWhiteSpace(current.ETag))
                throw new ReadRejected(409, "legacy_quote_etag_unavailable");
            GenericWriteService.RequireEtag(current.ETag);
            var result = await transport.SendAsync(new WriteCommand(
                "update", quote,
                "/api/data/v9.2/" + quote.Table.EntitySetName +
                    "(" + request.QuoteId.ToString("D") + ")",
                new Dictionary<string, object?> {
                    [keyField] = rateId.ToString("D")
                }, IfMatch: current.ETag), ct);
            if (result.HttpStatus is < 200 or >= 300 || result.Ambiguous)
                throw new ReadRejected(409, "legacy_quote_link_unconfirmed");

            // Verify the durable link from Dataverse itself. Only after this
            // can Pricing resume the original revision with its original key.
            var verified = await reader.ByIdAsync(administrator, code, "quote",
                request.QuoteId, ["quoteid", keyField], ct, admin: true);
            if (!verified.Item.TryGetProperty(keyField, out var linkedValue) ||
                linkedValue.ValueKind != JsonValueKind.String ||
                linkedValue.GetString() != rateId.ToString("D"))
                throw new ReadRejected(409, "legacy_quote_link_unconfirmed");

            await using var auditDb = await data.OpenConnectionAsync(ct);
            await using var audit = new NpgsqlCommand("""
                INSERT INTO dynamics.audit_events(id,connection_id,actor_id,action)
                VALUES (@id,@connection,@actor,'quote.legacy_rate_id.linked')
                """, auditDb);
            audit.Parameters.AddWithValue("id", Guid.NewGuid());
            audit.Parameters.AddWithValue("connection", quote.Connection.Id);
            audit.Parameters.AddWithValue("actor", ScopeSecurity.ActorId(administrator));
            await audit.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { linked = true, alreadyLinked = false,
                quoteId = request.QuoteId, noPostExecuted = false });
        }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http, ex.Status, ex.Code,
                "La QUO histórica no pudo vincularse de forma segura. No se creó otra QUO.");
        }
        catch (HttpRequestException)
        {
            return ApiProblems.Create(http, 409, "legacy_quote_link_unconfirmed",
                "No se confirmó el PATCH. Verificá el vínculo antes de repetir la operación.");
        }
        catch (TaskCanceledException)
        {
            return ApiProblems.Create(http, 409, "legacy_quote_link_unconfirmed",
                "Se perdió la confirmación del PATCH. Verificá Dataverse antes de repetir.");
        }
    }
}
