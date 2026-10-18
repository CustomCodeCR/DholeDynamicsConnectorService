using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Dhole.Dynamics.Api;

/// <summary>
/// Final, independent safety fence before ANY Dataverse write is dispatched.
/// Production is read-only until out-of-band, explicit operator approvals are applied.
/// Approval reference must be a nonempty change-control ticket, not simply a boolean.
/// </summary>
public sealed class ProductionWritePolicy
{
    public bool IsProduction { get; }
    public bool WritesApproved { get; }
    public bool DeletesApproved { get; }

    public ProductionWritePolicy(bool production, bool writeApproval, bool deleteApproval)
    {
        IsProduction=production;
        WritesApproved=!production || writeApproval;
        DeletesApproved=!production || (writeApproval && deleteApproval);
    }

    public static ProductionWritePolicy FromConfiguration(IConfiguration configuration,IHostEnvironment environment)
    {
        static bool ValidReference(string? value) =>
            value is { Length: >= 12 and <= 120 } &&
            value.All(c=>char.IsLetterOrDigit(c)||c is '-' or '_' or '.' or '/');
        var writes=configuration["ProductionWrites:Approved"]=="YES" &&
            ValidReference(configuration["ProductionWrites:ApprovalReference"]);
        var deletes=configuration["ProductionWrites:DeletesApproved"]=="YES" &&
            ValidReference(configuration["ProductionWrites:DeletesApprovalReference"]);
        return new(environment.IsProduction(),writes,deletes);
    }

    public void Demand(string operation)
    {
        if(!IsProduction)return;
        if(!WritesApproved)throw new ReadRejected(403,"production_writes_not_approved");
        if(operation=="delete" && !DeletesApproved)
            throw new ReadRejected(403,"production_deletes_not_approved");
    }

    public bool IsBlocked(HttpRequest request)
    {
        if(!IsProduction)return false;
        if(HttpMethods.IsGet(request.Method)||HttpMethods.IsHead(request.Method)||
            HttpMethods.IsOptions(request.Method))return false;
        var path=request.Path;
        if(path.StartsWithSegments("/api/v1"))
            return !WritesApproved || (HttpMethods.IsDelete(request.Method) && !DeletesApproved);
        if(path.StartsWithSegments("/api/admin/v1/console/records"))
            return !WritesApproved || (HttpMethods.IsDelete(request.Method) && !DeletesApproved);
        return false;
    }
}

/// <summary>Fail-closed before any idempotency reservation or quote-link mutation.</summary>
public sealed class ProductionWriteGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http,ProductionWritePolicy policy)
    {
        if(policy.IsBlocked(http.Request))
        {
            var code=HttpMethods.IsDelete(http.Request.Method) && policy.WritesApproved
                ? "production_deletes_not_approved":"production_writes_not_approved";
            await ApiProblems.Create(http,403,code,"Production operation requires approved change control.")
                .ExecuteAsync(http);
            return;
        }
        await next(http);
    }
}
