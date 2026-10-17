using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

namespace Dhole.Dynamics.Api;

public static class MetadataEndpoints
{
    public static IEndpointRouteBuilder MapMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/v1/connections/{code}/metadata")
            .WithGroupName("admin-v1").RequireAuthorization("Admin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        group.MapGet("", GetCurrent);
        group.MapGet("/diff", GetDiff);
        group.MapGet("/policies", GetPolicies);
        group.MapPost("/refresh", Refresh);
        group.MapPut("/entities/{entity}/enabled", SetEntity);
        group.MapPut("/entities/{entity}/fields/{field}/policy", SetField);
        group.MapGet("/schedule", GetSchedule);
        group.MapPut("/schedule", SetSchedule);
        return app;
    }

    private static async Task<IResult> GetCurrent(string code, IConnectionRepository connections,
        MetadataRepository metadata, HttpContext http, CancellationToken ct)
    {
        var conn = await connections.FindAsync(code, ct);
        if (conn is null) return NotFound(http);
        var current = await metadata.LatestAsync(conn.Id, ct);
        return current is null ? ApiProblems.Create(http, 404, "metadata_not_discovered",
            "No metadata snapshot. Refresh from Dataverse first.") : Results.Ok(current);
    }

    private static async Task<IResult> GetDiff(string code, IConnectionRepository connections,
        MetadataRepository metadata, HttpContext http, CancellationToken ct)
    {
        var conn = await connections.FindAsync(code, ct);
        if (conn is null) return NotFound(http);
        var diff = await metadata.CompareLatestAsync(conn.Id, ct);
        return diff is null ? ApiProblems.Create(http, 404, "metadata_not_discovered",
            "No metadata snapshot.") : Results.Ok(diff);
    }

    private static async Task<IResult> GetPolicies(string code, IConnectionRepository connections,
        MetadataRepository metadata, HttpContext http, CancellationToken ct)
    {
        var conn = await connections.FindAsync(code, ct);
        return conn is null ? NotFound(http) : Results.Ok(await metadata.PoliciesAsync(conn.Id, ct));
    }

    private static async Task<IResult> Refresh(string code, MetadataRefreshService service,
        System.Security.Claims.ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        try
        {
            var current = await service.RefreshAsync(code, "manual", ScopeSecurity.ActorId(actor), ct);
            return current is null ? NotFound(http) : Results.Ok(current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or InvalidOperationException or System.Text.Json.JsonException
                or Microsoft.Identity.Client.MsalException)
        {
            return ApiProblems.Create(http, 502, "metadata_refresh_failed",
                "Dataverse metadata refresh failed; previous snapshot was preserved.");
        }
    }

    private static async Task<IResult> SetEntity(string code, string entity, MetadataToggle body,
        IConnectionRepository connections, MetadataRepository metadata,
        System.Security.Claims.ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (!DataverseMetadataDiscovery.SafeName(entity))
            return ApiProblems.Create(http, 400, "invalid_entity", "Invalid entity logical name.");
        var conn = await connections.FindAsync(code, ct);
        if (conn is null) return NotFound(http);
        return await metadata.SetEntityAsync(conn.Id, entity, body.Enabled, ScopeSecurity.ActorId(actor), ct)
            ? Results.NoContent()
            : ApiProblems.Create(http, 409, "entity_not_in_snapshot", "Entity is absent from latest snapshot.");
    }

    private static async Task<IResult> SetField(string code, string entity, string field,
        MetadataFieldToggle body, IConnectionRepository connections, MetadataRepository metadata,
        System.Security.Claims.ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (!DataverseMetadataDiscovery.SafeName(entity) || !DataverseMetadataDiscovery.SafeName(field))
            return ApiProblems.Create(http, 400, "invalid_field", "Invalid metadata name.");
        var conn = await connections.FindAsync(code, ct);
        if (conn is null) return NotFound(http);
        return await metadata.SetFieldAsync(conn.Id, entity, field, body, ScopeSecurity.ActorId(actor), ct)
            ? Results.NoContent()
            : ApiProblems.Create(http, 409, "metadata_write_denied",
                "Enable the entity first and select only capabilities approved by Dataverse metadata.");
    }

    private static async Task<IResult> GetSchedule(string code, IConnectionRepository connections,
        MetadataRepository metadata, HttpContext http, CancellationToken ct)
    {
        var conn = await connections.FindAsync(code, ct);
        return conn is null ? NotFound(http) : Results.Ok(await metadata.GetScheduleAsync(conn.Id, ct));
    }

    private static async Task<IResult> SetSchedule(string code, MetadataSchedule schedule,
        IConnectionRepository connections, MetadataRepository metadata,
        System.Security.Claims.ClaimsPrincipal actor, HttpContext http, CancellationToken ct)
    {
        if (schedule.IntervalMinutes is < 60 or > 10080)
            return ApiProblems.Create(http, 400, "invalid_schedule", "Refresh interval must be 60 to 10080 minutes.");
        var conn = await connections.FindAsync(code, ct);
        if (conn is null) return NotFound(http);
        await metadata.SetScheduleAsync(conn.Id, schedule, ScopeSecurity.ActorId(actor), ct);
        return Results.NoContent();
    }

    private static IResult NotFound(HttpContext http) =>
        ApiProblems.Create(http, 404, "connection_not_found", "Connection not found.");
}
