using System.Security.Claims;
using System.Text.Json;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Microsoft.AspNetCore.RateLimiting;

namespace Dhole.Dynamics.Api;

/// <summary>Only scoped, structured searches; administration cannot call arbitrary OData.</summary>
public static class ReadEndpoints
{
    public static IEndpointRouteBuilder MapReadEndpoints(this IEndpointRouteBuilder app)
    {
        var runtime = app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization("M2M");
        runtime.MapGet("/records/{entity}/{id:guid}", GetById);
        runtime.MapPost("/records/{entity}/by-key", ByKey);
        runtime.MapPost("/records/{entity}/search", Search);
        runtime.MapPost("/saved-queries/{name}/execute", ExecuteSaved);

        var admin = app.MapGroup("/api/admin/v1/connections/{code}/saved-queries")
            .WithGroupName("admin-v1").RequireAuthorization("Admin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        admin.MapGet("", ListSaved);
        admin.MapPost("", Save);
        admin.MapPut("/{name}/enabled", Enable);
        return app;
    }

    private static async Task<IResult> GetById(string entity, Guid id, string connectionCode,
        string select, ClaimsPrincipal user, ReadSearchService service, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () => Results.Ok(await service.ByIdAsync(user, connectionCode, entity, id,
            Fields(select), ct)));

    private static async Task<IResult> ByKey(string entity, ReadAlternateKeyRequest body,
        ClaimsPrincipal user, ReadSearchService service, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () => Results.Ok(await service.ByKeyAsync(user, body.ConnectionCode, entity,
            body.Keys, body.Select, ct)));

    private static async Task<IResult> Search(string entity, ReadSearchRequest body, ClaimsPrincipal user,
        ReadSearchService service, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () => Results.Ok(await service.SearchAsync(user, entity, body, ct)));

    private static async Task<IResult> ExecuteSaved(string name, ExecuteSavedQueryRequest body,
        ClaimsPrincipal user, ReadSearchService service, SavedQueryRepository saved,
        ReadAccessRepository access, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () =>
        {
            if (!ReadQueryCompiler.ValidParam(name) || body.Parameters is null ||
                string.IsNullOrWhiteSpace(body.ConnectionCode))
                throw new ReadRejected(400, "invalid_saved_query_request");
            // Never resolve saved templates by name across organizations.
            var conn = await access.ConnectionAsync(body.ConnectionCode, ct);
            var template = await saved.FindAsync(conn.Id, name, ct);
            if (template is not { Enabled: true }) throw new ReadRejected(404, "saved_query_not_found");
            ReadSpec? query = null;
            if (string.IsNullOrWhiteSpace(body.Cursor))
                query = ReadSavedQueryBinder.Bind(template.Query, template.Parameters, body.Parameters);
            else if (body.Parameters.Count > 0)
                throw new ReadRejected(400, "cursor_with_query");
            var result = await service.SearchAsync(user, template.EntityName,
                new ReadSearchRequest(body.ConnectionCode, query, body.Cursor), ct, name);
            return Results.Ok(result);
        });

    private static async Task<IResult> ListSaved(string code, IConnectionRepository connections,
        SavedQueryRepository saved, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () =>
        {
            var conn = await connections.FindAsync(code, ct) ??
                throw new ReadRejected(404, "connection_not_found");
            return Results.Ok(await saved.ListAsync(conn.Id, ct));
        });

    private static async Task<IResult> Save(string code, SavedQueryWrite body,
        IConnectionRepository connections, SavedQueryRepository saved,
        ReadAccessRepository access, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () =>
        {
            if (!ReadQueryCompiler.ValidParam(body.Name) ||
                body.Query is null || body.Parameters is null)
                throw new ReadRejected(400, "invalid_saved_query");
            var policy = await access.ResolveAsync(code, body.EntityName, ct);
            // Validate every placeholder and all selected/filter/order fields against the active snapshot.
            var sample = ReadSavedQueryBinder.Bind(body.Query, body.Parameters,
                new Dictionary<string, JsonElement>(), validateOnly: true);
            var compiled = ReadQueryCompiler.Compile(policy.Table, sample with { Expand = null }, policy.Allowed);
            if (compiled.RequiredFields.Count == 0) throw new ReadRejected(400, "empty_saved_query");
            if (body.Query.Expand is { Count: > 0 })
                throw new ReadRejected(400, "saved_expand_requires_live_authorization");
            var record = await saved.SaveAsync(policy.Connection.Id, body, ScopeSecurity.ActorId(user), ct);
            return Results.Ok(record);
        });

    private static async Task<IResult> Enable(string code, string name, SavedQueryEnabled body,
        IConnectionRepository connections, SavedQueryRepository saved,
        ReadAccessRepository access, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () =>
        {
            var conn = await connections.FindAsync(code, ct) ??
                throw new ReadRejected(404, "connection_not_found");
            var current = await saved.FindAsync(conn.Id, name, ct) ??
                throw new ReadRejected(404, "saved_query_not_found");
            if (body.Enabled)
            {
                var policy = await access.ResolveAsync(code, current.EntityName, ct);
                var sample = ReadSavedQueryBinder.Bind(current.Query, current.Parameters,
                    new Dictionary<string, JsonElement>(), validateOnly: true);
                ReadQueryCompiler.Compile(policy.Table, sample with { Expand = null }, policy.Allowed);
            }
            await saved.SetEnabledAsync(conn.Id, name, body.Enabled, ScopeSecurity.ActorId(user), ct);
            return Results.NoContent();
        });

    private static IReadOnlyList<string> Fields(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static async Task<IResult> Guard(HttpContext http, Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ReadRejected e) { return ApiProblems.Create(http, e.Status, e.Code, "Read request rejected."); }
        catch (UnauthorizedAccessException)
        { return ApiProblems.Create(http, 403, "fields_denied", "Read fields are not authorized."); }
        catch (ArgumentException)
        { return ApiProblems.Create(http, 400, "invalid_query", "Invalid structured read query."); }
        catch (HttpRequestException)
        { return ApiProblems.Create(http, 502, "dataverse_unavailable", "Dataverse read failed."); }
        catch (TaskCanceledException)
        { return ApiProblems.Create(http, 504, "dataverse_timeout", "Dataverse read timed out."); }
        catch (System.Text.Json.JsonException)
        { return ApiProblems.Create(http, 502, "dataverse_invalid_json", "Dataverse returned invalid data."); }
        catch (Microsoft.Identity.Client.MsalException)
        { return ApiProblems.Create(http, 502, "dataverse_identity", "Dataverse identity is not available."); }
    }
}
