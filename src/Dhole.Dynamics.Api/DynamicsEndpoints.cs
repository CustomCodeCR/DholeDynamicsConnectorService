using System.Security.Claims;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;

namespace Dhole.Dynamics.Api;

public static class DynamicsEndpoints
{
    public static IEndpointRouteBuilder MapDynamicsEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/v1").WithGroupName("admin-v1");
        admin.MapGet("/connections", ListConnections).RequireAuthorization("Connections");
        admin.MapPost("/connections", UpsertConnection).RequireAuthorization("Connections");
        admin.MapPut("/connections/{code}/secret", RotateSecret).RequireAuthorization("Connections");

        var runtime = app.MapGroup("/api/v1").WithGroupName("v1");
        runtime.MapPost("/quotes", CreateQuote).RequireAuthorization("QuoteCreate");
        runtime.MapGet("/quotes/links/{dholeRateId:guid}", GetQuoteLink).RequireAuthorization("QuoteRead");
        runtime.MapGet("/quotes/{id:guid}", GetQuote).RequireAuthorization("QuoteRead");

        // Transitional routes retained until consumer migration; do not remove without contract tests.
        app.MapGet("/api/dynamics/connections", ListConnections).RequireAuthorization("Connections");
        app.MapPost("/api/dynamics/connections", UpsertConnection).RequireAuthorization("Connections");
        app.MapPut("/api/dynamics/connections/{code}/secret", RotateSecret).RequireAuthorization("Connections");
        app.MapPost("/api/dynamics/quotes", CreateQuote).RequireAuthorization("QuoteCreate");
        app.MapGet("/api/dynamics/quotes/links/{dholeRateId:guid}", GetQuoteLink).RequireAuthorization("QuoteRead");
        app.MapGet("/api/dynamics/quotes/{id:guid}", GetQuote).RequireAuthorization("QuoteRead");
        return app;
    }

    private static async Task<IResult> ListConnections(IConnectionRepository repository, CancellationToken ct) =>
        Results.Ok(await repository.ListAsync(ct));

    private static async Task<IResult> UpsertConnection(
        ConnectionUpsertRequest request, IConnectionRepository repository, ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 64 ||
            request.TenantId == Guid.Empty || request.ClientId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ClientSecret) ||
            !DataverseUrlValidator.TryNormalize(request.DataverseUrl, out var normalized))
            return ApiProblems.Create(http, 400, "invalid_connection", "Invalid connection configuration.");

        return Results.Ok(await repository.UpsertAsync(
            request with { Code = request.Code.Trim().ToLowerInvariant(), DataverseUrl = normalized },
            ScopeSecurity.ActorId(actor), ct));
    }

    private static async Task<IResult> RotateSecret(
        string code, SecretRotationRequest request, IConnectionRepository repository,
        ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ClientSecret))
            return ApiProblems.Create(http, 400, "invalid_secret", "Secret must not be empty.");
        return await repository.RotateSecretAsync(code, request.ClientSecret, ScopeSecurity.ActorId(actor), ct)
            ? Results.NoContent() : ApiProblems.Create(http, 404, "connection_not_found", "Connection not found.");
    }

    private static async Task<IResult> CreateQuote(
        QuoteCreateRequest request, QuoteApplicationService service, ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (request.DholeRateId == Guid.Empty || request.CustomerAccountId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 300 ||
            string.IsNullOrWhiteSpace(request.ConnectionCode) || request.ConnectionCode.Length > 64)
            return ApiProblems.Create(http, 400, "invalid_quote", "Rate, client, name and connection are mandatory.");

        var submission = await service.SubmitAsync(request, ScopeSecurity.ActorId(actor), ct);
        return submission.State switch
        {
            QuoteSubmissionState.ConnectionNotFound =>
                ApiProblems.Create(http, 404, "connection_not_found", "Connection not configured."),
            QuoteSubmissionState.Existing => Results.Ok(submission.Result),
            QuoteSubmissionState.Conflict =>
                ApiProblems.Create(http, 409, "quote_requires_reconciliation", "Quote already submitted or needs reconciliation."),
            QuoteSubmissionState.Created => Results.Created(
                $"/api/v1/quotes/links/{request.DholeRateId}?connectionCode={Uri.EscapeDataString(request.ConnectionCode)}",
                submission.Result),
            _ => ApiProblems.Create(http, 502, "dataverse_unknown", "Outcome is unknown; reconcile before retrying.")
        };
    }

    private static async Task<IResult> GetQuoteLink(
        Guid dholeRateId, string connectionCode, IConnectionRepository connections,
        IQuoteRepository quotes, HttpContext http, CancellationToken ct)
    {
        var connection = await connections.FindAsync(connectionCode, ct);
        if (connection is null) return ApiProblems.Create(http, 404, "connection_not_found", "Connection not found.");
        var link = await quotes.FindAsync(connection.Id, dholeRateId, ct);
        return link is null
            ? ApiProblems.Create(http, 404, "quote_not_found", "Quote link not found.")
            : Results.Ok(link);
    }

    private static async Task<IResult> GetQuote(
        Guid id, string connectionCode, IConnectionRepository connections,
        IDataverseClient dataverse, HttpContext http, CancellationToken ct)
    {
        if (id == Guid.Empty) return ApiProblems.Create(http, 400, "invalid_id", "Invalid quote ID.");
        var connection = await connections.FindAsync(connectionCode, ct);
        if (connection is null) return ApiProblems.Create(http, 404, "connection_not_found", "Connection not found.");
        try { return Results.Content(await dataverse.GetQuoteAsync(connection, id, ct), "application/json"); }
        catch (HttpRequestException)
        {
            return ApiProblems.Create(http, 502, "dataverse_error", "Dataverse request failed.");
        }
    }
}
