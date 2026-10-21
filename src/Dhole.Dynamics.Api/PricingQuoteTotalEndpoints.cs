using System.Security.Claims;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

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
        return app;
    }

    private static async Task<IResult> Execute(PricingQuoteTotalRequest request,
        TemplateApiKeyRepository keys, TemplateRepository templates,
        WriteAccessService access, TemplateOrchestrator templatesWriter,
        GenericWriteService writer, DataverseMetadataDiscovery discovery,
        ProductionWritePolicy gate,
        HttpContext http, CancellationToken ct)
    {
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
            gate.Demand("create");

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
                "crear_oferta", version, request.Inputs, ct);
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
            return ApiProblems.Create(http, ex.Status, ex.Code,
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
