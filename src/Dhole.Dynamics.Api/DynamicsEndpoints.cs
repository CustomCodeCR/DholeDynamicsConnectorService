using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;

namespace Dhole.Dynamics.Api;

public static class DynamicsEndpoints
{
    private static readonly string[] QuoteReadFields =
    [
        "quoteid", "quotenumber", "name", "new_pol", "new_poe", "new_pod",
        "effectivefrom", "expireson", "new_flete", "new_origen", "new_destino"
    ];

    public static IEndpointRouteBuilder MapDynamicsEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/v1").WithGroupName("admin-v1")
            .RequireAuthorization("Admin").RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        admin.MapGet("/connections", ListConnections);
        admin.MapPost("/connections", UpsertConnection);
        admin.MapPut("/connections/{code}/secret", RotateSecret);
        admin.MapPost("/connections/{code}/test", TestConnection);
        admin.MapGet("/clients", ListClients).RequireAuthorization("SecurityAdmin");
        admin.MapGet("/grants", ListGrants).RequireAuthorization("SecurityAdmin");
        admin.MapPost("/clients", RegisterClient).RequireAuthorization("SecurityAdmin");
        admin.MapPost("/grants", UpsertGrant).RequireAuthorization("SecurityAdmin");

        var runtime = app.MapGroup("/api/v1").WithGroupName("v1")
            .RequireAuthorization("M2M");
        runtime.MapPost("/quotes", CreateQuote);
        runtime.MapGet("/quotes/links/{dholeRateId:guid}", GetQuoteLink);
        runtime.MapGet("/quotes/{id:guid}", GetQuote);

        // Legacy route names remain for existing clients, but NO HMAC token or
        // machine token can authenticate an Admin action after this security upgrade.
        var legacyAdmin = app.MapGroup("/api/dynamics").RequireRateLimiting("AdminRate").RequireCsrfForWrites()
            .RequireAuthorization("Admin");
        legacyAdmin.MapGet("/connections", ListConnections);
        legacyAdmin.MapPost("/connections", UpsertConnection);
        legacyAdmin.MapPut("/connections/{code}/secret", RotateSecret);

