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
        var admin = app.MapGroup("/api/admin/v1/connections/{code}")
            .WithGroupName("admin-v1").RequireAuthorization("SecurityAdmin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        admin.MapPost("/quote-total-grants/{keyId:guid}", GrantQuoteTotal);
        return app;
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
        ProductionWritePolicy gate, HttpContext http, CancellationToken ct)
    {
        try
        {
            if (connectionCode != "principal")
                throw new ReadRejected(400, "invalid_pricing_connection");
            var key = await keys.ValidateAndChargeAsync(
                http.Request.Headers["X-Dhole-Api-Key"].ToString(),
                connectionCode, "crear_oferta", ct);
            if (key.Operation != "create" || key.EntityName != "quote")
                throw new ReadRejected(403, "quote_creation_key_required");
            var allowed = ProductionQuoteRouteAllowed(gate);
            return Results.Ok(new
            {
                ready = allowed,
                code = allowed ? "DYNAMICS_QUOTE_WRITES_READY" : "production_writes_not_approved",
                message = allowed
                    ? "La política de producción permite ejecutar la API de QUO."
                    : "El conector rechaza escrituras de QUO en producción; requiere autorización aprobada.",
                noPostExecuted = true
            });
        }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http, ex.Status, ex.Code,
                "No se pudo validar la preparación de la clave para QUO.");
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
                request.Inputs is null || request.Inputs.Count is < 1 or > 30)
                throw new ReadRejected(400, "invalid_pricing_quote_request");
            var rawKey = http.Request.Headers["Idempotency-Key"].ToString();
            if (!WriteLedger.ValidKey(rawKey) ||
                rawKey != "dhole-pricing-quote-" + request.DholeRateId.ToString("N") +
                    "-v" + Revision(rawKey, request.DholeRateId))
                throw new ReadRejected(400, "pricing_quote_idempotency_key_invalid");

            var key = await keys.ValidateAndChargeAsync(
                http.Request.Headers["X-Dhole-Api-Key"].ToString(),
                request.ConnectionCode, "crear_oferta", ct);
            if (key.EntityName != "quote" || key.Operation != "create")
                throw new ReadRejected(403, "quote_creation_key_required");
            gate.DemandPricingQuote();

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
                "crear_oferta", version, request.Inputs, ct, actor);
            await access.PermitAsync(actor, quote, "create", preview.Fields, ct);

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
                rawKey + "-header", ct);
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
                    "Pricing quote creation rejected by Dataverse connector policy.");
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
                var relationshipFields = definition.Mappings.Where(m =>
                    m.Target.EndsWith("@odata.bind", StringComparison.Ordinal) &&
                    m.Steps is { Count: > 0 } &&
                    m.Steps.Any(step => step.Op == "lookup_name" &&
                        !string.IsNullOrWhiteSpace(step.Argument)) &&
                    quote.Table.Relationships.Any(r =>
                        r.ReferencingNavigation == m.Target[..^"@odata.bind".Length] &&
                        r.ReferencedEntity == target))
                    .SelectMany(m => m.Steps!.Where(step => step.Op == "lookup_name")
                        .Select(step => step.Argument!))
                    .Distinct(StringComparer.Ordinal);
                foreach (var fieldName in relationshipFields)
                {
                    var col = targetPolicy.Table.Columns.FirstOrDefault(c =>
                        c.LogicalName == fieldName && c.CanRead &&
                        c.Type is "String" or "Memo");
                    var grant = targetPolicy.Permissions.Fields.FirstOrDefault(p =>
                        p.EntityName == target && p.FieldName == fieldName &&
                        p.Enabled && p.CanRead);
                    if (col is null || grant is null ||
                        grant.MetadataFingerprint != MetadataRepository.Fingerprint(col))
                        throw new ReadRejected(409, "quote_lookup_name_field_not_authorized");
                    fields.Add(fieldName);
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
