namespace Dhole.Dynamics.Api;

public sealed class CorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers["X-Correlation-ID"].ToString();
        var correlation = Guid.TryParse(incoming, out var parsed)
            ? parsed.ToString("D") : Guid.NewGuid().ToString("D");
        context.TraceIdentifier = correlation;
        context.Response.Headers["X-Correlation-ID"] = correlation;
        await next(context);
    }
}
