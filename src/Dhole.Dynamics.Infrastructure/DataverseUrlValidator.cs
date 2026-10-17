namespace Dhole.Dynamics.Infrastructure;

public static class DataverseUrlValidator
{
    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || !uri.Host.EndsWith(".dynamics.com", StringComparison.OrdinalIgnoreCase))
            return false;
        normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }
}
