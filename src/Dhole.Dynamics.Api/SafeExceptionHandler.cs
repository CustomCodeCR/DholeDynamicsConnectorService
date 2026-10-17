using Microsoft.AspNetCore.Diagnostics;

namespace Dhole.Dynamics.Api;

/// <summary>Internal exception details remain server-side; never expose tokens or Dataverse payloads.</summary>
public sealed class SafeExceptionHandler(ILogger<SafeExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        logger.LogError("Unhandled request failure; correlation {CorrelationId}; type {ExceptionType}",
            context.TraceIdentifier, exception.GetType().Name);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = 500,
            Title = "Internal service error",
            Detail = "Request could not be completed.",
            Type = "about:blank"
        };
        problem.Extensions["code"] = "internal_error";
        problem.Extensions["correlationId"] = context.TraceIdentifier;
        await context.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
