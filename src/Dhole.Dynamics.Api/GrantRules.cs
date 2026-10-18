using System.Security.Claims;

namespace Dhole.Dynamics.Api;

/// <summary>Independent, deterministic tests for strict M2M scope + field subset authorization.</summary>
public static class GrantRules
{
    public static bool IsAllowed(ClaimsPrincipal principal, string requiredScope,
        IReadOnlyCollection<string> grantedFields, IReadOnlyCollection<string> requestedFields)
    {
        if (!ScopeSecurity.HasScope(principal, requiredScope)) return false;
        if (grantedFields.Count == 0 || grantedFields.Any(field => field == "*" || !ValidField(field)))
            return false;
        return requestedFields.All(field => grantedFields.Contains(field, StringComparer.Ordinal));
    }

    public static bool ValidField(string? field) =>
        !string.IsNullOrWhiteSpace(field) && field.Length <= 100 &&
        (char.IsLetter(field[0]) || field[0] == '_') &&
        field.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
            || (c >= '0' && c <= '9') || c == '_' || c == '@' || c == '.');

    public static bool ValidGrant(string entitySet, string operation, string[] fields, int quota) =>
        Dhole.Dynamics.Infrastructure.DataverseMetadataDiscovery.SafeName(entitySet) &&
        operation is ("create" or "read" or "update" or "delete" or "upsert" or "action") &&
        quota is >= 1 and <= 600 && fields.Length is > 0 and <= 80 &&
        fields.Distinct(StringComparer.Ordinal).Count() == fields.Length &&
        fields.All(ValidField);
}
