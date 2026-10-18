using System.Security.Claims;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Dhole is an ordinary M2M consumer: no dependency on Pricing, DholeAuth, or DholeApiGateway.
/// An approved versioned template handles all Dataverse columns and Choice values.
/// </summary>
public static class DholeQuoteTemplateEndpoints
{
    public static IEndpointRouteBuilder MapDholeQuoteTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var route = app.MapGroup("/api/v1/dhole/quotes").WithGroupName("v1")
            .RequireAuthorization("M2M");
        route.MapPost("", Submit);
        route.MapGet("/links/{rateId:guid}", Find);

        var admin=app.MapGroup("/api/admin/v1/connections/{code}/dhole/quotes")
            .WithGroupName("admin-v1").RequireAuthorization("SecurityAdmin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        admin.MapPost("/{rateId:guid}/reconcile", Reconcile);
        return app;
    }

    private static async Task<IResult> Submit(DholeQuoteTemplateRequest request,
        TemplateOrchestrator orchestrator, TemplateRepository templates,
        WriteAccessService writeAccess, IQuoteRepository quotes,
        ExternalReferenceRepository references, ClaimsPrincipal actor,
        HttpContext http, CancellationToken ct) =>
        await Guard(http, async () =>
        {
            if (!request.Confirmed || request.DholeRateId == Guid.Empty ||
                request.Inputs is null || request.Inputs.Count is < 3 or > 30)
                throw new ReadRejected(400, "quote_confirmation_required");

            // Stable key per rate across crashes and caller retries; never accept an arbitrary
            // alternate key that could create a second Dynamics quote for the same Dhole rate.
            var key = "dhole-rate-" + request.DholeRateId.ToString("N");
            if (http.Request.Headers["Idempotency-Key"].ToString() != key)
                throw new ReadRejected(400, "quote_idempotency_key_mismatch");

            var policy = await writeAccess.ResolveAsync(request.ConnectionCode, "quote", ct);
            var template = await templates.FindAsync(policy.Connection.Id, "CreateQuote", ct);
            if (template?.ActiveVersion is not int version)
                throw new ReadRejected(409, "create_quote_template_inactive");
            var definition = template.History.SingleOrDefault(v => v.Version == version)?.Definition;
            if (definition is null || definition.EntityName != "quote" || definition.Operation != "create" ||
                !new[] { "name", "customerid_account@odata.bind", "transactioncurrencyid@odata.bind" }
                    .All(field => definition.Mappings.Any(m => m.Target == field)))
                throw new ReadRejected(409, "invalid_create_quote_template");

            // Validate metadata, input types, all Choice codes, lookup relationships and caller grant
            // BEFORE reserving the business ID. Invalid clients cannot lock legitimate rate submissions.
            var preview = await orchestrator.PreviewAsync(request.ConnectionCode,
                "CreateQuote", version, request.Inputs, ct);
            await writeAccess.PermitAsync(actor, policy, "create", preview.Fields, ct);
            var reserved = await quotes.ReserveAsync(policy.Connection.Id, request.DholeRateId,
                ScopeSecurity.ClientId(actor)!, ct);
            if (reserved.Status == "CREATED")
                return Results.Ok(new DholeQuoteTemplateResult(request.DholeRateId,
                    reserved.DynamicsQuoteId, "succeeded", true, null));
            if (reserved.Status != "NEW")
                throw new ReadRejected(409, "quote_requires_reconciliation");

            try
            {
                var written = await orchestrator.ExecuteAsync(actor, "CreateQuote",
                    new TemplateInputs(request.ConnectionCode, request.Inputs), key, ct);
                if (written is not WriteResult result)
                    throw new InvalidOperationException("CreateQuote must return a write result.");
                if (result.State != "succeeded" || result.RecordId is not Guid quoteId
                    || quoteId == Guid.Empty)
                {
                    await quotes.MarkUnknownAsync(reserved.Id, ScopeSecurity.ClientId(actor)!, CancellationToken.None);
                    return Results.Json(new DholeQuoteTemplateResult(request.DholeRateId,
                        null, "requires_reconciliation", false, result.OperationId), statusCode: 409);
                }
                await quotes.MarkCreatedAsync(reserved.Id, quoteId,
                    ScopeSecurity.ClientId(actor)!, CancellationToken.None);
                // Secondary cross-system lookup used by the finite lookup_ref transformation.
                // quote_links remains the authoritative reservation across all M2M callers.
                await references.UpsertAsync(policy.Connection.Id,
                    new ReferenceWrite("dhole-rate", request.DholeRateId.ToString("D"), "quote", quoteId),
                    ScopeSecurity.ClientId(actor)!, CancellationToken.None);
                return Results.Json(new DholeQuoteTemplateResult(request.DholeRateId, quoteId,
                    "succeeded", result.Replay, result.OperationId), statusCode: result.Replay ? 200 : 201);
            }
            catch (Exception)
            {
                // A failed write/response may have succeeded remotely. Never reissue automatically.
                await quotes.MarkUnknownAsync(reserved.Id, ScopeSecurity.ClientId(actor)!, CancellationToken.None);
                throw;
            }
        });

    private static async Task<IResult> Find(Guid rateId, string connectionCode,
        ReadAccessRepository read, IQuoteRepository quotes, ClaimsPrincipal actor,
        HttpContext http, CancellationToken ct) =>
        await Guard(http, async () =>
        {
            if (rateId == Guid.Empty) throw new ReadRejected(400, "invalid_rate_id");
            var policy = await read.ResolveAsync(connectionCode, "quote", ct);
            await read.PermitAsync(actor, policy, ["quoteid"], ct);
            var linked = await quotes.FindAsync(policy.Connection.Id, rateId, ct);
            return linked is null
                ? ApiProblems.Create(http, 404, "quote_link_not_found", "Quote not found.")
                : Results.Ok(new DholeQuoteTemplateResult(rateId, linked.DynamicsQuoteId,
                    linked.Status == "CREATED" ? "succeeded" : "requires_reconciliation",
                    true, null));
        });


    private static async Task<IResult> Reconcile(string code, Guid rateId,
        QuoteLinkReconciliationRequest request, ReadAccessRepository read,
        QuoteLinkReconciliationService links, ClaimsPrincipal actor,
        HttpContext http, CancellationToken ct) =>
        await Guard(http, async () =>
        {
            var conn=await read.ConnectionAsync(code,ct);
            var result=await links.ConfirmExistingAsync(conn.Id,rateId,request,
                ScopeSecurity.ActorId(actor),ct);
            return Results.Ok(result);
        });

    private static async Task<IResult> Guard(HttpContext http, Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (ReadRejected e) { return ApiProblems.Create(http, e.Status, e.Code, "Quote request rejected."); }
        catch (HttpRequestException) { return ApiProblems.Create(http, 502, "connector_upstream_error", "Dataverse unavailable."); }
        catch (TaskCanceledException) { return ApiProblems.Create(http, 504, "connector_upstream_timeout", "Dataverse timeout."); }
    }
}
