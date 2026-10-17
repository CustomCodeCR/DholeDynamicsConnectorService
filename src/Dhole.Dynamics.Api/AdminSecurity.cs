using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Dhole.Dynamics.Api;

public static class AdminSecurity
{
    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/login", (IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationOptions> auth,
            IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(config["AdminOidc:Authority"])
                || string.IsNullOrWhiteSpace(config["AdminOidc:ClientId"])
                || string.IsNullOrWhiteSpace(config["AdminOidc:ClientSecret"]))
                return Results.Problem(statusCode: 503, title: "Admin OIDC has not been configured.");
            return Results.Challenge(new Microsoft.AspNetCore.Authentication.AuthenticationProperties
            {
                RedirectUri = "/"
            }, [ServiceRegistration.AdminOidc]);
        }).AllowAnonymous();

        app.MapGet("/auth/session", (HttpContext ctx, IAntiforgery antiforgery) =>
        {
            var token = antiforgery.GetAndStoreTokens(ctx).RequestToken;
            return Results.Ok(new
            {
                authenticated = true,
                actor = ScopeSecurity.ActorId(ctx.User),
                securityAdmin = ScopeSecurity.HasAdminRole(ctx.User, "dynamics.security.admin"),
                csrfToken = token
            });
        }).RequireAuthorization("Admin");

        app.MapPost("/auth/logout", async (HttpContext ctx, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(ctx)) return Results.StatusCode(400);
            await ctx.SignOutAsync(ServiceRegistration.AdminCookie);
            return Results.NoContent();
        }).RequireAuthorization("Admin");
        return app;
    }

    public static RouteGroupBuilder RequireCsrfForWrites(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            if (HttpMethods.IsPost(http.Request.Method) || HttpMethods.IsPut(http.Request.Method)
                || HttpMethods.IsPatch(http.Request.Method) || HttpMethods.IsDelete(http.Request.Method))
            {
                var anti = http.RequestServices.GetRequiredService<IAntiforgery>();
                if (!await anti.IsRequestValidAsync(http))
                    return ApiProblems.Create(http, 400, "csrf_invalid", "Antiforgery token is required.");
            }
            return await next(invocation);
        });
        return group;
    }
}
