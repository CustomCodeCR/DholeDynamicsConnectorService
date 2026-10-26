using System.Security.Claims;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Microsoft.AspNetCore.RateLimiting;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Intentionally separate from OAuth M2M: the only bearer-free runtime
/// operation executes an enabled, pre-approved read template using a secret
/// supplied in X-Dhole-Api-Key. No anonymous Dataverse reads or writes.
/// </summary>
public static class SavedQueryApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapSavedQueryApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var admin=app.MapGroup("/api/admin/v1/connections/{code}/integration-keys")
            .WithGroupName("admin-v1").RequireAuthorization("SecurityAdmin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        admin.MapGet("",List);
        admin.MapPost("",Issue);
        admin.MapPut("/{keyId:guid}/access",UpdateAccess);
        admin.MapDelete("/{keyId:guid}",Revoke);
        app.MapPost("/api/v1/keyed/saved-queries/{name}/execute",Execute)
            .WithGroupName("v1")
            .AllowAnonymous()
            .RequireRateLimiting("AdminRate");
        return app;
    }

    private static Task<IResult> List(string code,SavedQueryApiKeyRepository repo,
        HttpContext http,CancellationToken ct) =>
        Guard(http,async()=>Results.Ok(await repo.ListAsync(code,ct)));

    private static Task<IResult> Issue(string code,SavedQueryApiKeyRequest request,
        SavedQueryApiKeyRepository repo,ClaimsPrincipal user,HttpContext http,CancellationToken ct) =>
        Guard(http,async()=>Results.Ok(await repo.IssueAsync(code,request,
            ScopeSecurity.ActorId(user),ct)));

    private static Task<IResult> UpdateAccess(string code,Guid keyId,
        SavedQueryApiKeyAccessUpdate request,SavedQueryApiKeyRepository repo,
        ClaimsPrincipal user,HttpContext http,CancellationToken ct) =>
        Guard(http,async()=>await repo.UpdateAccessAsync(code,keyId,request,
            ScopeSecurity.ActorId(user),ct) is { } updated
            ? Results.Ok(updated) : ApiProblems.Create(http,404,"integration_key_not_found",
                "Integration key not found or no longer active."));

    private static Task<IResult> Revoke(string code,Guid keyId,
        SavedQueryApiKeyRepository repo,ClaimsPrincipal user,HttpContext http,CancellationToken ct) =>
        Guard(http,async()=>await repo.RevokeAsync(code,keyId,ScopeSecurity.ActorId(user),ct)
            ? Results.NoContent() : ApiProblems.Create(http,404,"integration_key_not_found",
                "Integration key not found."));

    private static Task<IResult> Execute(string name,ExecuteSavedQueryRequest body,
        SavedQueryApiKeyRepository keys,SavedQueryRepository saved,
        ReadSearchService reader, HttpContext http,CancellationToken ct) =>
        Guard(http,async()=>
        {
            if(!ReadQueryCompiler.ValidParam(name) ||
               string.IsNullOrWhiteSpace(body.ConnectionCode) || body.Parameters is null)
                throw new ReadRejected(400,"invalid_saved_query_request");
            // Authentication + durable quota on every page (also continuations).
            // Do not accept credentials in query parameters, cookies or JSON.
            var identity=await keys.ValidateAndChargeAsync(
                http.Request.Headers["X-Dhole-Api-Key"].ToString(),
                body.ConnectionCode,name,ct);
            var template=await saved.FindAsync(identity.ConnectionId,name,ct);
            if(template is not { Enabled: true })
                throw new ReadRejected(404,"saved_query_not_found");
            ReadSpec? query=null;
            if(string.IsNullOrEmpty(body.Cursor))
                query=ReadSavedQueryBinder.Bind(template.Query,template.Parameters,body.Parameters);
            else if(body.Parameters.Count>0)
                throw new ReadRejected(400,"cursor_with_query");
            var result=await reader.SearchAsync(new ClaimsPrincipal(),template.EntityName,
                new ReadSearchRequest(body.ConnectionCode,query,body.Cursor),ct,
                savedName:name,apiKey:identity);
            return Results.Ok(result);
        });

    private static async Task<IResult> Guard(HttpContext http,Func<Task<IResult>> action)
    {
        try{return await action();}
        catch(ReadRejected e)
        {
            // The API key lookup must not disclose existence, expiry or scopes.
            return ApiProblems.Create(http,e.Status,e.Code,
                e.Status==401 ? "Invalid or expired integration credential."
                    : "Integration request rejected.");
        }
        catch(ArgumentException)
        {return ApiProblems.Create(http,400,"invalid_query","Invalid saved query parameters.");}
        catch(HttpRequestException)
        {return ApiProblems.Create(http,502,"dataverse_unavailable","Dataverse read failed.");}
        catch(TaskCanceledException)
        {return ApiProblems.Create(http,504,"dataverse_timeout","Dataverse request timed out.");}
        catch(JsonException)
        {return ApiProblems.Create(http,502,"dataverse_invalid_json","Dataverse returned invalid data.");}
        catch(Microsoft.Identity.Client.MsalException)
        {return ApiProblems.Create(http,502,"dataverse_identity","Dataverse identity unavailable.");}
    }
}
