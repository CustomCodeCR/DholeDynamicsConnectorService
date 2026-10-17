using System.Security.Claims;

namespace Dhole.Dynamics.Api;

public static class ScopeSecurity
{
    // Claims are never sufficient by themselves: ClientAccessRepository additionally
    // requires an enabled registration and a connection+entity+operation+field grant.
    public static bool HasScope(ClaimsPrincipal user, string required) =>
        user.Identity?.IsAuthenticated == true &&
        user.Claims.Where(x => x.Type is "roles" or "role" or "scp" or "scope")
            .SelectMany(x => x.Value.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Any(x => string.Equals(x, required, StringComparison.Ordinal));

    // Entra application identity. Do NOT use oid/sub as a substitute for appid/azp.
    public static string? ClientId(ClaimsPrincipal user) =>
        user.FindFirst("azp")?.Value ?? user.FindFirst("appid")?.Value
        ?? user.FindFirst("client_id")?.Value;

    public static string? Issuer(ClaimsPrincipal user) =>
        (user.Identity as System.IdentityModel.Tokens.Jwt.JwtSecurityToken)?.Issuer
        ?? user.FindFirst("iss")?.Value
        ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/issuer")?.Value
        ?? user.Claims.FirstOrDefault(c => c.Type == "iss")?.Value;

    public static string ActorId(ClaimsPrincipal user) =>
        user.FindFirst("oid")?.Value ?? user.FindFirst("sub")?.Value ?? "unknown";

    public static bool HasAdminRole(ClaimsPrincipal user, string role) =>
        user.Identity?.IsAuthenticated == true &&
        user.Claims.Any(x => (x.Type is "roles" or "role" or ClaimTypes.Role)
            && string.Equals(x.Value, role, StringComparison.Ordinal));
}
