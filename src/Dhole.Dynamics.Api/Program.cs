using Dhole.Dynamics.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDholeApiSecurity(builder.Configuration, builder.Environment);
builder.Services.AddDholeApiInfrastructure(builder.Configuration);
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
app.UseRateLimiter();
app.MapHealthEndpoints();
app.MapAdminAuthEndpoints();
app.MapDynamicsEndpoints();

if (app.Environment.IsDevelopment())
    app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();

app.Run();
public partial class Program { }
