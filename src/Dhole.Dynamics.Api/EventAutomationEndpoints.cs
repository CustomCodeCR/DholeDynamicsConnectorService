using System.Security.Claims;
using Microsoft.AspNetCore.RateLimiting;

namespace Dhole.Dynamics.Api;

/// <summary>Admin-only automation blueprint management. No runtime execution or activation route.</summary>
public static class EventAutomationEndpoints
{
    public static IEndpointRouteBuilder MapEventAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        var routes=app.MapGroup("/api/admin/v1/connections/{code}/event-automations")
            .WithGroupName("admin-v1")
            .RequireAuthorization("SecurityAdmin")
            .RequireRateLimiting("AdminRate").RequireCsrfForWrites();
        routes.MapGet("",List);
        routes.MapPost("",Save);
        routes.MapGet("/pricing-fields",()=>Results.Ok(EventAutomationRepository.PricingFields));
        return app;
    }

    private static async Task<IResult> List(string code,EventAutomationRepository repo,
        HttpContext http,CancellationToken ct)
    {
        try{return Results.Ok(await repo.ListAsync(code,ct));}
        catch(ReadRejected e)
        {return ApiProblems.Create(http,e.Status,e.Code,"Automation request rejected.");}
    }

    private static async Task<IResult> Save(string code,AutomationRuleRequest request,
        EventAutomationRepository repo,ClaimsPrincipal actor,HttpContext http,
        CancellationToken ct)
    {
        try{return Results.Ok(await repo.SaveDraftAsync(code,request,
            ScopeSecurity.ActorId(actor),ct));}
        catch(ReadRejected e)
        {return ApiProblems.Create(http,e.Status,e.Code,"Automation draft rejected.");}
    }
}
