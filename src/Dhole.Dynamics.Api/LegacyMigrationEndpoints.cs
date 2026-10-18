using System.Security.Claims;
using Dhole.Dynamics.Contracts;
using Microsoft.AspNetCore.RateLimiting;

namespace Dhole.Dynamics.Api;

/// <summary>No per-legacy-route controllers. Only preview/staging of audited declarative mappings.</summary>
public static class LegacyMigrationEndpoints
{
    public static IEndpointRouteBuilder MapLegacyMigrationEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/v1/connections/{code}/legacy-migration")
            .WithGroupName("admin-v1").RequireAuthorization("Admin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        admin.MapGet("/inventory", Inventory);
        admin.MapGet("/preview", Preview);
        admin.MapGet("/history", History);
        admin.MapPost("/import", Import).RequireAuthorization("SecurityAdmin");
        return app;
    }

    private static IResult Inventory() => Results.Ok(new
    {
        LegacyMigrationService.Bundle.Format,
        LegacyMigrationService.Bundle.Source,
        Entries = LegacyMigrationService.Bundle.Entries.Select(e => new
        {
            e.Id, e.Method, e.Route, e.Family, e.EntityName, e.SourceLine,
            e.Status, e.Reasons, e.QueryName, e.TemplateName,
            RequiredColumns = e.Query?.Select ?? e.Template?.Mappings.Select(m => m.Target).ToArray() ?? []
        })
    });

    private static async Task<IResult> Preview(string code, LegacyMigrationService migrations,
        HttpContext http, CancellationToken ct) =>
        await Guard(http, async () => Results.Ok(await migrations.PreviewAsync(code, ct)));

    private static async Task<IResult> History(string code, LegacyMigrationService migrations,
        HttpContext http, CancellationToken ct) =>
        await Guard(http, async () => Results.Ok(await migrations.HistoryAsync(code, ct)));

    private static async Task<IResult> Import(string code, LegacyImportRequest request,
        LegacyMigrationService migrations, ClaimsPrincipal user,
        HttpContext http, CancellationToken ct) =>
        await Guard(http, async () => Results.Ok(await migrations.ImportAsync(code,
            request.Confirm, ScopeSecurity.ActorId(user), ct)));

    private static async Task<IResult> Guard(HttpContext http, Func<Task<IResult>> task)
    {
        try { return await task(); }
        catch (ReadRejected ex)
        {
            return ApiProblems.Create(http, ex.Status, ex.Code, "Legacy migration request denied.");
        }
        catch (ArgumentException)
        {
            return ApiProblems.Create(http, 400, "invalid_legacy_migration", "Invalid legacy migration input.");
        }
    }
}