        var legacyM2m = app.MapGroup("/api/dynamics").RequireAuthorization("M2M");
        legacyM2m.MapPost("/quotes", CreateQuote);
        legacyM2m.MapGet("/quotes/links/{dholeRateId:guid}", GetQuoteLink);
        legacyM2m.MapGet("/quotes/{id:guid}", GetQuote);
        return app;
    }

    private static async Task<IResult> ListConnections(IConnectionRepository repository, CancellationToken ct) =>
        Results.Ok(await repository.ListAsync(ct));

    private static async Task<IResult> UpsertConnection(ConnectionUpsertRequest request,
        IConnectionRepository repository, ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 64
            || !System.Text.RegularExpressions.Regex.IsMatch(request.Code, "^[a-zA-Z0-9_-]+$")
            || request.TenantId == Guid.Empty || request.ClientId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.ClientSecret)
            || !DataverseUrlValidator.TryNormalize(request.DataverseUrl, out var normalized))
            return ApiProblems.Create(http, 400, "invalid_connection", "Invalid connection configuration.");

        return Results.Ok(await repository.UpsertAsync(
            request with { Code = request.Code.Trim().ToLowerInvariant(), DataverseUrl = normalized },
            ScopeSecurity.ActorId(actor), ct));
    }

    private static async Task<IResult> RotateSecret(string code, SecretRotationRequest request,
        IConnectionRepository repository, ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ClientSecret))
            return ApiProblems.Create(http, 400, "invalid_secret", "Secret must not be empty.");
        return await repository.RotateSecretAsync(code, request.ClientSecret, ScopeSecurity.ActorId(actor), ct)
            ? Results.NoContent()
            : ApiProblems.Create(http, 404, "connection_not_found", "Connection not found.");
    }

    private static async Task<IResult> TestConnection(string code, IConnectionRepository connections,
        IDataverseClient dataverse, HttpContext http, CancellationToken ct)
    {
        var connection = await connections.FindAsync(code, ct);
        if (connection is null) return ApiProblems.Create(http, 404, "connection_not_found", "Connection not found.");
        try { return Results.Ok(await dataverse.TestConnectionAsync(connection, ct)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or InvalidOperationException or Microsoft.Identity.Client.MsalException)
        {
            // Do not expose third-party details, access tokens, app secrets or error payloads.
            return ApiProblems.Create(http, 502, "dataverse_connection_failed",
                "Dataverse identity or connection validation failed.");
        }
    }

    private static async Task<IResult> ListClients(ClientAccessRepository repository, CancellationToken ct) =>
        Results.Ok(await repository.ListClientsAsync(ct));

    private static async Task<IResult> ListGrants(ClientAccessRepository repository, CancellationToken ct) =>
        Results.Ok(await repository.ListGrantsAsync(ct));

    private static async Task<IResult> RegisterClient(ClientRegistrationRequest request,
        ClientAccessRepository repository, IConfiguration config, ClaimsPrincipal actor,
        HttpContext http, CancellationToken ct)
    {
        // Registrations are bound to the one trusted configured M2M issuer, never arbitrary OIDC tenants.
        var trustedIssuer = config["M2m:Authority"]?.TrimEnd('/');
        if (string.IsNullOrEmpty(trustedIssuer) || request.Issuer != trustedIssuer
            || string.IsNullOrWhiteSpace(request.ClientId) || request.ClientId.Length > 128
            || string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 120)
            return ApiProblems.Create(http, 400, "invalid_m2m_client", "Untrusted issuer or invalid client.");
        return Results.Ok(await repository.RegisterAsync(request, ScopeSecurity.ActorId(actor), ct));
    }

    private static async Task<IResult> UpsertGrant(GrantRequest request,
        ClientAccessRepository repository, ReadAccessRepository readPolicy, ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (request.ClientId == Guid.Empty || string.IsNullOrWhiteSpace(request.ConnectionCode)
            || request.ConnectionCode.Length > 64 || request.AllowedFields is null
            || !GrantRules.ValidGrant(request.EntitySet, request.Operation, request.AllowedFields, request.MaxRequestsPerMinute))
            return ApiProblems.Create(http, 400, "invalid_m2m_grant", "Invalid connection or field grant.");
        if (request.Operation == "read" && request.EntitySet != "quotes")
        {
            try
            {
                var approved = await readPolicy.ResolveAsync(request.ConnectionCode, request.EntitySet, ct);
                if (request.AllowedFields.Any(f => !approved.Allowed.Contains(f)))
                    return ApiProblems.Create(http, 403, "grant_fields_denied", "Fields are not enabled in metadata.");
            }
            catch (ReadRejected)
            {
                return ApiProblems.Create(http, 403, "grant_entity_denied", "Entity is not enabled in metadata.");
            }
        }
        if (request.Operation is "create" or "update" or "upsert" or "delete"
            && !(request.EntitySet == "quotes" && request.Operation == "create"))
        {
            try
            {
                var writer = http.RequestServices.GetRequiredService<WriteAccessService>();
                var candidate = await writer.ResolveAsync(request.ConnectionCode, request.EntitySet, ct);
                if (request.Operation == "delete")
                {
                    var deletion = http.RequestServices.GetRequiredService<DeletePolicyRepository>();
                    var policy = await deletion.GetAsync(candidate.Connection.Id, request.EntitySet, ct);
                    if (!policy.Enabled || request.AllowedFields.Length != 1 ||
                        request.AllowedFields[0] != candidate.Table.PrimaryIdAttribute)
                        return ApiProblems.Create(http, 403, "delete_grant_denied", "Enable explicit deletion policy first.");
                }
                else
                {
                    foreach (var field in request.AllowedFields)
                    {
                        var col = candidate.Table.Columns.FirstOrDefault(c => c.LogicalName == field);
                        var enabled = candidate.Permissions.Fields.FirstOrDefault(p =>
                            p.EntityName == request.EntitySet && p.FieldName == field);
                        if (col is null || enabled is null || !enabled.Enabled || col.IsCalculated ||
                            enabled.MetadataFingerprint != MetadataRepository.Fingerprint(col) ||
                            (request.Operation is "create" or "upsert") && (!col.CanCreate || !enabled.CanCreate) ||
                            (request.Operation is "update" or "upsert") && (!col.CanUpdate || !enabled.CanUpdate))
                            return ApiProblems.Create(http, 403, "write_grant_fields_denied",
                                "Grant contains fields not approved for the write operation.");
                    }
                }
            }
            catch (ReadRejected)
            {
                return ApiProblems.Create(http, 403, "write_grant_entity_denied", "Entity not approved.");
            }
        }
        var grant = await repository.UpsertGrantAsync(request, ScopeSecurity.ActorId(actor), ct);
        return grant is null
            ? ApiProblems.Create(http, 404, "client_or_connection_not_found", "Client or connection not found.")
            : Results.Ok(grant);
    }

    private static async Task<IResult?> AuthorizeRuntime(
        ClaimsPrincipal user, string code, string operation, IReadOnlyCollection<string> fields,
        ClientAccessRepository policies, HttpContext http, CancellationToken ct)
    {
        var scope = $"dynamics.quotes.{operation}";
        var verifiedIssuer = user.FindFirst("connector:issuer")?.Value;
        var client = ScopeSecurity.ClientId(user);
        if (!ScopeSecurity.HasScope(user, scope) || string.IsNullOrWhiteSpace(verifiedIssuer)
            || string.IsNullOrWhiteSpace(client))
            return ApiProblems.Create(http, 403, "scope_denied", "Insufficient machine permissions.");
        var decision = await policies.AuthorizeAsync(verifiedIssuer, client, code, "quotes", operation, fields, ct);
        return decision switch
        {
            GrantDecision.Allowed => null,
            GrantDecision.QuotaExceeded =>
                ApiProblems.Create(http, 429, "client_quota_exceeded", "Machine quota exceeded."),
            _ => ApiProblems.Create(http, 403, "connection_access_denied", "Not authorized for this connection or fields.")
        };
    }

    private static async Task<IResult> CreateQuote(QuoteCreateRequest request,
        QuoteApplicationService service, IConnectionRepository connections,
        ClientAccessRepository policies, ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (request.DholeRateId == Guid.Empty || request.CustomerAccountId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 300
            || string.IsNullOrWhiteSpace(request.ConnectionCode) || request.ConnectionCode.Length > 64)
            return ApiProblems.Create(http, 400, "invalid_quote", "Rate, customer, name and connection required.");

        var connection = await connections.FindAsync(request.ConnectionCode, ct);
        if (connection is null)
            return ApiProblems.Create(http, 403, "connection_access_denied", "Not authorized for this connection.");
        var currencyId = request.TransactionCurrencyId ?? connection.DefaultCurrencyId;
        if (currencyId is null || currencyId == Guid.Empty)
            return ApiProblems.Create(http, 400, "missing_currency", "Transaction currency is required.");
        var payloadFields = DataverseClient.CreatePayload(request, currencyId.Value).Keys.ToArray();
        var denied = await AuthorizeRuntime(actor, request.ConnectionCode, "create", payloadFields, policies, http, ct);
        if (denied is not null) return denied;

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

    private static async Task<IResult> GetQuoteLink(Guid dholeRateId, string connectionCode,
        IConnectionRepository connections, IQuoteRepository quotes,
        ClientAccessRepository policies, ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        var denied = await AuthorizeRuntime(user, connectionCode, "read", ["quoteid"], policies, http, ct);
        if (denied is not null) return denied;
        var connection = await connections.FindAsync(connectionCode, ct);
        if (connection is null) return ApiProblems.Create(http, 404, "connection_not_found", "Connection not found.");
        var link = await quotes.FindAsync(connection.Id, dholeRateId, ct);
        return link is null ? ApiProblems.Create(http, 404, "quote_not_found", "Quote link not found.")
            : Results.Ok(link);
    }

    private static async Task<IResult> GetQuote(Guid id, string connectionCode,
        IConnectionRepository connections, IDataverseClient dataverse,
        ClientAccessRepository policies, ClaimsPrincipal user, HttpContext http, CancellationToken ct)
    {
        if (id == Guid.Empty) return ApiProblems.Create(http, 400, "invalid_id", "Invalid quote ID.");
        var denied = await AuthorizeRuntime(user, connectionCode, "read", QuoteReadFields, policies, http, ct);
        if (denied is not null) return denied;
        var connection = await connections.FindAsync(connectionCode, ct);
        if (connection is null) return ApiProblems.Create(http, 404, "connection_not_found", "Connection not found.");
        try { return Results.Content(await dataverse.GetQuoteAsync(connection, id, ct), "application/json"); }
        catch (HttpRequestException)
        {
            return ApiProblems.Create(http, 502, "dataverse_error", "Dataverse request failed.");
        }
    }
}
