using Npgsql;

namespace Dhole.Dynamics.Api;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new
            { service = "DholeDynamicsConnectorService", status = "live" })).AllowAnonymous();

        app.MapGet("/health/ready", async (NpgsqlDataSource db, CancellationToken ct) =>
        {
            try
            {
                await using var cn = await db.OpenConnectionAsync(ct);
                await using var command = new NpgsqlCommand("SELECT 1 FROM dynamics.connections LIMIT 1", cn);
                await command.ExecuteScalarAsync(ct);
                return Results.Ok(new { status = "ready" });
            }
            catch (Exception) { return Results.StatusCode(503); }
        }).AllowAnonymous();

        return app;
    }
}
