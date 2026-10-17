using System.Security.Claims;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Dhole.Dynamics.Api;

public static class WriteEndpoints
{
    public static IEndpointRouteBuilder MapWriteEndpoints(this IEndpointRouteBuilder app)
    {
        var runtime = app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization("M2M");
        runtime.MapPost("/records/{entity}", Create);
        runtime.MapPatch("/records/{entity}/{id:guid}", Patch);
        runtime.MapDelete("/records/{entity}/{id:guid}", Delete);
        runtime.MapPut("/records/{entity}/by-key", Upsert);
        runtime.MapPost("/records/batch", Batch);
        runtime.MapGet("/write-operations/{id:guid}", GetOwnOperation);

        var admin = app.MapGroup("/api/admin/v1/connections/{code}")
            .WithGroupName("admin-v1").RequireAuthorization("SecurityAdmin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        admin.MapGet("/delete-policies/{entity}", GetDeletePolicy);
        admin.MapPut("/delete-policies/{entity}", SetDeletePolicy);
        admin.MapGet("/write-operations/{id:guid}", GetAdminOperation);
        admin.MapPost("/write-operations/{id:guid}/reconcile", Reconcile);
        return app;
    }

    private static async Task<IResult> Create(string entity, GenericCreateRequest request,
        GenericWriteService service, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () => Response(http, await service.CreateAsync(user, entity,
            request, Key(http), ct)));

    private static async Task<IResult> Patch(string entity, Guid id, GenericPatchRequest request,
        GenericWriteService service, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () => Response(http, await service.PatchAsync(user, entity,
            id, request, Key(http), http.Request.Headers.IfMatch.ToString(), ct)));

    private static async Task<IResult> Delete(string entity, Guid id, [FromBody] GenericDeleteRequest request,
        GenericWriteService service, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () => Response(http, await service.DeleteAsync(user, entity,
            id, request, Key(http), http.Request.Headers.IfMatch.ToString(), ct)));

    private static async Task<IResult> Upsert(string entity, GenericUpsertRequest request,
        GenericWriteService service, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () => Response(http, await service.UpsertAsync(user, entity,
            request, Key(http), ct)));

    private static async Task<IResult> Batch(GenericBatchRequest request,
        GenericWriteService service, ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () => Response(http, await service.BatchAsync(user, request,
            Key(http), ct)));

    private static async Task<IResult> GetOwnOperation(Guid id, string connectionCode,
        ReadAccessRepository access, WriteLedger ledger, ClaimsPrincipal user,
        HttpContext http, CancellationToken ct) =>
        await Guard(http, async () =>
        {
            var conn = await access.ConnectionAsync(connectionCode, ct);
            var issuer = user.FindFirst("connector:issuer")?.Value;
            var client = ScopeSecurity.ClientId(user);
            if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(client))
                throw new ReadRejected(403, "client_denied");
            // The same client that submitted the operation can inspect its status.
            var status = await ledger.FindAsync(conn.Id, id, ct, issuer, client);
            return status is null ? ApiProblems.Create(http, 404, "write_not_found", "Operation not found.")
                : Results.Ok(status);
        });

    private static async Task<IResult> GetDeletePolicy(string code, string entity,
        ReadAccessRepository access, DeletePolicyRepository repository,
        HttpContext http, CancellationToken ct) =>
        await Guard(http, async () =>
        {
            var policy = await access.ResolveAsync(code, entity, ct);
            return Results.Ok(await repository.GetAsync(policy.Connection.Id, entity, ct));
        });

    private static async Task<IResult> SetDeletePolicy(string code, string entity,
        DeletePolicyRequest body, WriteAccessService access, DeletePolicyRepository repository,
        ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () =>
        {
            var policy = await access.ResolveAsync(code, entity, ct);
            if (!await repository.SetAsync(policy.Connection.Id, entity, body,
                ScopeSecurity.ActorId(user), ct))
                throw new ReadRejected(403, "delete_policy_denied");
            return Results.NoContent();
        });

    private static async Task<IResult> GetAdminOperation(string code, Guid id,
        ReadAccessRepository access, WriteLedger ledger, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () =>
        {
            var conn = await access.ConnectionAsync(code, ct);
            var status = await ledger.FindAsync(conn.Id, id, ct);
            return status is null ? ApiProblems.Create(http, 404, "write_not_found", "Operation not found.")
                : Results.Ok(status);
        });

    private static async Task<IResult> Reconcile(string code, Guid id,
        WriteReconcileRequest request, ReadAccessRepository access, WriteLedger ledger,
        ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
        await Guard(http, async () =>
        {
            var conn = await access.ConnectionAsync(code, ct);
            if (!await ledger.ReconcileAsync(conn.Id, id, ScopeSecurity.ActorId(user), request, ct))
                throw new ReadRejected(409, "operation_not_reconcilable");
            return Results.NoContent();
        });

    private static string Key(HttpContext http)
    {
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (!WriteLedger.ValidKey(key))
            throw new ReadRejected(400, "idempotency_key_required");
        return key;
    }

    private static IResult Response(HttpContext http, WriteResult result)
    {
        if (result.RetryAfterSeconds is int seconds)
            http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var status = result.State switch
        {
            "ambiguous" => 409,
            "rejected" => result.HttpStatus is >= 400 and <= 499 ? result.HttpStatus.Value : 502,
            "succeeded" => result.HttpStatus is >= 200 and <= 299 ? result.HttpStatus.Value : 200,
            _ => 409
        };
        return Results.Json(result, statusCode: status);
    }

    private static async Task<IResult> Guard(HttpContext http, Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ReadRejected ex) { return ApiProblems.Create(http, ex.Status, ex.Code, "Write request rejected."); }
        catch (ArgumentException) { return ApiProblems.Create(http, 400, "invalid_write_payload", "Invalid write payload."); }
        catch (HttpRequestException) { return ApiProblems.Create(http, 502, "dataverse_unavailable", "Dataverse unavailable."); }
        catch (TaskCanceledException) { return ApiProblems.Create(http, 504, "dataverse_timeout", "Dataverse timeout."); }
        catch (Microsoft.Identity.Client.MsalException)
        { return ApiProblems.Create(http, 502, "dataverse_identity", "Dataverse identity failed."); }
    }
}
