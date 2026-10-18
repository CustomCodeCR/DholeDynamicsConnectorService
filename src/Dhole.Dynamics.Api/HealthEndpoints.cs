using Npgsql;

namespace Dhole.Dynamics.Api;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new
            { service = "DholeDynamicsConnectorService", status = "live" })).AllowAnonymous();

        async Task<IResult> Ready(NpgsqlDataSource db, CancellationToken ct)
        {
            try
            {
                await using var cn = await db.OpenConnectionAsync(ct);
                await using var command = new NpgsqlCommand("SELECT 1 FROM dynamics.connections LIMIT 1", cn);
                await command.ExecuteScalarAsync(ct);
                return Results.Ok(new { status = "ready" });
            }
            catch (Exception) { return Results.StatusCode(503); }
        }

        app.MapGet("/health/ready", Ready).AllowAnonymous();
        // Dhole Gateway health alias appends /health to service URL.
        app.MapGet("/health", Ready).AllowAnonymous();

        return app;
    }
}
