using System.Text;
using Dhole.Dynamics.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

string Require(string key) =>
    builder.Configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing required server configuration: {key}");

var jwtIssuer = Require("Jwt:Issuer");
var jwtAudience = Require("Jwt:Audience");
var jwtKey = Require("Jwt:SecretKey");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Jwt:SecretKey must be at least 32 bytes long.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Connections", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(ctx => ScopeSecurity.HasScope(ctx.User, "dynamics.connections.manage")));
    options.AddPolicy("QuoteCreate", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(ctx => ScopeSecurity.HasScope(ctx.User, "dynamics.quotes.create")));
    options.AddPolicy("QuoteRead", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(ctx => ScopeSecurity.HasScope(ctx.User, "dynamics.quotes.read")));
});

builder.Services.AddSingleton<Encryption>();
builder.Services.AddSingleton(NpgsqlDataSource.Create(Require("ConnectionStrings:Dynamics")));
builder.Services.AddScoped<ConnectionRepository>();
builder.Services.AddScoped<QuoteRepository>();
builder.Services.AddHttpClient<DataverseClient>(client => client.Timeout = TimeSpan.FromSeconds(60));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { service = "DholeDynamicsConnectorService", status = "live" }))
   .AllowAnonymous();

app.MapGet("/health/ready", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    try
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT 1 FROM dynamics.connections LIMIT 1", connection);
        await command.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ready" });
    }
    catch { return Results.StatusCode(503); }
}).AllowAnonymous();

app.MapGet("/api/dynamics/connections", async (ConnectionRepository repository, CancellationToken ct) =>
    Results.Ok(await repository.ListAsync(ct)))
    .RequireAuthorization("Connections");

app.MapPost("/api/dynamics/connections", async (
    ConnectionUpsertRequest request, ConnectionRepository repository, ClaimsPrincipal actor, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 64
        || request.TenantId == Guid.Empty || request.ClientId == Guid.Empty
        || string.IsNullOrWhiteSpace(request.ClientSecret)
        || !DataverseUrlValidator.TryNormalize(request.DataverseUrl, out var url))
        return Results.BadRequest(new { error = "Invalid code, Dataverse URL, tenant, client or secret." });

    var connection = await repository.UpsertAsync(
        request with { Code = request.Code.Trim().ToLowerInvariant(), DataverseUrl = url },
        ScopeSecurity.ActorId(actor), ct);
    return Results.Ok(connection);
}).RequireAuthorization("Connections");

app.MapPut("/api/dynamics/connections/{code}/secret", async (
    string code, SecretRotationRequest request, ConnectionRepository repository, ClaimsPrincipal actor, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.ClientSecret))
        return Results.BadRequest(new { error = "Client secret must not be empty." });

    return await repository.RotateSecretAsync(code, request.ClientSecret, ScopeSecurity.ActorId(actor), ct)
        ? Results.NoContent()
        : Results.NotFound();
}).RequireAuthorization("Connections");

app.MapPost("/api/dynamics/quotes", async (
    QuoteCreateRequest request, ConnectionRepository connections, QuoteRepository quotes,
    DataverseClient dataverse, ClaimsPrincipal actor, CancellationToken ct) =>
{
    if (request.DholeRateId == Guid.Empty || request.CustomerAccountId == Guid.Empty
        || string.IsNullOrWhiteSpace(request.Name)
        || request.Name.Length > 300 || request.ConnectionCode.Length is < 1 or > 64)
        return Results.BadRequest(new { error = "Rate, client, name and connection are mandatory." });
    var connection = await connections.FindAsync(request.ConnectionCode, ct);
    if (connection is null) return Results.NotFound(new { error = "Dynamics connection not configured." });

    var existing = await quotes.ReserveAsync(connection.Id, request.DholeRateId, ScopeSecurity.ActorId(actor), ct);
    if (existing.Status == "CREATED")
        return Results.Ok(new QuoteCreatedResult(request.DholeRateId, existing.DynamicsQuoteId, "CREATED", true));
    if (existing.Status != "NEW")
        return Results.Conflict(new { error = "Quote already submitted or requires reconciliation.", status = existing.Status });

    try
    {
        var created = await dataverse.CreateQuoteAsync(connection, request, ct);
        await quotes.MarkCreatedAsync(existing.Id, created.QuoteId, ScopeSecurity.ActorId(actor), ct);
        return Results.Created($"/api/dynamics/quotes/links/{request.DholeRateId}?connectionCode={Uri.EscapeDataString(connection.Code)}",
            new QuoteCreatedResult(request.DholeRateId, created.QuoteId, "CREATED", false));
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
    {
        // Uncertain remote state: never automatically POST again, to avoid duplicate Dynamics quotes.
        await quotes.MarkUnknownAsync(existing.Id, ScopeSecurity.ActorId(actor), ct);
        app.Logger.LogError("Dataverse quote creation failed, link {LinkId}; reconciliation required.", existing.Id);
        return Results.Problem("Dynamics create failed or timed out. Reconcile before retrying.", statusCode: 502);
    }
}).RequireAuthorization("QuoteCreate");

app.MapGet("/api/dynamics/quotes/links/{dholeRateId:guid}", async (
    Guid dholeRateId, string connectionCode, ConnectionRepository connections,
    QuoteRepository quotes, CancellationToken ct) =>
{
    var connection = await connections.FindAsync(connectionCode, ct);
    if (connection is null) return Results.NotFound();
    var link = await quotes.FindAsync(connection.Id, dholeRateId, ct);
    return link is null ? Results.NotFound() : Results.Ok(link);
}).RequireAuthorization("QuoteRead");

app.MapGet("/api/dynamics/quotes/{id:guid}", async (
    Guid id, string connectionCode, ConnectionRepository connections,
    DataverseClient dataverse, CancellationToken ct) =>
{
    if (id == Guid.Empty) return Results.BadRequest();
    var connection = await connections.FindAsync(connectionCode, ct);
    if (connection is null) return Results.NotFound();
    try { return Results.Content(await dataverse.GetQuoteAsync(connection, id, ct), "application/json"); }
    catch (HttpRequestException) { return Results.Problem("Dynamics quote lookup failed.", statusCode: 502); }
}).RequireAuthorization("QuoteRead");

app.Run();

public partial class Program { }
