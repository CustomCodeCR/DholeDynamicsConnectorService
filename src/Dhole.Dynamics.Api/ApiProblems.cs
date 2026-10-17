namespace Dhole.Dynamics.Api;

public static class ApiProblems
{
    public static IResult Create(HttpContext http, int status, string code, string title) =>
        Results.Problem(
            statusCode: status,
            title: title,
            type: "about:blank",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["correlationId"] = http.TraceIdentifier
            });
}
