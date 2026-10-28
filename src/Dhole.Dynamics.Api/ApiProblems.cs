namespace Dhole.Dynamics.Api;

public static class ApiProblems
{
    // Only return validated schema identifiers. Never echo the searched
    // customer/IDTRA value or the API key into an HTTP error.
    public static IResult Create(HttpContext http, int status, string code, string title,
        string? lookupInput = null, string? lookupSearchField = null)
    {
        static bool SafeField(string? value) =>
            value is { Length: > 0 and <= 80 } &&
            char.IsLetter(value[0]) &&
            value.All(c => char.IsLetterOrDigit(c) || c == '_');
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["correlationId"] = http.TraceIdentifier
        };
        if (code is "lookup_name_not_found" or "lookup_name_ambiguous" or
            "quote_customer_required" or "quote_text_exceeds_dataverse_limit" or "client_denied")
        {
            if (SafeField(lookupInput)) extensions["lookupInput"] = lookupInput;
            if (SafeField(lookupSearchField)) extensions["lookupSearchField"] = lookupSearchField;
        }
        return Results.Problem(statusCode: status, title: title,
            type: "about:blank", extensions: extensions);
    }
}
