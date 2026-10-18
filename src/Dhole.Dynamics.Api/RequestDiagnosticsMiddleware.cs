using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace Dhole.Dynamics.Api;

/// <summary>Persists only sanitized route pattern, status, duration and correlation ID.</summary>
public sealed class RequestDiagnosticsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, NpgsqlDataSource database,
        ILogger<RequestDiagnosticsMiddleware> log)
    {
        var track = context.Request.Path.StartsWithSegments("/api");
        var watch = Stopwatch.StartNew();
        try { await next(context); }
        finally
        {
            if (!track) return;
            watch.Stop();
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            if (string.IsNullOrWhiteSpace(route) || route.Length > 250) return;
            var id = Guid.TryParse(context.TraceIdentifier, out var current) ? current : Guid.NewGuid();
            try
            {
                await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
                await using var command = new NpgsqlCommand("""
                    INSERT INTO dynamics.http_observations
                    (id,correlation_id,route_pattern,method,status_code,elapsed_ms)
                    VALUES(@id,@correlation,@route,@method,@status,@elapsed)
                    """, connection);
                command.CommandTimeout = 2;
                command.Parameters.AddWithValue("id",Guid.NewGuid());
                command.Parameters.AddWithValue("correlation",id);
                command.Parameters.AddWithValue("route",route);
                command.Parameters.AddWithValue("method",context.Request.Method[..Math.Min(12,context.Request.Method.Length)]);
                command.Parameters.AddWithValue("status",Math.Clamp(context.Response.StatusCode,100,599));
                command.Parameters.AddWithValue("elapsed",(int)Math.Min(watch.ElapsedMilliseconds,3600000));
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException
                or OperationCanceledException or InvalidOperationException)
            {
                log.LogWarning("Sanitized API request metric could not be stored.");
            }
        }
    }
}
