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
    // Explicit sales lines for the opt-in multi-line route. No arbitrary
    // Dataverse field names or navigation properties are accepted.
    public sealed record PricingQuoteLine(
        string Name, string? Description, decimal Quantity, decimal UnitPrice);

    public sealed record PricingQuoteTotalRequest(
        string ConnectionCode, Guid DholeRateId, decimal TotalSaleAmount,
        IReadOnlyDictionary<string, JsonElement> Inputs,
        decimal? TotalCostAmount = null,
        string? CurrencyCode = null, Guid? CurrencyId = null,
        IReadOnlyList<PricingQuoteLine>? Lines = null);

    public sealed record PricingQuoteTotalResponse(
        string State, Guid? RecordId, Guid? LineRecordId,
        string Stage, bool Replay, IReadOnlyList<Guid>? LineRecordIds = null);

    public static IEndpointRouteBuilder MapPricingQuoteTotalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/keyed/templates/crear_oferta/execute-with-total", Execute)
            .AllowAnonymous().WithGroupName("v1").RequireRateLimiting("AdminRate");
        // Opt-in: accepts explicit itemized sale lines instead of the legacy
        // single summary line. Existing Pricing callers remain unchanged.
        app.MapPost("/api/v1/keyed/templates/crear_oferta/execute-with-lines", Execute)
            .AllowAnonymous().WithGroupName("v1").RequireRateLimiting("AdminRate");
        // Read-only, key-protected check used by Pricing before an explicit
        // retry. It neither reserves write idempotency nor contacts Dataverse.
        app.MapGet("/api/v1/keyed/templates/crear_oferta/write-readiness", WriteReadiness)
            .AllowAnonymous().WithGroupName("v1").RequireRateLimiting("AdminRate");
        // Resolves a tenant ISO currency to its real Dataverse GUID (never a
        // hardcoded currency or the connection default). Read-only.
        app.MapGet("/api/v1/keyed/templates/crear_oferta/write-result", GetPricingWriteResult)
            .AllowAnonymous().WithGroupName("v1").RequireRateLimiting("AdminRate");
        app.MapGet("/api/v1/keyed/templates/crear_oferta/resolve-currency", ResolveQuoteCurrency)
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


    // Only reports confirmed succeeded writes owned by the CURRENT
    // integration client and matching the exact rate/revision. A 409 from
    // Pricing alone is never evidence that a Dataverse operation succeeded.
    private static async Task<IResult> GetPricingWriteResult(
        string connectionCode, Guid dholeRateId, int revisionNumber,
        TemplateApiKeyRepository keys, NpgsqlDataSource database,
        HttpContext http, CancellationToken ct)
    {
        try
        {
            if (connectionCode != "principal" || dholeRateId == Guid.Empty ||
                revisionNumber is < 1 or > 999999999)
                throw new ReadRejected(400, "quote_result_query_invalid");
            var key = await keys.ValidateAndChargeAsync(
                http.Request.Headers["X-Dhole-Api-Key"].ToString(),
                connectionCode, "crear_oferta", ct, chargeQuota: false);
            if (key.EntityName != "quote" || key.Operation is not ("create" or "upsert"))
                throw new ReadRejected(403, "quote_write_key_required");

            var prefix = "dhole-pricing-quote-" + dholeRateId.ToString("N") +
                "-v" + revisionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var suffixes = new[] { prefix + "-header", prefix + "-total",
                prefix + "-total-update" };
            var status = new Dictionary<string, (string State, Guid? RecordId)>(
                StringComparer.Ordinal);
            await using var db = await database.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                SELECT idempotency_key,entity_name,operation,status,record_id
                FROM dynamics.write_operations
                WHERE connection_id=@connection AND issuer=@issuer
                  AND oauth_client_id=@client
                  AND idempotency_key=ANY(@keys)
                """, db);
            cmd.Parameters.AddWithValue("connection", key.ConnectionId);
            cmd.Parameters.AddWithValue("issuer", TemplateApiKeyRepository.Issuer);
            cmd.Parameters.AddWithValue("client", key.ClientId);
            cmd.Parameters.AddWithValue("keys", suffixes);
            await using var rows = await cmd.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
            {
                var idem = rows.GetString(0);
                var expectedEntity = idem == suffixes[0] ? "quote" : "quotedetail";
                var expectedOperation = idem == suffixes[0]
                    ? key.Operation : idem == suffixes[1] ? "create" : "update";
                if (rows.GetString(1) != expectedEntity ||
                    rows.GetString(2) != expectedOperation ||
                    !status.TryAdd(idem, (rows.GetString(3),
                        rows.IsDBNull(4) ? null : rows.GetGuid(4))))
                    throw new ReadRejected(409, "quote_result_ledger_ambiguous");
            }
            var headerOk = status.TryGetValue(suffixes[0], out var header) &&
                header.State == "succeeded" && header.RecordId is Guid h && h != Guid.Empty;
            var detailKey = status.ContainsKey(suffixes[2]) ? suffixes[2] : suffixes[1];
            var totalOk = status.TryGetValue(detailKey, out var total) &&
                total.State == "succeeded" && total.RecordId is Guid t && t != Guid.Empty;
            return Results.Ok(new
            {
                confirmed = headerOk && totalOk,
                headerStatus = status.TryGetValue(suffixes[0], out var hs)
                    ? hs.State : "not_sent",
                totalStatus = status.TryGetValue(detailKey, out var ts)
                    ? ts.State : "not_sent",
                quoteId = headerOk ? header.RecordId : null,
                lineId = totalOk ? total.RecordId : null,
                noPostExecuted = true
            });
        }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http, ex.Status, ex.Code,
                "No se pudo comprobar la finalización de la QUO.");
        }
    }

    private static async Task<IResult> ResolveQuoteCurrency(
        string connectionCode, string currencyCode, TemplateApiKeyRepository keys,
        ReadSearchService reader, HttpContext http, CancellationToken ct)
    {
        try
        {
            if (connectionCode != "principal" || currencyCode is null ||
                currencyCode.Length != 3 || !currencyCode.All(c => c is >= 'A' and <= 'Z'))
                throw new ReadRejected(400, "quote_currency_code_invalid");
            var key = await keys.ValidateAndChargeAsync(
                http.Request.Headers["X-Dhole-Api-Key"].ToString(),
                connectionCode, "crear_oferta", ct, chargeQuota: false);
            if (key.EntityName != "quote" || key.Operation is not ("create" or "upsert"))
                throw new ReadRejected(403, "quote_write_key_required");
            var actor = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("connector:issuer", TemplateApiKeyRepository.Issuer),
                new Claim("client_id", key.ClientId),
                new Claim("scope", "dynamics.records.read"),
                new Claim("sub", key.Id.ToString("D"))
            ], "DholePricingKey"));
            var result = await reader.SearchAsync(actor, "transactioncurrency",
                new ReadSearchRequest(connectionCode, new ReadSpec(
                    ["transactioncurrencyid", "isocurrencycode"],
                    new ReadPredicate(Field: "isocurrencycode", Op: "eq",
                        Value: JsonSerializer.SerializeToElement(currencyCode)), PageSize: 2)),
                ct);
            if (result.Items.Count == 0)
                throw new ReadRejected(404, "quote_currency_not_found");
            if (result.Items.Count != 1 || result.NextCursor is not null)
                throw new ReadRejected(409, "quote_currency_ambiguous");
            var row = result.Items[0];
            if (!row.TryGetProperty("isocurrencycode", out var iso) ||
                iso.GetString() != currencyCode ||
                !row.TryGetProperty("transactioncurrencyid", out var value) ||
                !Guid.TryParse(value.GetString(), out var currencyId) ||
                currencyId == Guid.Empty)
                throw new ReadRejected(502, "quote_currency_invalid_record");
            return Results.Ok(new { currencyCode, currencyId, noPostExecuted = true });
        }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http, ex.Status, ex.Code,
                "La divisa indicada no se pudo resolver en Dynamics. No se creó ninguna QUO.");
        }
    }

    private static async Task<IResult> GetQuoteSyncLedger(
        string code, Guid rateId, int revision, NpgsqlDataSource data,
        ReadSearchService live, ClaimsPrincipal administrator, CancellationToken ct)
    {
        if (code != "principal" || rateId == Guid.Empty || revision is < 1 or > 999999999)
            return Results.BadRequest(new { code = "invalid_quote_ledger_query" });

        var prefix = "dhole-pricing-quote-" + rateId.ToString("N") + "-v" +
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var keys = new[] { prefix + "-header", prefix + "-total" };
        var entries = new Dictionary<string, object?>(StringComparer.Ordinal);
        Guid? headerId = null;
        Guid? detailId = null;
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
            if (status == "succeeded" && recordId is Guid validId &&
                validId != Guid.Empty)
            {
                if (isHeader) headerId = validId;
                else detailId = validId;
            }
        }
        // Live, read-only Dataverse verification. The write ledger proves
        // what the connector recorded, NOT that records still exist in CRM.
        // Do not infer absence from a 403/timeout or issue a replacement POST.
        async Task<(bool? Exists, string State)> VerifyAsync(
            string entity, Guid? id, string primary)
        {
            if (id is not Guid recordId || recordId == Guid.Empty)
                return (null, "no_record_id");
            try
            {
                await live.ByIdAsync(administrator, code, entity,
                    recordId, [primary], ct, admin: true);
                return (true, "exists");
            }
            catch (ReadRejected ex) when (ex.Status == 404)
            {
                return (false, "not_found");
            }
            catch (ReadRejected ex) when (ex.Status == 403)
            {
                return (null, "read_not_authorized");
            }
            catch (ReadRejected)
            {
                return (null, "verification_unavailable");
            }
            catch (HttpRequestException)
            {
                return (null, "verification_unavailable");
            }
        }
        // Dispose the journal reader before opening independent read-only CRM
        // requests. Reads are scoped to the two exact recorded identifiers.
        var liveHeader = await VerifyAsync("quote", headerId, "quoteid");
        var liveDetail = await VerifyAsync("quotedetail", detailId, "quotedetailid");
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
            live = new
            {
                headerExists = liveHeader.Exists,
                headerStatus = liveHeader.State,
                detailExists = liveDetail.Exists,
                detailStatus = liveDetail.State,
                confirmed = liveHeader.Exists == true && liveDetail.Exists == true
            },
            noPostExecuted = true,
            message = "Verificación de solo lectura del ledger y de los registros reales de Dynamics. No se creó ninguna QUO."
        });
    }

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
            if (key.Operation is not ("create" or "upsert") || key.EntityName != "quote")
                throw new ReadRejected(403, "quote_write_key_required");
            var allowed = ProductionQuoteRouteAllowed(gate);
            var grantStatus = allowed
                ? await InspectQuoteGrantsAsync(
                    key.Id, key.ConnectionId, connectionCode, key.Operation,
                    templates, access, database, ct)
                : new QuoteGrantStatus(false, ["production_writes_not_approved"]);
            var grantsReady = grantStatus.Ready;
            return Results.Ok(new
            {
                ready = allowed && grantsReady,
                code = !allowed ? "production_writes_not_approved" :
                    !grantsReady ? "quote_grants_incomplete" : "DYNAMICS_QUOTE_WRITES_READY",
                message = !allowed
                    ? "El conector rechaza escrituras de QUO en producción; requiere autorización aprobada."
                    : !grantsReady
                    ? "La clave activa de Pricing requiere permisos adicionales: " +
                      string.Join(", ", grantStatus.Missing.Take(12)) +
                      ". Autorizar vínculos y venta total usando esta misma clave."
                    : "Política y permisos de cabecera, vínculos y línea de venta verificados. No se creó ninguna QUO.",
                missingGrants = grantStatus.Missing,
                noPostExecuted = true
            });
        }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http, ex.Status, ex.Code,
                "No se pudo validar la preparación de la clave para QUO.");
        }
    }

    private sealed record QuoteGrantStatus(bool Ready, string[] Missing);

    /// <summary>
    /// Inspect precisely which grants the ACTIVE Pricing key is missing.
    /// Read-only, never writes to Dataverse or alters the grant configuration.
    /// Every configured relationship (including v13 IDTRA 1..9) must be
    /// authorized. Returns actionable field-level diagnostics rather than a
    /// misleading opaque quote_grants_incomplete result.
    /// </summary>
    private static async Task<QuoteGrantStatus> InspectQuoteGrantsAsync(
        Guid keyId, Guid connectionId, string code, string operation,
        TemplateRepository templates, WriteAccessService access,
        NpgsqlDataSource database, CancellationToken ct)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        WritePolicy? quote = null;
        try { quote = await access.ResolveAsync(code, "quote", ct); }
        catch (ReadRejected) { missing.Add("quote.metadata_access"); }

        var template = await templates.FindAsync(connectionId, "crear_oferta", ct);
        var definition = template?.History.FirstOrDefault(v =>
            v.Version == template.ActiveVersion)?.Definition;
        if (definition is null || definition.EntityName != "quote" ||
            definition.Operation != operation)
            missing.Add("crear_oferta.active_template");

        var grants = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using (var db = await database.OpenConnectionAsync(ct))
        await using (var command = new NpgsqlCommand("""
            SELECT g.entity_set,g.operation,g.allowed_fields
            FROM dynamics.api_grants g
            JOIN dynamics.template_api_keys k ON k.client_id=g.api_client_id
            WHERE k.id=@key AND g.connection_id=@connection AND g.enabled=true
            """, db))
        {
            command.Parameters.AddWithValue("key", keyId);
            command.Parameters.AddWithValue("connection", connectionId);
            await using var rows = await command.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
            {
                var grantKey = rows.GetString(0) + ":" + rows.GetString(1);
                if (!grants.TryGetValue(grantKey, out var allowed))
                {
                    allowed = new HashSet<string>(StringComparer.Ordinal);
                    grants[grantKey] = allowed;
                }
                allowed.UnionWith(rows.GetFieldValue<string[]>(2));
            }
        }
        void Check(string entity, string operation, IEnumerable<string> fields)
        {
            grants.TryGetValue(entity + ":" + operation, out var approved);
            foreach (var field in fields)
                if (approved is null || !approved.Contains(field))
                    missing.Add(entity + "." + operation + ":" + field);
        }
        Check("quote", operation,
            ["name", ..Enumerable.Range(1,9).Select(n => "new_monto" + n)]);
        if (quote is not null)
            Check("quote", "read", [quote.Table.PrimaryIdAttribute]);
        Check("quotedetail", "create",
            ["quoteid", "isproductoverridden", "ispriceoverridden",
             "productdescription", "quotedetailname", "quantity", "priceperunit"]);
        if (operation == "upsert")
        {
            Check("quotedetail", "read", ["quotedetailid", "quoteid", "quotedetailname"]);
            Check("quotedetail", "update",
                ["productdescription", "quotedetailname", "quantity", "priceperunit"]);
        }

        if (definition is not null && quote is not null)
        {
            // Pricing intentionally does not bind a Potential Customer or
            // supplementary account; neither relationship requires a grant.
            var effectiveDefinition = PricingQuoteTermOmission.FilterDefinition(definition);
            if (!effectiveDefinition.Mappings.Any(m => m.Target.StartsWith(
                    "transactioncurrencyid", StringComparison.OrdinalIgnoreCase) &&
                    m.Target.EndsWith("@odata.bind", StringComparison.Ordinal)))
                missing.Add("crear_oferta.currency_relationship");
            // The resolver explicitly reads both ISO code and its Dataverse ID.
            Check("transactioncurrency", "read",
                ["transactioncurrencyid", "isocurrencycode"]);
            foreach (var mapping in effectiveDefinition.Mappings.Where(m =>
                m.Target.EndsWith("@odata.bind", StringComparison.Ordinal)))
            {
                var navigation = mapping.Target[..^"@odata.bind".Length];
                var relation = quote.Table.Relationships.FirstOrDefault(r =>
                    r.Kind == "ManyToOneRelationships" &&
                    r.ReferencingNavigation == navigation &&
                    r.ReferencingEntity == "quote");
                if (relation?.ReferencedEntity is not string target)
                {
                    missing.Add("crear_oferta.relationship:" + navigation);
                    continue;
                }
                WritePolicy related;
                try { related = await access.ResolveAsync(code, target, ct); }
                catch (ReadRejected)
                {
                    missing.Add(target + ".metadata_access");
                    continue;
                }
                var primary = related.Table.PrimaryIdAttribute;
                Check(target, "read", [primary]);
                foreach (var step in mapping.Steps ?? [])
                {
                    if (step.Op != "lookup_name") continue;
                    if (string.IsNullOrWhiteSpace(step.Argument))
                    {
                        missing.Add("crear_oferta.lookup_field:" + navigation);
                        continue;
                    }
                    var candidates = TemplateOrchestrator.ResolveLookupNameFields(
                        target, relation.ReferencingAttribute ?? "", step.Argument);
                    var permitted = grants.TryGetValue(target + ":read", out var approved) &&
                        approved.Contains(primary) &&
                        candidates.Any(approved.Contains);
                    if (!permitted)
                        missing.Add(target + ".read:" + string.Join("|", candidates));
                }
            }
        }
        return new QuoteGrantStatus(missing.Count == 0,
            missing.Order(StringComparer.Ordinal).ToArray());
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
            if (key.EntityName != "quote" || key.Operation is not ("create" or "upsert"))
                throw new ReadRejected(403, "quote_write_key_required");
            var current = await templates.FindAsync(key.ConnectionId, "crear_oferta", ct);
            if (current?.ActiveVersion is not int version)
                throw new ReadRejected(409, "crud_template_inactive");
            var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("connector:issuer", TemplateApiKeyRepository.Issuer),
                new Claim("client_id", key.ClientId),
                new Claim("scope", "dynamics.records.create dynamics.records.upsert dynamics.records.read"),
                new Claim("sub", key.Id.ToString("D"))
            ], "DholePricingKey"));
            // PreviewAsync applies the live metadata, the saved per-field
            // grants, and exact-name lookups. It NEVER calls CreateAsync.
            var preview = await writer.PreviewAsync(request.ConnectionCode, "crear_oferta",
                version, request.Inputs, ct, actor,
                omitMissingQuoteCustomer: true, omitPricingQuoteTerms: true);
            var customerOmitted = !preview.Payload.Keys.Any(target =>
                target.StartsWith("customerid_", StringComparison.OrdinalIgnoreCase) &&
                target.EndsWith("@odata.bind", StringComparison.Ordinal));
            // Preview must enforce the same header write grant as the real
            // execution. A successful name lookup alone is not sufficient.
            var quotePolicy = await access.ResolveAsync(request.ConnectionCode, "quote", ct);
            await access.PermitAsync(actor, quotePolicy, key.Operation, preview.Fields, ct);
            return Results.Ok(new
            {
                valid = true,
                code = "DYNAMICS_LOOKUPS_VALID",
                schemaVersion = version,
                customerOmitted,
                noPostExecuted = true,
                message = "Se validaron campos y referencias restantes; no se requiere cliente potencial en el envío automático. No se creó ninguna QUO."
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
        GenericWriteService writer, ReadSearchService reader,
        ReadAccessRepository readAccess, DataverseWriteTransport transport,
        DataverseMetadataDiscovery discovery, ProductionWritePolicy gate,
        HttpContext http, CancellationToken ct)
    {
        var quoteConfirmedCreated = false;
        try
        {
            var itemized = http.Request.Path.Value?.EndsWith(
                "/execute-with-lines", StringComparison.Ordinal) == true;
            if (itemized)
            {
                if (request.Lines is not { Count: >= 1 and <= 50 } ||
                    request.Lines.Any(line => line is null ||
                        string.IsNullOrWhiteSpace(line.Name) ||
                        line.Name.Trim().Length > 100 ||
                        line.Description?.Length > 2000 ||
                        line.Quantity <= 0 || line.Quantity > 100000m ||
                        line.UnitPrice < 0 || line.UnitPrice > 999999999999m))
                    throw new ReadRejected(400, "quote_lines_invalid");
                var amount = request.Lines.Sum(line => line.Quantity * line.UnitPrice);
                if (Math.Abs(Math.Round(amount, 2, MidpointRounding.AwayFromZero) -
                             Math.Round(request.TotalSaleAmount, 2, MidpointRounding.AwayFromZero)) > 0.01m)
                    throw new ReadRejected(400, "quote_lines_total_mismatch");
            }
            else if (request.Lines is not null)
                throw new ReadRejected(400, "quote_lines_require_itemized_endpoint");
            if (request.ConnectionCode != "principal" ||
                request.DholeRateId == Guid.Empty ||
                request.TotalSaleAmount < 0 || request.TotalSaleAmount > 999999999999m ||
                request.TotalCostAmount is null ||
                request.TotalCostAmount < 0 || request.TotalCostAmount > 999999999999m ||
                request.Inputs is null || request.Inputs.Count is < 1 or > 64)
                throw new ReadRejected(400, "invalid_pricing_quote_request");
            var supplierCost = request.TotalCostAmount.Value;
            var rawKey = http.Request.Headers["Idempotency-Key"].ToString();
            if (!WriteLedger.ValidKey(rawKey))
                throw new ReadRejected(400, "pricing_quote_idempotency_key_invalid");
            var (revision, recoveryAttempt) = ParsePricingKey(rawKey, request.DholeRateId);

            var key = await keys.ValidateAndChargeAsync(
                http.Request.Headers["X-Dhole-Api-Key"].ToString(),
                request.ConnectionCode, "crear_oferta", ct);
            if (key.EntityName != "quote" || key.Operation is not ("create" or "upsert"))
                throw new ReadRejected(403, "quote_write_key_required");
            if (itemized && key.Operation != "create")
                throw new ReadRejected(403, "itemized_quote_requires_create");
            var isUpsert = key.Operation == "upsert";
            if (isUpsert && (key.KeyFields.Count != 1 ||
                key.KeyFields[0] is not ("cr933_dholerateid" or "new_idtraasignado")))
                throw new ReadRejected(409, "pricing_quote_rate_key_required");
            gate.Demand(key.Operation);
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

            // A single rate revision must never mix the old summary line
            // with the new itemized lines, or change item count on replay.
            await EnsureQuoteLineModeAsync(
                http.RequestServices.GetRequiredService<NpgsqlDataSource>(),
                key.ConnectionId, key.ClientId, rawKey,
                itemized ? request.Lines!.Count : 0, ct);

            var claims = new[]
            {
                new Claim("connector:issuer", TemplateApiKeyRepository.Issuer),
                new Claim("client_id", key.ClientId),
                new Claim("scope", isUpsert
                    ? "dynamics.records.upsert dynamics.records.create dynamics.records.update dynamics.records.read"
                    : "dynamics.records.create dynamics.records.read"),
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
                definition.Operation != key.Operation)
                throw new ReadRejected(409, "crud_template_scope_denied");
            var alternateKeys = isUpsert
                ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    { [key.KeyFields[0]] = JsonSerializer.SerializeToElement(
                        request.DholeRateId.ToString("D")) }
                : null;
            if (isUpsert && !(quote.Table.AlternateKeys ?? []).Any(candidate =>
                candidate.Attributes.Count == 1 &&
                candidate.Attributes[0] == key.KeyFields[0]))
                throw new ReadRejected(409, "pricing_quote_alternate_key_inactive");
            // Never permit a provided IDTRA to become the Quote identity.
            var trustedInputs = new Dictionary<string, JsonElement>(request.Inputs,
                StringComparer.Ordinal);
            if (isUpsert && definition.Inputs.Any(input =>
                input.Name == key.KeyFields[0]))
                trustedInputs[key.KeyFields[0]] = alternateKeys![key.KeyFields[0]];

            // Check the real tenant's MaxLength before any Quote POST. The
            // prior Dataverse 0x80044331 was StringLengthTooLong, and the
            // generic 4,000-character compiler bound is NOT each field's real
            // limit. Only the generated description may be shortened, with an
            // explicit Dhole reference. Identity and commercial terms fail
            // closed with their field name instead of being silently cut.
            var tenantTextLimits = await discovery.DiscoverTextMaxLengthsAsync(
                quote.Connection, "quote", ct);
            var preview = await templatesWriter.PreviewAsync(request.ConnectionCode,
                "crear_oferta", version, trustedInputs, ct, actor,
                omitMissingQuoteCustomer: true, omitPricingQuoteTerms: true);
            var safeInputs = PricingQuoteTextLimits.Prepare(
                definition, trustedInputs, preview.Payload, tenantTextLimits);
            if (!ReferenceEquals(safeInputs, trustedInputs))
                preview = await templatesWriter.PreviewAsync(request.ConnectionCode,
                    "crear_oferta", version, safeInputs, ct, actor,
                    omitMissingQuoteCustomer: true, omitPricingQuoteTerms: true);
            PricingQuoteTextLimits.Check(preview.Payload, tenantTextLimits);
            await access.PermitAsync(actor, quote, key.Operation, preview.Fields, ct);
            // All nine inputs feed Dynamics' legacy utility calculation.
            // Validate metadata AND API key grants before sending the header.
            var costFields = Enumerable.Range(1, 9)
                .Select(n => "new_monto" + n).ToArray();
            foreach (var name in costFields)
            {
                var column = quote.Table.Columns.FirstOrDefault(c =>
                    c.LogicalName == name);
                var permission = quote.Permissions.Fields.FirstOrDefault(p =>
                    p.EntityName == "quote" && p.FieldName == name);
                if (column is null || !column.CanCreate ||
                    isUpsert && !column.CanUpdate || column.IsCalculated ||
                    permission is null || !permission.Enabled || !permission.CanCreate ||
                    isUpsert && !permission.CanUpdate ||
                    permission.MetadataFingerprint != MetadataRepository.Fingerprint(column))
                    throw new ReadRejected(409, "quote_cost_fields_not_authorized", name);
            }
            await access.PermitAsync(actor, quote, key.Operation,
                isUpsert ? costFields.Concat(key.KeyFields).ToArray() : costFields, ct);
            // Pricing Quotes omit Potential Customer even when the published
            // CRUD template still requires it. Dataverse API does not enforce
            // ApplicationRequired by itself. Any tenant-specific server-side
            // validation rejection is propagated and never silently bypassed.

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
            if (itemized)
            {
                // Validate the connected tenant's real text limits BEFORE the
                // header POST; an overlong line must not leave an orphan Quote.
                var detailTextLimits = await discovery.DiscoverTextMaxLengthsAsync(
                    detail.Connection, "quotedetail", ct);
                if (!detailTextLimits.TryGetValue("quotedetailname", out var titleMax) ||
                    !detailTextLimits.TryGetValue("productdescription", out var descriptionMax))
                    throw new ReadRejected(409, "quote_detail_text_metadata_missing");
                foreach (var item in request.Lines!)
                {
                    var description = string.IsNullOrWhiteSpace(item.Description)
                        ? item.Name.Trim() : item.Description.Trim();
                    if (item.Name.Trim().Length > titleMax ||
                        description.Length > descriptionMax)
                        throw new ReadRejected(400, "quote_detail_text_too_long");
                }
            }

            var validationQuoteId = Guid.NewGuid();
            var validationLines = itemized
                ? request.Lines!.Select(line => BuildItemizedLine(line,
                    validationQuoteId, relation.ReferencingNavigation!)).ToArray()
                : new[] { BuildLine(request.DholeRateId, request.TotalSaleAmount,
                    validationQuoteId, relation.ReferencingNavigation!) };
            foreach (var validationLine in validationLines)
            {
                var compiled = await WriteValueCompiler.CompileAsync(detail, validationLine,
                    (rel, id, token) => access.EnsureLookupAsync(actor, detail, rel, id, token), ct);
                await access.PermitAsync(actor, detail, "create", compiled.Fields, ct);
            }

            // Preflight quote-detail read/update grants BEFORE touching the
            // header so unauthorized updates fail without partial changes.
            if (isUpsert)
            {
                var detailRead = await readAccess.ResolveAsync(
                    request.ConnectionCode, "quotedetail", ct);
                await readAccess.PermitAsync(actor, detailRead,
                    ["quotedetailid", "quotedetailname", "quoteid"], ct);
                var updateCompiled = await WriteValueCompiler.CompileAsync(detail,
                    BuildLineUpdate(request.TotalSaleAmount), (rel, id, token) =>
                        access.EnsureLookupAsync(actor, detail, rel, id, token), ct);
                await access.PermitAsync(actor, detail, "update", updateCompiled.Fields, ct);
                var quoteRead = await readAccess.ResolveAsync(
                    request.ConnectionCode, "quote", ct);
                await readAccess.PermitAsync(actor, quoteRead,
                    [quote.Table.PrimaryIdAttribute, key.KeyFields[0]], ct);
            }
            if (isUpsert)
            {
                // A historical CREATE quote may not have the RateId alternate
                // key populated. If it exists in the ledger but is not found
                // by the registered key, creating another would duplicate it.
                var foundByKey = false;
                try
                {
                    var existingQuote = await reader.ByKeyAsync(actor,
                        request.ConnectionCode, "quote", alternateKeys!,
                        [quote.Table.PrimaryIdAttribute], ct);
                    foundByKey = existingQuote.Item.TryGetProperty(
                        quote.Table.PrimaryIdAttribute, out var known) &&
                        known.ValueKind == JsonValueKind.String &&
                        Guid.TryParse(known.GetString(), out var resolved) &&
                        resolved != Guid.Empty;
                }
                catch (ReadRejected ex) when (ex.Status == 404) { }
                if (!foundByKey && await HasLegacyCreatedQuoteAsync(
                    http.RequestServices.GetRequiredService<NpgsqlDataSource>(),
                    key.ConnectionId, request.DholeRateId, ct))
                    throw new ReadRejected(409, "legacy_quote_requires_rate_key_linking");
            }

            var header = await templatesWriter.ExecuteAsync(actor, "crear_oferta",
                new TemplateInputs(request.ConnectionCode, safeInputs, Keys: alternateKeys),
                rawKey + "-header", ct, omitMissingQuoteCustomer: true,
                trustedPricingSupplierCost: supplierCost, omitPricingQuoteTerms: true,
                trustedPricingTotalSale: request.TotalSaleAmount);
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
            if (header is not WriteResult created || created.State != "succeeded")
                return Results.Json(new PricingQuoteTotalResponse("requires_reconciliation",
                    null, null, "quote", false), statusCode: 409);
            Guid quoteId;
            if (created.RecordId is Guid returned && returned != Guid.Empty)
                quoteId = returned;
            else if (isUpsert)
            {
                // Dataverse PATCH can succeed without returning quoteid.
                // Only a read against the registered alternate key can
                // resolve it; a second header write is never issued.
                var resolved = await reader.ByKeyAsync(actor, request.ConnectionCode,
                    "quote", alternateKeys!, [quote.Table.PrimaryIdAttribute], ct);
                if (!resolved.Item.TryGetProperty(quote.Table.PrimaryIdAttribute, out var id) ||
                    id.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(id.GetString(), out quoteId) || quoteId == Guid.Empty)
                    throw new ReadRejected(409, "quote_upsert_id_unresolved");
            }
            else
                return Results.Json(new PricingQuoteTotalResponse("requires_reconciliation",
                    null, null, "quote", created.Replay), statusCode: 409);

            quoteConfirmedCreated = true;
            if (isUpsert)
            {
                var existing = await transport.ReadQuoteDetailsAsync(detail, quoteId, ct);
                if (existing.Count > 1 ||
                    existing.Count == 1 &&
                    existing[0].Name != "Total venta de tarifa Dhole")
                    throw new ReadRejected(409, "quote_line_unmanaged_requires_reconciliation");
                var values = existing.Count == 0
                    ? BuildLine(request.DholeRateId, request.TotalSaleAmount, quoteId,
                        relation.ReferencingNavigation!)
                    : BuildLineUpdate(request.TotalSaleAmount);
                var line = existing.Count == 0
                    ? await writer.CreateWithResolvedMetadataAsync(actor, detail,
                        new GenericCreateRequest(request.ConnectionCode, values),
                        rawKey + "-total", ct)
                    : await writer.PatchAsync(actor, "quotedetail", existing[0].RecordId,
                        new GenericPatchRequest(request.ConnectionCode, values),
                        rawKey + "-total-update", existing[0].ETag, ct);
                var lineId = line.RecordId ?? (existing.Count == 1
                    ? existing[0].RecordId : Guid.Empty);
                if (line.State != "succeeded" || lineId == Guid.Empty)
                    return Results.Json(new PricingQuoteTotalResponse(
                        "requires_reconciliation", quoteId, null, "total",
                        created.Replay), statusCode: 409);
                return Results.Ok(new PricingQuoteTotalResponse(
                    "succeeded", quoteId, lineId, "completed",
                    created.Replay && line.Replay, [lineId]));
            }
            // A separate ledger key per detail ensures replays cannot create
            // duplicated lines. No caller may pass arbitrary quotedetail fields.
            var lineIds = new List<Guid>();
            var allReplay = created.Replay;
            for (var index = 0; index < validationLines.Length; index++)
            {
                var values = itemized
                    ? BuildItemizedLine(request.Lines![index], quoteId,
                        relation.ReferencingNavigation!)
                    : BuildLine(request.DholeRateId, request.TotalSaleAmount, quoteId,
                        relation.ReferencingNavigation!);
                var suffix = itemized
                    ? "-line-" + (index + 1).ToString("D3",
                        System.Globalization.CultureInfo.InvariantCulture) +
                        "-of-" + validationLines.Length.ToString("D3",
                            System.Globalization.CultureInfo.InvariantCulture)
                    : "-total";
                var result = await writer.CreateWithResolvedMetadataAsync(actor, detail,
                    new GenericCreateRequest(request.ConnectionCode, values),
                    rawKey + suffix, ct);
                if (result.State != "succeeded" || result.RecordId is not Guid lineId ||
                    lineId == Guid.Empty)
                    return Results.Json(new PricingQuoteTotalResponse(
                        "requires_reconciliation", quoteId,
                        lineIds.Count == 0 ? null : lineIds[0],
                        itemized ? "line-" + (index + 1) : "total", created.Replay, lineIds),
                        statusCode: 409);
                lineIds.Add(lineId);
                allReplay &= result.Replay;
            }

            return Results.Ok(new PricingQuoteTotalResponse("succeeded",
                quoteId, lineIds.Count == 0 ? null : lineIds[0],
                "completed", allReplay, lineIds));
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

    internal static async Task<IResult> GrantQuoteTotal(
        string code, Guid keyId, NpgsqlDataSource dbSource,
        WriteAccessService access, TemplateRepository templates,
        MetadataRepository catalog, MetadataRefreshService metadataRefresh,
        ClaimsPrincipal admin, HttpContext http, CancellationToken ct)
    {
        try
        {
            if (code != "principal" || keyId == Guid.Empty)
                throw new ReadRejected(400, "invalid_quote_total_grant_request");
            // One-time *authenticated administrator* approval of the exact
            // fixed Pricing QUO fields. This is not performed by keyed workers.
            // Later key rotations reuse these durable metadata approvals.
            var quoteBeforeApproval = await access.ResolveAsync(code, "quote", ct);
            var connectionId = quoteBeforeApproval.Connection.Id;
            // SecurityAdmin approves only the active CREATE/UPSERT key.
            // Determine its operation before modifying metadata permissions.
            string grantedOperation;
            string[] grantedKeyFields;
            await using (var lookupDb = await dbSource.OpenConnectionAsync(ct))
            await using (var lookup = new NpgsqlCommand("""
                SELECT k.operation,k.key_fields FROM dynamics.template_api_keys k
                JOIN dynamics.integration_templates t ON t.id=k.template_id
                WHERE k.id=@key AND k.connection_id=@connection AND k.enabled=true
                  AND k.expires_at>now() AND k.entity_name='quote'
                  AND k.operation IN ('create','upsert') AND t.name='crear_oferta'
                  AND t.active_version=k.template_version
                """, lookupDb))
            {
                lookup.Parameters.AddWithValue("key", keyId);
                lookup.Parameters.AddWithValue("connection", connectionId);
                await using var keyRow = await lookup.ExecuteReaderAsync(ct);
                if (!await keyRow.ReadAsync(ct))
                    throw new ReadRejected(404, "active_quote_template_key_not_found");
                grantedOperation = keyRow.GetString(0);
                grantedKeyFields = keyRow.GetFieldValue<string[]>(1);
            }
            var isUpsert = grantedOperation == "upsert";
            if (isUpsert && (grantedKeyFields.Length != 1 ||
                grantedKeyFields[0] is not ("cr933_dholerateid" or "new_idtraasignado")))
                throw new ReadRejected(409, "pricing_quote_rate_key_required");
            var snapshot = await catalog.LatestAsync(connectionId, ct)
                ?? throw new ReadRejected(409, "metadata_missing");
            if (!snapshot.Catalog.Tables.Any(t => t.LogicalName == "transactioncurrency"))
            {
                await metadataRefresh.RefreshAsync(code, "manual",
                    ScopeSecurity.ActorId(admin), ct);
                snapshot = await catalog.LatestAsync(connectionId, ct)
                    ?? throw new ReadRejected(409, "metadata_missing");
            }
            if (!snapshot.Catalog.Tables.Any(t => t.LogicalName == "transactioncurrency"))
                throw new ReadRejected(409, "quote_currency_catalog_missing");
            var currencyTable = snapshot.Catalog.Tables.First(t =>
                t.LogicalName == "transactioncurrency");
            if (!currencyTable.Columns.Any(c => c.LogicalName == "isocurrencycode") ||
                !currencyTable.Columns.Any(c => c.LogicalName == "transactioncurrencyid"))
            {
                await metadataRefresh.RefreshTableAsync(code, "transactioncurrency",
                    ScopeSecurity.ActorId(admin), ct);
                snapshot = await catalog.LatestAsync(connectionId, ct)
                    ?? throw new ReadRejected(409, "metadata_missing");
            }
            var actorId = ScopeSecurity.ActorId(admin);
            var policies = await catalog.PoliciesAsync(connectionId, ct);
            if (!policies.Entities.Any(e =>
                e.LogicalName == "transactioncurrency" && e.Enabled) &&
                !await catalog.SetEntityAsync(connectionId, "transactioncurrency",
                    true, actorId, ct))
                throw new ReadRejected(409, "quote_currency_table_approval_failed");
            var approval = new (string Entity, string[] Fields,
                bool Create, bool Update)[]
            {
                ("quote", Enumerable.Range(1, 9)
                    .Select(index => "new_monto" + index).ToArray(), true, isUpsert),
                ("quote", [quoteBeforeApproval.Table.PrimaryIdAttribute], false, false),
                ("quote", isUpsert ? grantedKeyFields : [], false, false),
                ("transactioncurrency",
                    ["transactioncurrencyid", "isocurrencycode"], false, false),
                // Read and update only the one automatic total line on UPSERT.
                ("quotedetail", ["quotedetailid", "quoteid", "quotedetailname"],
                    false, false),
                ("quotedetail", ["productdescription", "quantity", "priceperunit"],
                    true, isUpsert),
                ("quotedetail", ["quotedetailname"], true, isUpsert)
            };
            foreach (var group in approval)
            {
                var table = snapshot.Catalog.Tables.FirstOrDefault(t =>
                    t.LogicalName == group.Entity)
                    ?? throw new ReadRejected(409, "quote_metadata_entity_missing");
                foreach (var name in group.Fields)
                {
                    var column = table.Columns.FirstOrDefault(c => c.LogicalName == name);
                    if (column is null || column.IsCalculated ||
                        group.Create && !column.CanCreate ||
                        group.Update && !column.CanUpdate ||
                        !group.Create && !column.CanRead)
                        throw new ReadRejected(409, "quote_metadata_field_not_supported",
                            group.Entity, name);
                    var existing = policies.Fields.FirstOrDefault(p =>
                        p.EntityName == group.Entity && p.FieldName == name);
                    if (existing is { Enabled: true } &&
                        existing.MetadataFingerprint == MetadataRepository.Fingerprint(column) &&
                        (group.Create ? existing.CanCreate : existing.CanRead) &&
                        (!group.Update || existing.CanUpdate))
                        continue;
                    if (!await catalog.SetFieldAsync(connectionId, group.Entity,
                        name, new MetadataFieldToggle(
                            true, column.CanRead,
                            column.CanCreate && (group.Create || existing?.CanCreate == true),
                            column.CanUpdate && (group.Update || existing?.CanUpdate == true)),
                        actorId, ct))
                        throw new ReadRejected(409, "quote_metadata_field_approval_failed",
                            group.Entity, name);
                }
            }
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
                    isUpsert && (new[] { "productdescription", "quotedetailname",
                        "quantity", "priceperunit" }.Contains(name) &&
                        (!field.CanUpdate || !permission.CanUpdate)) ||
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

            var amountNames = Enumerable.Range(1, 9).Select(n => "new_monto" + n).ToArray();
            foreach (var name in amountNames)
            {
                var field = quote.Table.Columns.FirstOrDefault(c => c.LogicalName == name);
                var permission = quote.Permissions.Fields.FirstOrDefault(p =>
                    p.EntityName == "quote" && p.FieldName == name);
                if (field is null || !field.CanCreate ||
                    isUpsert && !field.CanUpdate || field.IsCalculated ||
                    permission is null || !permission.Enabled || !permission.CanCreate ||
                    isUpsert && !permission.CanUpdate ||
                    permission.MetadataFingerprint != MetadataRepository.Fingerprint(field))
                    throw new ReadRejected(409, "quote_cost_fields_not_authorized", name);
            }
            var isoPolicy = await access.ResolveAsync(code, "transactioncurrency", ct);
            foreach (var name in new[] { "transactioncurrencyid", "isocurrencycode" })
            {
                var field = isoPolicy.Table.Columns.FirstOrDefault(c => c.LogicalName == name);
                var permission = isoPolicy.Permissions.Fields.FirstOrDefault(p =>
                    p.EntityName == "transactioncurrency" && p.FieldName == name);
                if (field is null || !field.CanRead || permission is null ||
                    !permission.Enabled || !permission.CanRead ||
                    permission.MetadataFingerprint != MetadataRepository.Fingerprint(field))
                    throw new ReadRejected(409, "quote_currency_iso_field_not_authorized", name);
            }
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
                   AND k.operation=@operation AND t.name='crear_oferta'
                   AND t.active_version=k.template_version
                 FOR UPDATE OF k
                """, db, tx))
            {
                cmd.Parameters.AddWithValue("key", keyId);
                cmd.Parameters.AddWithValue("operation", grantedOperation);
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
                definition.Operation != grantedOperation)
                throw new ReadRejected(409, "crud_template_inactive");

            // Only relationships actually sent by this Pricing route need
            // grants; customerid and msdyn_account are deliberately omitted.
            var effectiveDefinition = PricingQuoteTermOmission.FilterDefinition(definition);
            var targets = effectiveDefinition.Mappings
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
                ("quote", "read", isUpsert
                    ? [quote.Table.PrimaryIdAttribute, ..grantedKeyFields]
                    : [quote.Table.PrimaryIdAttribute]),
                ("quote", grantedOperation, amountNames),
                ("transactioncurrency", "read",
                    ["transactioncurrencyid", "isocurrencycode"])
            };
            if (isUpsert)
            {
                grants.Add(("quotedetail", "read",
                    ["quotedetailid", "quoteid", "quotedetailname"]));
                grants.Add(("quotedetail", "update",
                    ["productdescription", "quotedetailname", "quantity", "priceperunit"]));
            }
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
                foreach (var mapping in effectiveDefinition.Mappings.Where(m =>
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
    private static async Task<bool> HasLegacyCreatedQuoteAsync(
        NpgsqlDataSource source, Guid connectionId, Guid rateId, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS(
                SELECT 1 FROM dynamics.write_operations
                WHERE connection_id=@connection
                  AND issuer=@issuer AND entity_name='quote'
                  AND operation='create' AND status='succeeded'
                  AND idempotency_key LIKE @prefix
            )
            """, db);
        cmd.Parameters.AddWithValue("connection", connectionId);
        cmd.Parameters.AddWithValue("issuer", TemplateApiKeyRepository.Issuer);
        cmd.Parameters.AddWithValue("prefix",
            "dhole-pricing-quote-" + rateId.ToString("N") + "-v%-header");
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    // If a previous request already reserved any line, its shape is immutable.
    // This protects against silently adding lines on a replay with the same
    // header key or mixing an itemized payload with Pricing's summary line.
    private static async Task EnsureQuoteLineModeAsync(
        NpgsqlDataSource source, Guid connection, string client,
        string baseKey, int lineCount, CancellationToken ct)
    {
        var expected = Enumerable.Range(1, lineCount)
            .Select(index => baseKey + "-line-" +
                index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) +
                "-of-" + lineCount.ToString("D3",
                    System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT idempotency_key
            FROM dynamics.write_operations
            WHERE connection_id=@connection AND issuer=@issuer
              AND oauth_client_id=@client
              AND (idempotency_key=@summary OR
                   left(idempotency_key,length(@linePrefix))=@linePrefix)
            """, db);
        cmd.Parameters.AddWithValue("connection", connection);
        cmd.Parameters.AddWithValue("issuer", TemplateApiKeyRepository.Issuer);
        cmd.Parameters.AddWithValue("client", client);
        cmd.Parameters.AddWithValue("summary", baseKey + "-total");
        cmd.Parameters.AddWithValue("linePrefix", baseKey + "-line-");
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            var existing = rows.GetString(0);
            if (lineCount == 0 && existing != baseKey + "-total" ||
                lineCount > 0 && !expected.Contains(existing))
                throw new ReadRejected(409, "quote_line_mode_changed_requires_reconciliation");
        }
    }

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

    /// <summary>
    /// Single authoritative calculation of Quote amounts, for both future
    /// CREATE and UPSERT paths. Dataverse new_monto1 is supplier cost;
    /// monto2..9 are always zero; new_flete is the customer SALE.
    /// These trusted values override any caller/template-supplied amounts.
    /// Standard totalamount remains calculated from the quotedetail line.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> WithExactQuoteAmounts(
        IReadOnlyDictionary<string, JsonElement> source,
        decimal totalSupplierCost, decimal totalSale)
    {
        if (totalSupplierCost < 0 || totalSale < 0 ||
            totalSupplierCost > 999999999999m || totalSale > 999999999999m)
            throw new ArgumentOutOfRangeException(nameof(totalSupplierCost),
                "Quote sale and supplier cost must be finite and nonnegative.");
        var values = source.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal);
        values["new_flete"] = JsonSerializer.SerializeToElement(totalSale);
        for (var i = 1; i <= 9; i++)
            values["new_monto" + i] = JsonSerializer.SerializeToElement(
                i == 1 ? totalSupplierCost : 0m);
        return values;
    }

    // The connector owns both the target field set and the resolved relationship.
    // Dataverse calculates totalamount; never accept it as a direct write.
    // The legacy Pricing sales line is managed automatically; an existing
    // line only updates its commercial values, never its parent relationship.
    public static Dictionary<string, JsonElement> BuildLineUpdate(decimal totalSale)
    {
        if (totalSale < 0 || totalSale > 999999999999m)
            throw new ArgumentOutOfRangeException(nameof(totalSale));
        return new(StringComparer.Ordinal)
        {
            ["productdescription"] = JsonSerializer.SerializeToElement("Tarifa logística Dhole"),
            ["quotedetailname"] = JsonSerializer.SerializeToElement("Total venta de tarifa Dhole"),
            ["quantity"] = JsonSerializer.SerializeToElement(1m),
            ["priceperunit"] = JsonSerializer.SerializeToElement(totalSale)
        };
    }

    public static Dictionary<string, JsonElement> BuildItemizedLine(
        PricingQuoteLine line, Guid quoteId, string relationship = "quoteid")
    {
        if (quoteId == Guid.Empty || !DataverseMetadataDiscovery.SafeName(relationship) ||
            string.IsNullOrWhiteSpace(line.Name) || line.Name.Trim().Length > 100 ||
            line.Description?.Length > 2000 ||
            line.Quantity <= 0 || line.Quantity > 100000m ||
            line.UnitPrice < 0 || line.UnitPrice > 999999999999m)
            throw new ArgumentException("Invalid quote detail line.", nameof(line));
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [relationship + "@odata.bind"] = JsonSerializer.SerializeToElement(quoteId.ToString("D")),
            ["isproductoverridden"] = JsonSerializer.SerializeToElement(true),
            ["ispriceoverridden"] = JsonSerializer.SerializeToElement(true),
            ["productdescription"] = JsonSerializer.SerializeToElement(
                string.IsNullOrWhiteSpace(line.Description) ? line.Name.Trim() : line.Description.Trim()),
            ["quotedetailname"] = JsonSerializer.SerializeToElement(line.Name.Trim()),
            ["quantity"] = JsonSerializer.SerializeToElement(line.Quantity),
            ["priceperunit"] = JsonSerializer.SerializeToElement(line.UnitPrice)
        };
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
