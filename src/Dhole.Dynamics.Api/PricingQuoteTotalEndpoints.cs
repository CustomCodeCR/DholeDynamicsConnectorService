using System.Security.Claims;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Narrowly scoped two-step Pricing integration. Uses the active quote template
/// for the header, then an ordinary, separately authorized Dataverse quotedetail
/// creation to make quote.totalamount calculate from a sales line. Both writes
/// have stable, distinct idempotency keys and can be resumed safely after a
/// confirmed header success. No arbitrary records, fields, or targets are accepted.
/// </summary>
public static class PricingQuoteTotalEndpoints
{
    public sealed record PricingQuoteTotalRequest(
        string ConnectionCode, Guid DholeRateId, decimal TotalSaleAmount,
        IReadOnlyDictionary<string, JsonElement> Inputs);

    public sealed record PricingQuoteTotalResponse(
        string State, Guid? RecordId, Guid? LineRecordId,
        string Stage, bool Replay);

    public static IEndpointRouteBuilder MapPricingQuoteTotalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/keyed/templates/crear_oferta/execute-with-total", Execute)
            .AllowAnonymous().WithGroupName("v1").RequireRateLimiting("AdminRate");
        // Read-only, key-protected check used by Pricing before an explicit
        // retry. It neither reserves write idempotency nor contacts Dataverse.
        app.MapGet("/api/v1/keyed/templates/crear_oferta/write-readiness", WriteReadiness)
            .AllowAnonymous().WithGroupName("v1").RequireRateLimiting("AdminRate");
        app.MapGet("/api/v1/keyed/templates/crear_oferta/recovery-eligibility", RecoveryEligibility)
            .AllowAnonymous().WithGroupName("v1").RequireRateLimiting("AdminRate");
        // A read-only preview of the exact active quote template: checks
        // actual name lookups, but cannot reserve/create any Dataverse record.
        app.MapPost("/api/v1/keyed/templates/crear_oferta/validate-lookups", ValidateLookups)
            .AllowAnonymous().WithGroupName("v1").RequireRateLimiting("AdminRate");
        var admin = app.MapGroup("/api/admin/v1/connections/{code}")
            .WithGroupName("admin-v1").RequireAuthorization("SecurityAdmin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        admin.MapPost("/quote-total-grants/{keyId:guid}", GrantQuoteTotal);
        // Read-only inspection of the two exact idempotency keys for one
        // Pricing revision. SecurityAdmin only; never returns request bodies.
        admin.MapGet("/quote-sync-ledger/{rateId:guid}/{revision:int}", GetQuoteSyncLedger);
        return app;
    }


