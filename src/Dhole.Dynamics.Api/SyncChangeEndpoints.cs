using System.Security.Claims;

namespace Dhole.Dynamics.Api;

/// <summary>Incremental manifest for M2M consumers with explicit read grants, never raw Dataverse payloads.</summary>
public static class SyncChangeEndpoints
{
    public static IEndpointRouteBuilder MapSyncChangeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/sync/{entity}/changes",GetChanges)
            .WithGroupName("v1").RequireAuthorization("M2M");
        return app;
    }

    private static async Task<IResult> GetChanges(string entity,string connectionCode,
        long? after,int? limit,ReadAccessRepository access,
        DurableJobRepository queue,ClaimsPrincipal actor,HttpContext http,CancellationToken ct)
    {
        try
        {
            var policy=await access.ResolveAsync(connectionCode,entity,ct);
            // Connection, entity, primary GUID and M2M identity all checked and counted.
            await access.PermitAsync(actor,policy,[policy.Table.PrimaryIdAttribute],ct);
            return Results.Ok(await queue.ChangesAsync(policy.Connection.Id,entity,
                after??0,limit??50,ct));
        }
        catch(ReadRejected e)
        {
            return ApiProblems.Create(http,e.Status,e.Code,"Change feed access denied.");
        }
    }
}
