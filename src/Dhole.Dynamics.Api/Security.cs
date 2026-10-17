using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Dhole.Dynamics.Api;

public static class ScopeSecurity
{
    // Supports either repeated scope claims or a single space-separated / JSON-array claim.
    public static bool HasScope(ClaimsPrincipal principal, string required) =>
        principal.Identity?.IsAuthenticated == true &&
        principal.Claims
            .Where(c => c.Type is "scope" or "scopes" or "scp" or "permissions" or "permission")
            .SelectMany(c => c.Value.Split(
                [' ', ',', '[', ']', '"', '\n', '\r', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(scope => string.Equals(scope, required, StringComparison.OrdinalIgnoreCase));

    public static string ActorId(ClaimsPrincipal principal) =>
        principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? "unknown";
}

