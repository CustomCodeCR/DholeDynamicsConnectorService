using Dhole.Dynamics.Api;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Infrastructure;
using Dhole.Dynamics.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDholeApiSecurity(builder.Configuration);
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
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthEndpoints();
app.MapDynamicsEndpoints();

if (app.Environment.IsDevelopment())
    app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();

app.Run();
public partial class Program { }
