using Dhole.Dynamics.Api;

var builder = WebApplication.CreateBuilder(args);
ReleaseSecretFiles.Apply(builder.Configuration);
DholeRuntimeKeys.Apply(builder.Configuration);
ReleaseDataProtection.Configure(builder.Services,builder.Configuration,builder.Environment);
builder.Services.AddDholeApiSecurity(builder.Configuration, builder.Environment);
builder.Services.AddDholeApiInfrastructure(builder.Configuration);
builder.Services.AddHostedService<DynamicsRealtimeOutboxPublisher>();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.AddExceptionHandler<SafeExceptionHandler>();
builder.Services.AddOpenApi("v1");
builder.Services.AddOpenApi("admin-v1");

var app = builder.Build();
app.UseExceptionHandler();
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<RequestDiagnosticsMiddleware>();
app.Use(async (http, next) =>
{
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    http.Response.Headers["X-Frame-Options"] = "DENY";
    http.Response.Headers["Cache-Control"] = "no-store";
    await next();
});
app.UseCors("AdminSameOrigin");
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ProductionWriteGateMiddleware>();
app.UseRateLimiter();
app.MapHealthEndpoints();
app.MapAdminAuthEndpoints();
app.MapDynamicsEndpoints();
app.MapMetadataEndpoints();
app.MapReadEndpoints();
app.MapSavedQueryApiKeyEndpoints();
app.MapTemplateApiKeyEndpoints();
app.MapPricingQuoteTotalEndpoints();
app.MapLegacyQuoteRecoveryEndpoints();
app.MapWriteEndpoints();
app.MapTemplateEndpoints();
app.MapEventAutomationEndpoints();
app.MapDholeQuoteTemplateEndpoints();
app.MapAdminConsoleEndpoints();
app.MapLegacyMigrationEndpoints();
app.MapDurableJobEndpoints();
app.MapSyncChangeEndpoints();

if (app.Environment.IsDevelopment())
    app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();

// Dhole API Manager: authenticated runtime inventory, not a public proxy.
app.MapGet("/api/internal/third-party-route-catalog", (HttpContext http) =>
{
    var allowed = http.User.Identity?.IsAuthenticated == true &&
        http.User.Claims.Where(c => c.Type is "scope" or "scp" or "scopes")
            .SelectMany(c => c.Value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
            .Contains("integrations.thirdparty.manage", StringComparer.OrdinalIgnoreCase);
    if (!allowed) return (IResult)Results.StatusCode(StatusCodes.Status403Forbidden);
    var routes = new List<object>();
    foreach (var source in ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources)
        foreach (var endpoint in source.Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods;
            if (methods is null || endpoint.RoutePattern.RawText is not { } path) continue;
            foreach (var method in methods)
                if (method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE")
                    routes.Add(new { method, path = "/" + path.TrimStart('/') });
        }
    return (IResult)Results.Ok(routes);
}).ExcludeFromDescription();

app.Run();
public partial class Program { }