    private static async Task<IResult> GetQuoteSyncLedger(
        string code, Guid rateId, int revision, NpgsqlDataSource data,
        CancellationToken ct)
    {
        if (code != "principal" || rateId == Guid.Empty || revision is < 1 or > 999999999)
            return Results.BadRequest(new { code = "invalid_quote_ledger_query" });

        var prefix = "dhole-pricing-quote-" + rateId.ToString("N") + "-v" +
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var keys = new[] { prefix + "-header", prefix + "-total" };
        var entries = new Dictionary<string, object?>(StringComparer.Ordinal);
        await using var db = await data.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT w.idempotency_key, w.entity_name, w.status,
                   w.http_status, w.record_id,
                   w.response->>'reference'
            FROM dynamics.write_operations w
            INNER JOIN dynamics.connections c ON c.id = w.connection_id
            WHERE c.code = @code
              AND w.issuer = @issuer
              AND w.operation = 'create'
              AND w.idempotency_key = ANY(@keys)
            ORDER BY w.started_at DESC
            """, db);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("issuer", TemplateApiKeyRepository.Issuer);
        cmd.Parameters.AddWithValue("keys", keys);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            var key = rows.GetString(0);
            var isHeader = key == keys[0];
            if (!isHeader && key != keys[1]) continue;
            if (rows.GetString(1) != (isHeader ? "quote" : "quotedetail")) continue;
            var status = rows.GetString(2);
            if (status is not ("reserved" or "succeeded" or "rejected" or
                              "ambiguous" or "reconciled")) continue;
            var httpStatus = rows.IsDBNull(3) ? (int?)null : rows.GetInt32(3);
            var recordId = rows.IsDBNull(4) ? (Guid?)null : rows.GetGuid(4);
            var reference = rows.IsDBNull(5) ? null : rows.GetString(5);
            var providerCode = SafeDataverseCode(reference);
            var item = new { status, httpStatus, recordId, providerCode };
            entries[isHeader ? "header" : "total"] = item;
        }
        // NotFound/not_sent is NOT proof of no Dataverse side effect; it only
        // describes this connector's durable ledger at query time.
        return Results.Ok(new
        {
            rateId, revisionNumber = revision,
            header = entries.GetValueOrDefault("header") ??
                (object)new { status = "not_sent", httpStatus = (int?)null,
                    recordId = (Guid?)null, providerCode = (string?)null },
            total = entries.GetValueOrDefault("total") ??
                (object)new { status = "not_sent", httpStatus = (int?)null,
                    recordId = (Guid?)null, providerCode = (string?)null },
            noPostExecuted = true,
            message = "Consulta de operaciones registradas; no se envió ningún POST a Dataverse."
        });
    }

    private static bool IsCustomerBind(string target) =>
        (target.StartsWith("customerid_", StringComparison.OrdinalIgnoreCase) ||
         target.StartsWith("msdyn_account", StringComparison.OrdinalIgnoreCase)) &&
        target.EndsWith("@odata.bind", StringComparison.Ordinal);

    internal static bool ProductionQuoteRouteAllowed(ProductionWritePolicy gate)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/keyed/templates/crear_oferta/execute-with-total";
        return !gate.IsBlocked(context.Request);
    }

    private static async Task<IResult> WriteReadiness(
        string connectionCode, TemplateApiKeyRepository keys,
        ProductionWritePolicy gate, TemplateRepository templates,
        WriteAccessService access, NpgsqlDataSource database,
        HttpContext http, CancellationToken ct)
    {
        try
        {
            if (connectionCode != "principal")
                throw new ReadRejected(400, "invalid_pricing_connection");
            var key = await keys.ValidateAndChargeAsync(
                http.Request.Headers["X-Dhole-Api-Key"].ToString(),
                connectionCode, "crear_oferta", ct, chargeQuota: false);
            if (key.Operation != "create" || key.EntityName != "quote")
                throw new ReadRejected(403, "quote_creation_key_required");
            var allowed = ProductionQuoteRouteAllowed(gate);
            var grantsReady = allowed && await QuoteGrantsReadyAsync(
                key.Id, key.ConnectionId, connectionCode, templates, access, database, ct);
            return Results.Ok(new
            {
                ready = allowed && grantsReady,
                code = !allowed ? "production_writes_not_approved" :
                    !grantsReady ? "quote_grants_incomplete" : "DYNAMICS_QUOTE_WRITES_READY",
                message = !allowed
                    ? "El conector rechaza escrituras de QUO en producción; requiere autorización aprobada."
                    : !grantsReady
                    ? "La clave de Pricing carece de permisos vigentes para crear la cabecera, leer sus vínculos o crear la línea de venta. Autorizar vínculos y venta total con esa misma clave."
                    : "Política y permisos de cabecera, vínculos y línea de venta verificados. No se creó ninguna QUO.",
                noPostExecuted = true
            });
        }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http, ex.Status, ex.Code,
                "No se pudo validar la preparación de la clave para QUO.");
        }
    }

    /// <summary>
    /// Read-only grant preflight for the ACTUAL Pricing key. Production policy
    /// approval by itself is not evidence that account/IDTRA/currency lookups
    /// or the quotedetail line are authorized.
    /// </summary>
    private static async Task<bool> QuoteGrantsReadyAsync(
        Guid keyId, Guid connectionId, string code, TemplateRepository templates,
        WriteAccessService access, NpgsqlDataSource database, CancellationToken ct)
    {
        WritePolicy quote;
        try { quote = await access.ResolveAsync(code, "quote", ct); }
        catch (ReadRejected) { return false; }
        var template = await templates.FindAsync(connectionId, "crear_oferta", ct);
        var definition = template?.History.FirstOrDefault(v =>
            v.Version == template.ActiveVersion)?.Definition;
        if (definition is null || definition.EntityName != "quote" ||
            definition.Operation != "create") return false;
        var grants = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using (var db = await database.OpenConnectionAsync(ct))
        await using (var command = new NpgsqlCommand("""
            SELECT g.entity_set,g.operation,g.allowed_fields
            FROM dynamics.api_grants g
            JOIN dynamics.template_api_keys k ON k.client_id=g.api_client_id
            WHERE k.id=@key AND g.connection_id=@connection
              AND g.enabled=true
            """, db))
        {
            command.Parameters.AddWithValue("key", keyId);
            command.Parameters.AddWithValue("connection", connectionId);
            await using var rows = await command.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
                grants[rows.GetString(0) + ":" + rows.GetString(1)] =
                    rows.GetFieldValue<string[]>(2).ToHashSet(StringComparer.Ordinal);
        }
        bool Has(string entity, string operation, IEnumerable<string> fields) =>
            grants.TryGetValue(entity + ":" + operation, out var allowed) &&
            fields.All(allowed.Contains);
        if (!Has("quote", "create", ["name"]) ||
            !Has("quote", "read", [quote.Table.PrimaryIdAttribute]) ||
            !Has("quotedetail", "create",
                ["quoteid", "isproductoverridden", "ispriceoverridden",
                 "productdescription", "quotedetailname", "quantity", "priceperunit"]))
            return false;
        // Do not accept an unrelated account lookup in place of Dataverse's
        // required customerid relationship on quote.
        if (!definition.Mappings.Any(m => m.Target.StartsWith(
                "customerid_", StringComparison.OrdinalIgnoreCase) &&
                m.Target.EndsWith("@odata.bind", StringComparison.Ordinal)))
            return false;
        foreach (var mapping in definition.Mappings.Where(m =>
            m.Target.EndsWith("@odata.bind", StringComparison.Ordinal)))
        {
            var relation = quote.Table.Relationships.FirstOrDefault(r =>
                r.Kind == "ManyToOneRelationships" &&
                r.ReferencingNavigation == mapping.Target[..^"@odata.bind".Length] &&
                r.ReferencingEntity == "quote");
            if (relation?.ReferencedEntity is not string target)
                return false;
            WritePolicy related;
            try { related = await access.ResolveAsync(code, target, ct); }
            catch (ReadRejected) { return false; }
            if (!Has(target, "read", [related.Table.PrimaryIdAttribute]))
                return false;
            foreach (var step in mapping.Steps ?? [])
            {
                if (step.Op != "lookup_name") continue;
                if (string.IsNullOrWhiteSpace(step.Argument)) return false;
                var candidates = TemplateOrchestrator.ResolveLookupNameFields(
                    target, relation.ReferencingAttribute ?? "", step.Argument);
                if (!candidates.Any(field => Has(target, "read",
                    [related.Table.PrimaryIdAttribute, field])))
                    return false;
            }
        }
        return true;
    }

    private static async Task<IResult> ValidateLookups(
        TemplateInputs request, TemplateApiKeyRepository keys,
        TemplateRepository templates, TemplateOrchestrator writer,
        WriteAccessService access, HttpContext http, CancellationToken ct)
    {
        try
        {
            if (request.ConnectionCode != "principal" || request.Inputs is null ||
                request.Inputs.Count is < 1 or > 64)
                throw new ReadRejected(400, "invalid_quote_lookup_validation");
            var key = await keys.ValidateAndChargeAsync(
                http.Request.Headers["X-Dhole-Api-Key"].ToString(),
                request.ConnectionCode, "crear_oferta", ct, chargeQuota: false);
            if (key.EntityName != "quote" || key.Operation != "create")
                throw new ReadRejected(403, "quote_creation_key_required");
            var current = await templates.FindAsync(key.ConnectionId, "crear_oferta", ct);
            if (current?.ActiveVersion is not int version)
                throw new ReadRejected(409, "crud_template_inactive");
            var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("connector:issuer", TemplateApiKeyRepository.Issuer),
                new Claim("client_id", key.ClientId),
                new Claim("scope", "dynamics.records.create dynamics.records.read"),
                new Claim("sub", key.Id.ToString("D"))
            ], "DholePricingKey"));
            // PreviewAsync applies the live metadata, the saved per-field
            // grants, and exact-name lookups. It NEVER calls CreateAsync.
            var preview = await writer.PreviewAsync(request.ConnectionCode, "crear_oferta",
                version, request.Inputs, ct, actor, omitMissingQuoteCustomer: true);
            var customerOmitted = !preview.Payload.Keys.Any(target =>
                target.StartsWith("customerid_", StringComparison.OrdinalIgnoreCase) &&
                target.EndsWith("@odata.bind", StringComparison.Ordinal));
            if (customerOmitted)
                throw new ReadRejected(422, "quote_customer_required", "customerid", "name");
            // Preview must enforce the same header write grant as the real
            // execution. A successful name lookup alone is not sufficient.
            var quotePolicy = await access.ResolveAsync(request.ConnectionCode, "quote", ct);
            await access.PermitAsync(actor, quotePolicy, "create", preview.Fields, ct);
            return Results.Ok(new
            {
                valid = true,
                code = "DYNAMICS_LOOKUPS_VALID",
                schemaVersion = version,
                customerOmitted = false,
                noPostExecuted = true,
                message = "Cliente obligatorio, campos y relaciones validados sin crear ofertas."
            });
        }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http, ex.Status, ex.Code,
                "Quote lookup validation failed before any write.",
                ex.LookupInput, ex.LookupSearchField);
        }
    }

    private static async Task<IResult> Execute(PricingQuoteTotalRequest request,
        TemplateApiKeyRepository keys, TemplateRepository templates,
        WriteAccessService access, TemplateOrchestrator templatesWriter,
        GenericWriteService writer, DataverseMetadataDiscovery discovery,
        ProductionWritePolicy gate,
        HttpContext http, CancellationToken ct)
    {
        var quoteConfirmedCreated = false;
        try
        {
            if (request.ConnectionCode != "principal" ||
                request.DholeRateId == Guid.Empty ||
                request.TotalSaleAmount < 0 || request.TotalSaleAmount > 999999999999m ||
                request.Inputs is null || request.Inputs.Count is < 1 or > 64)
                throw new ReadRejected(400, "invalid_pricing_quote_request");
            var rawKey = http.Request.Headers["Idempotency-Key"].ToString();
            if (!WriteLedger.ValidKey(rawKey))
                throw new ReadRejected(400, "pricing_quote_idempotency_key_invalid");
            var (revision, recoveryAttempt) = ParsePricingKey(rawKey, request.DholeRateId);

            var key = await keys.ValidateAndChargeAsync(
                http.Request.Headers["X-Dhole-Api-Key"].ToString(),
                request.ConnectionCode, "crear_oferta", ct);
            if (key.EntityName != "quote" || key.Operation != "create")
                throw new ReadRejected(403, "quote_creation_key_required");
            gate.DemandPricingQuote();
            // The underlying WriteLedger is scoped by API client. When an
            // administrator rotates a key, the SAME Pricing idempotency key
            // must never be silently treated as a brand new Quote request.
            if (await PricingKeyUsedByAnotherClientAsync(
                http.RequestServices.GetRequiredService<NpgsqlDataSource>(),
                key.ConnectionId, key.ClientId, rawKey, ct))
                throw new ReadRejected(409, "pricing_quote_prior_key_requires_reconciliation");
            if (recoveryAttempt > 0)
            {
                var allowed = await IsRecoveryEligibleAsync(
                    http.RequestServices.GetRequiredService<NpgsqlDataSource>(),
                    key.ConnectionId, key.ClientId,
                    request.DholeRateId, revision, recoveryAttempt, ct);
                if (!allowed)
                    throw new ReadRejected(409, "recovery_not_safe");
            }

            var claims = new[]
            {
                new Claim("connector:issuer", TemplateApiKeyRepository.Issuer),
                new Claim("client_id", key.ClientId),
                new Claim("scope", "dynamics.records.create dynamics.records.read"),
                new Claim("sub", key.Id.ToString("D"))
            };
            var actor = new ClaimsPrincipal(new ClaimsIdentity(claims, "DholePricingKey"));

            // Validate the exact current quote template before any write.
            var quote = await access.ResolveAsync(request.ConnectionCode, "quote", ct);
            var template = await templates.FindAsync(quote.Connection.Id, "crear_oferta", ct);
            if (template?.ActiveVersion is not int version)
                throw new ReadRejected(409, "crud_template_inactive");
            var definition = template.History.FirstOrDefault(h => h.Version == version)?.Definition;
            if (definition is null || definition.EntityName != "quote" ||
                definition.Operation != "create")
                throw new ReadRejected(409, "crud_template_scope_denied");

            var preview = await templatesWriter.PreviewAsync(request.ConnectionCode,
                "crear_oferta", version, request.Inputs, ct, actor,
                omitMissingQuoteCustomer: true);
            await access.PermitAsync(actor, quote, "create", preview.Fields, ct);
            // customerid is ApplicationRequired in Dataverse quote. Suppressing
            // an unknown customer only hides the lookup failure and produces a
            // Dataverse 400. Fail BEFORE any POST with an actionable error.
            if (!preview.Payload.Keys.Any(k => k.StartsWith(
                    "customerid_", StringComparison.OrdinalIgnoreCase) &&
                    k.EndsWith("@odata.bind", StringComparison.Ordinal)))
                throw new ReadRejected(422, "quote_customer_required");

            // Check quotedetail metadata, field grants and quote lookup read
            // permission *before* creating a quote. A dummy nonempty GUID is
            // only used for permission/relationship validation, never sent.
            var detail = await access.ResolveAsync(request.ConnectionCode, "quotedetail", ct);
            if (!detail.Table.Relationships.Any(r =>
                r.ReferencingAttribute == "quoteid" &&
                r.ReferencingEntity == "quotedetail" &&
                r.ReferencedEntity == "quote" &&
                !string.IsNullOrEmpty(r.ReferencingNavigation)))
            {
                // Lightweight metadata snapshots often omit relationship
                // navigation names. Discover it live; never guess a bind key.
                var live = await discovery.DiscoverTableAsync(
                    detail.Connection, "quotedetail", ct, includeExtended: true);
                detail = detail with { Table = live };
            }
            var relation = detail.Table.Relationships.SingleOrDefault(r =>
                r.Kind == "ManyToOneRelationships" &&
                r.ReferencingAttribute == "quoteid" &&
                r.ReferencingEntity == "quotedetail" &&
                r.ReferencedEntity == "quote" &&
                !string.IsNullOrEmpty(r.ReferencingNavigation));
            if (relation is null)
                throw new ReadRejected(409, "quote_detail_relationship_missing");
            var validationLine = BuildLine(request.DholeRateId,
                request.TotalSaleAmount, Guid.NewGuid(), relation.ReferencingNavigation!);
            var compiled = await WriteValueCompiler.CompileAsync(detail, validationLine,
                (rel, id, token) => access.EnsureLookupAsync(actor, detail, rel, id, token), ct);
            await access.PermitAsync(actor, detail, "create", compiled.Fields, ct);

            // The prefix is common but the suffix is different for the two
            // separate WriteLedger entries (header and sales line).
            var header = await templatesWriter.ExecuteAsync(actor, "crear_oferta",
                new TemplateInputs(request.ConnectionCode, request.Inputs),
                rawKey + "-header", ct, omitMissingQuoteCustomer: true);
            if (header is WriteResult { State: "rejected" } rejectedHeader)
            {
                // Dataverse explicitly returned a non-success HTTP status for
                // the header. Do not mislabel this as an ambiguous 409 or
                // claim that the quote total line was attempted.
                var status = rejectedHeader.HttpStatus is >= 400 and < 500
                    ? rejectedHeader.HttpStatus.Value : 400;
                var safeCode = SafeDataverseCode(rejectedHeader.Reference);
                return ApiProblems.Create(http, status, "quote_header_dataverse_rejected",
                    "Dataverse rechazó la cabecera de la QUO (HTTP " + status +
                    "). No se intentó crear la línea de venta." +
                    (safeCode is null ? "" : " Código Dataverse: " + safeCode));
            }
            if (header is not WriteResult created || created.State != "succeeded" ||
                created.RecordId is not Guid quoteId || quoteId == Guid.Empty)
                return Results.Json(new PricingQuoteTotalResponse("requires_reconciliation",
                    null, null, "quote", false), statusCode: 409);

            quoteConfirmedCreated = true;
            // The client has the right to create quotedetail only through the
            // hardcoded aggregate line request here, not arbitrary field input.
            var line = await writer.CreateWithResolvedMetadataAsync(actor, detail,
                new GenericCreateRequest(request.ConnectionCode,
                    BuildLine(request.DholeRateId, request.TotalSaleAmount, quoteId,
                        relation.ReferencingNavigation!)),
                rawKey + "-total", ct);
            if (line.State != "succeeded" || line.RecordId is not Guid lineId ||
                lineId == Guid.Empty)
                return Results.Json(new PricingQuoteTotalResponse("requires_reconciliation",
                    quoteId, null, "total", created.Replay), statusCode: 409);

            return Results.Ok(new PricingQuoteTotalResponse("succeeded",
                quoteId, lineId, "completed", created.Replay && line.Replay));
        }
        catch (ReadRejected ex)
        {
            // Once a quote exists, never report a safe pre-write failure:
            // remediation must resume the total line with the SAME keys.
            return quoteConfirmedCreated
                ? ApiProblems.Create(http, 409, "quote_total_partial_requires_reconciliation",
                    "Quote header created; total line was not confirmed. Check idempotency ledger.")
                : ApiProblems.Create(http, ex.Status, ex.Code,
                    "Pricing quote creation rejected by Dataverse connector policy.",
                    ex.LookupInput, ex.LookupSearchField);
        }
        catch (HttpRequestException)
        {
            return ApiProblems.Create(http, 502, "dataverse_unavailable",
                "Dataverse unavailable. Reconcile before retrying.");
        }
        catch (TaskCanceledException)
        {
            return ApiProblems.Create(http, 504, "dataverse_timeout",
                "Dataverse timed out. Reconcile before retrying.");
        }
    }

    // The transport saves only a vetted Dataverse error code; never echo
    // the provider's untrusted message, request body or client information.
    private static string? SafeDataverseCode(string? reference)
    {
        const string prefix = "dataverse_code:";
        if (reference is null || !reference.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var code = reference[prefix.Length..];
        return code.Length is > 0 and <= 64 &&
            System.Text.RegularExpressions.Regex.IsMatch(code,
                @"^(?:0x[0-9A-Fa-f]{1,16}|[A-Za-z][A-Za-z0-9_.-]{0,63})$")
            ? code : null;
    }

    private static async Task<IResult> GrantQuoteTotal(
        string code, Guid keyId, NpgsqlDataSource dbSource,
        WriteAccessService access, TemplateRepository templates, ClaimsPrincipal admin,
        HttpContext http, CancellationToken ct)
    {
        try
        {
            if (code != "principal" || keyId == Guid.Empty)
                throw new ReadRejected(400, "invalid_quote_total_grant_request");
            var detail = await access.ResolveAsync(code, "quotedetail", ct);
            var quote = await access.ResolveAsync(code, "quote", ct);
            var writable = new[]
            {
                "quoteid", "isproductoverridden", "ispriceoverridden",
                "productdescription", "quotedetailname", "quantity", "priceperunit"
            };
            foreach (var name in writable)
            {
                var field = detail.Table.Columns.FirstOrDefault(c => c.LogicalName == name);
                var permission = detail.Permissions.Fields.FirstOrDefault(p =>
                    p.EntityName == "quotedetail" && p.FieldName == name);
                if (field is null || permission is null || !permission.Enabled ||
                    !field.CanCreate || !permission.CanCreate || field.IsCalculated ||
                    permission.MetadataFingerprint != MetadataRepository.Fingerprint(field))
                    throw new ReadRejected(409, "quote_total_field_not_authorized");
            }
            var quoteIdColumn = quote.Table.Columns.FirstOrDefault(c =>
                c.LogicalName == quote.Table.PrimaryIdAttribute);
            var quoteIdGrant = quote.Permissions.Fields.FirstOrDefault(p =>
                p.EntityName == "quote" && p.FieldName == quote.Table.PrimaryIdAttribute);
            if (quoteIdColumn is null || !quoteIdColumn.CanRead ||
                quoteIdGrant is null || !quoteIdGrant.Enabled || !quoteIdGrant.CanRead ||
                quoteIdGrant.MetadataFingerprint != MetadataRepository.Fingerprint(quoteIdColumn))
                throw new ReadRejected(409, "quote_id_read_not_authorized");

            await using var db = await dbSource.OpenConnectionAsync(ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            Guid client;
            int quota;
            await using (var cmd = new NpgsqlCommand(
                """
                SELECT k.client_id,k.max_requests_per_minute
                  FROM dynamics.template_api_keys k
                  JOIN dynamics.integration_templates t ON t.id=k.template_id
                 WHERE k.id=@key AND k.connection_id=@connection AND k.enabled=true
                   AND k.expires_at>now() AND k.entity_name='quote'
                   AND k.operation='create' AND t.name='crear_oferta'
                   AND t.active_version=k.template_version
                 FOR UPDATE OF k
                """, db, tx))
            {
                cmd.Parameters.AddWithValue("key", keyId);
                cmd.Parameters.AddWithValue("connection", quote.Connection.Id);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    throw new ReadRejected(404, "active_quote_template_key_not_found");
                client = reader.GetGuid(0);
                quota = reader.GetInt32(1);
            }
            var template = await templates.FindAsync(quote.Connection.Id, "crear_oferta", ct);
            var definition = template?.History.FirstOrDefault(v =>
                v.Version == template.ActiveVersion)?.Definition;
            if (definition is null || definition.EntityName != "quote" ||
                definition.Operation != "create")
                throw new ReadRejected(409, "crud_template_inactive");

            // Only navigation properties explicitly selected by the published
            // template qualify for a narrow, read-only related-table grant.
            var targets = definition.Mappings
                .Where(m => m.Target.EndsWith("@odata.bind", StringComparison.Ordinal))
                .Select(m => quote.Table.Relationships.FirstOrDefault(r =>
                    r.Kind == "ManyToOneRelationships" &&
                    r.ReferencingNavigation == m.Target[..^"@odata.bind".Length] &&
                    r.ReferencingEntity == "quote"))
                .Where(r => r?.ReferencedEntity is not null)
                .Select(r => r!.ReferencedEntity!)
                .Distinct(StringComparer.Ordinal).ToArray();
            var grants = new List<(string Entity, string Operation, string[] Fields)>
            {
                ("quotedetail", "create", writable),
                ("quote", "read", new[] { quote.Table.PrimaryIdAttribute })
            };
            foreach (var target in targets)
            {
                var targetPolicy = await access.ResolveAsync(code, target, ct);
                var primary = targetPolicy.Table.PrimaryIdAttribute;
                var idColumn = targetPolicy.Table.Columns.FirstOrDefault(c => c.LogicalName == primary);
                var idPermission = targetPolicy.Permissions.Fields.FirstOrDefault(p =>
                    p.EntityName == target && p.FieldName == primary);
                if (idColumn is not { CanRead: true } || idPermission is null ||
                    !idPermission.Enabled || !idPermission.CanRead ||
                    idPermission.MetadataFingerprint != MetadataRepository.Fingerprint(idColumn))
                    throw new ReadRejected(409, "quote_lookup_target_not_authorized");
                // Only explicit name fields referenced in the active
                // template receive a read grant. A caller still needs
                // metadata approval and M2M read scope at execution time.
                var fields = new List<string> { primary };
                foreach (var mapping in definition.Mappings.Where(m =>
                m.Target.EndsWith("@odata.bind", StringComparison.Ordinal) &&
                m.Steps is { Count: > 0 } &&
                quote.Table.Relationships.Any(r =>
                    r.ReferencingNavigation == m.Target[..^"@odata.bind".Length] &&
                    r.ReferencedEntity == target)))
            {
                var relation = quote.Table.Relationships.First(r =>
                    r.ReferencingNavigation == mapping.Target[..^"@odata.bind".Length] &&
                    r.ReferencedEntity == target);
                foreach (var step in mapping.Steps!.Where(s => s.Op == "lookup_name" &&
                    !string.IsNullOrWhiteSpace(s.Argument)))
                {
                    var candidates = TemplateOrchestrator.ResolveLookupNameFields(
                        target, relation.ReferencingAttribute ?? "", step.Argument!);
                    var approved = candidates.Where(fieldName =>
                    {
                        var col = targetPolicy.Table.Columns.FirstOrDefault(c =>
                            c.LogicalName == fieldName && c.CanRead &&
                            c.Type is "String" or "Memo");
                        var grant = targetPolicy.Permissions.Fields.FirstOrDefault(p =>
                            p.EntityName == target && p.FieldName == fieldName &&
                            p.Enabled && p.CanRead);
                        return col is not null && grant is not null &&
                            grant.MetadataFingerprint == MetadataRepository.Fingerprint(col);
                    }).ToArray();
                    // Legacy IDTRA can be stored in incident.title OR
                    // ticketnumber. Require one authorized exact-match column,
                    // not both: a missing optional fallback must not block grants.
                    if (approved.Length == 0)
                        throw new ReadRejected(409, "quote_lookup_name_field_not_authorized",
                            target, candidates[0]);
                    fields.AddRange(approved);
                }
            }
                grants.Add((target, "read", fields.Distinct(StringComparer.Ordinal).ToArray()));
            }

            // Explicit one-off admin approval, scoped to the published template
            // and its known relationship targets. No arbitrary table is granted.
            foreach (var entry in grants)
            {
                await using var cmd = new NpgsqlCommand(
                    """
                    INSERT INTO dynamics.api_grants
                      (id,api_client_id,connection_id,entity_set,operation,
                       allowed_fields,max_requests_per_minute,enabled)
                    VALUES(@id,@client,@connection,@entity,@operation,@fields,@quota,true)
                    ON CONFLICT(api_client_id,connection_id,entity_set,operation)
                      DO UPDATE SET
                        allowed_fields=ARRAY(
                            SELECT DISTINCT unnest(dynamics.api_grants.allowed_fields ||
                                                   EXCLUDED.allowed_fields)),
                        max_requests_per_minute=GREATEST(
                            dynamics.api_grants.max_requests_per_minute,
                            EXCLUDED.max_requests_per_minute),
                        enabled=true
                    """, db, tx);
                cmd.Parameters.AddWithValue("id", Guid.NewGuid());
                cmd.Parameters.AddWithValue("client", client);
                cmd.Parameters.AddWithValue("connection", quote.Connection.Id);
                cmd.Parameters.AddWithValue("entity", entry.Entity);
                cmd.Parameters.AddWithValue("operation", entry.Operation);
                cmd.Parameters.AddWithValue("fields", entry.Fields);
                cmd.Parameters.AddWithValue("quota", quota);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var audit = new NpgsqlCommand(
                """
                INSERT INTO dynamics.audit_events
                  (id,connection_id,actor_id,action)
                VALUES(@id,@connection,@actor,'quote.total.grants.approved')
                """, db, tx))
            {
                audit.Parameters.AddWithValue("id", Guid.NewGuid());
                audit.Parameters.AddWithValue("connection", quote.Connection.Id);
                audit.Parameters.AddWithValue("actor", ScopeSecurity.ActorId(admin));
                await audit.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new
            {
                enabled = true, keyId,
                grantedEntities = grants.Select(g => g.Entity).Distinct(StringComparer.Ordinal).ToArray(),
                message = "Autorizadas lectura de Quote y creación de su línea de venta total."
            });
        }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http, ex.Status, ex.Code,
                "Quote total grant requires approved Dataverse metadata and active template key.");
        }
    }

    private static (int Revision, int RecoveryAttempt) ParsePricingKey(string key, Guid rateId)
    {
        var prefix = "dhole-pricing-quote-" + rateId.ToString("N") + "-v";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
            throw new ReadRejected(400, "pricing_quote_idempotency_key_invalid");
        var suffix = key[prefix.Length..];
        var match = System.Text.RegularExpressions.Regex.Match(
            suffix, @"^(?<revision>[1-9][0-9]{0,8})(?:-r(?<attempt>[1-3]))?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success ||
            !int.TryParse(match.Groups["revision"].Value, out var revision))
            throw new ReadRejected(400, "pricing_quote_idempotency_key_invalid");
        var attempt = match.Groups["attempt"].Success
            ? int.Parse(match.Groups["attempt"].Value,
                System.Globalization.CultureInfo.InvariantCulture) : 0;
        return (revision, attempt);
    }

    // Read the durable ledger across all past Pricing integration keys, not
    // only the currently configured key. This prevents a key rotation from
    // bypassing a prior ambiguous/succeeded header or duplicating a QUO.
    private static async Task<bool> PricingKeyUsedByAnotherClientAsync(
        NpgsqlDataSource database, Guid connectionId, string clientId,
        string prefix, CancellationToken ct)
    {
        await using var db = await database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM dynamics.write_operations
                WHERE connection_id=@connection AND issuer=@issuer
                  AND oauth_client_id<>@client
                  AND idempotency_key=ANY(@keys)
            )
            """, db);
        cmd.Parameters.AddWithValue("connection", connectionId);
        cmd.Parameters.AddWithValue("issuer", TemplateApiKeyRepository.Issuer);
        cmd.Parameters.AddWithValue("client", clientId);
        cmd.Parameters.AddWithValue("keys", new[] {
            prefix + "-header", prefix + "-total" });
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<bool> IsRecoveryEligibleAsync(
        NpgsqlDataSource source, Guid connectionId, string clientId,
        Guid rateId, int revision, int attempt, CancellationToken ct)
    {
        if (attempt is < 1 or > 3) return false;
        var baseKey = "dhole-pricing-quote-" + rateId.ToString("N") + "-v" +
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var expectedKeys = Enumerable.Range(0, attempt)
            .Select(i => baseKey + (i == 0 ? "" : "-r" + i)).ToArray();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT idempotency_key, entity_name, status, http_status, record_id
            FROM dynamics.write_operations
            WHERE connection_id = @connection AND issuer = @issuer
              AND idempotency_key = ANY(@keys)
            """, db);
        cmd.Parameters.AddWithValue("connection", connectionId);
        cmd.Parameters.AddWithValue("issuer", TemplateApiKeyRepository.Issuer);
        cmd.Parameters.AddWithValue("client", clientId);
        cmd.Parameters.AddWithValue("keys", expectedKeys.SelectMany(k =>
            new[] { k + "-header", k + "-total" }).ToArray());
        var rows = new Dictionary<string, (string Entity, string Status, int? Http, Guid? Record)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var k = reader.GetString(0);
            if (!rows.TryAdd(k, (
                reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4))))
                return false;
        }
        // Every preceding header must be a confirmed 4xx rejection, never
        // reserved, succeeded, reconciled, ambiguous or unknown. No total
        // write may have been dispatched. Old ledger rows remain immutable.
        return expectedKeys.All(k =>
            rows.TryGetValue(k + "-header", out var header) &&
            header.Entity == "quote" && header.Status == "rejected" &&
            header.Http is >= 400 and < 500 && header.Record is null &&
            !rows.ContainsKey(k + "-total"));
    }

    private static async Task<IResult> RecoveryEligibility(
        string connectionCode, Guid dholeRateId, int revisionNumber,
        int nextAttempt, TemplateApiKeyRepository keys, NpgsqlDataSource database,
        HttpContext http, CancellationToken ct)
    {
        try
        {
            if (connectionCode != "principal" || dholeRateId == Guid.Empty ||
                revisionNumber < 1 || nextAttempt is < 1 or > 3)
                throw new ReadRejected(400, "invalid_recovery_query");
            var key = await keys.ValidateAndChargeAsync(
                http.Request.Headers["X-Dhole-Api-Key"].ToString(),
                connectionCode, "crear_oferta", ct, chargeQuota: false);
            if (key.EntityName != "quote" || key.Operation != "create")
                throw new ReadRejected(403, "quote_creation_key_required");
            var eligible = await IsRecoveryEligibleAsync(
                database, key.ConnectionId, key.ClientId,
                dholeRateId, revisionNumber, nextAttempt, ct);
            return Results.Ok(new
            {
                eligible,
                code = eligible ? "confirmed_header_rejected" : "recovery_not_safe",
                nextAttempt, noPostExecuted = true
            });
        }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http, ex.Status, ex.Code,
                "No se pudo comprobar la seguridad de la recuperación.");
        }
    }

    private static string Revision(string key, Guid rateId)
    {
        var prefix = "dhole-pricing-quote-" + rateId.ToString("N") + "-v";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
            throw new ReadRejected(400, "pricing_quote_idempotency_key_invalid");
        var revision = key[prefix.Length..];
        if (revision.Length is < 1 or > 9 || !revision.All(char.IsAsciiDigit) ||
            !int.TryParse(revision, out var number) || number <= 0)
            throw new ReadRejected(400, "pricing_quote_idempotency_key_invalid");
        return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static Dictionary<string, JsonElement> BuildLine(
        Guid rateId, decimal totalSale, Guid quoteId, string relationship = "quoteid")
    {
        if (rateId == Guid.Empty || quoteId == Guid.Empty || totalSale < 0 ||
            !DataverseMetadataDiscovery.SafeName(relationship))
            throw new ArgumentException("Invalid Pricing quote line.");
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [relationship + "@odata.bind"] = JsonSerializer.SerializeToElement(quoteId.ToString("D")),
            ["isproductoverridden"] = JsonSerializer.SerializeToElement(true),
            ["ispriceoverridden"] = JsonSerializer.SerializeToElement(true),
            ["productdescription"] = JsonSerializer.SerializeToElement("Tarifa logística Dhole"),
            ["quotedetailname"] = JsonSerializer.SerializeToElement("Total venta de tarifa Dhole"),
            ["quantity"] = JsonSerializer.SerializeToElement(1m),
            ["priceperunit"] = JsonSerializer.SerializeToElement(totalSale),
        };
    }
}
