using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dhole.Dynamics.Contracts;
using Dhole.Dynamics.Infrastructure;

namespace Dhole.Dynamics.Api;

/// <summary>Bounded, read-only Dataverse change feed. Persist GUIDs/hashes only, never row content.</summary>
public sealed class DataverseDeltaReader(HttpClient http,IDataverseAccessTokenProvider tokens)
{
    public static Uri ValidatedLink(ReadPolicy policy,string raw)
    {
        if(!DataverseUrlValidator.TryNormalize(policy.Connection.DataverseUrl,out var origin) ||
            raw.Length is < 1 or > 4096)
            throw new ReadRejected(400,"unsafe_delta_url");
        var baseUri=new Uri(origin,UriKind.Absolute);
        // A root-relative /api path must be resolved against the trusted HTTPS origin.
        // UriKind.Absolute can interpret slash-prefixed strings as file: URIs.
        Uri uri;
        if(raw.StartsWith("/",StringComparison.Ordinal))
            uri=new Uri(baseUri,raw);
        else if(!Uri.TryCreate(raw,UriKind.Absolute,out uri))
            throw new ReadRejected(400,"unsafe_delta_url");
        var path="/api/data/v9.2/"+policy.Table.EntitySetName;
        if(uri.Scheme!="https" || uri.UserInfo.Length>0 || uri.Fragment.Length>0 ||
            !uri.IsDefaultPort || !string.Equals(uri.Host,baseUri.Host,StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath!=path ||
            string.IsNullOrEmpty(uri.Query) || uri.Query.Length>3800)
            throw new ReadRejected(400,"unsafe_delta_url");
        var allowed=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "$select","$top","$filter","$orderby","$skiptoken","$deltatoken" };
        var keys=uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries);
        if(keys.Length==0 || keys.Length>10 || keys.Any(pair=>
            !allowed.Contains(Uri.UnescapeDataString(pair.Split('=')[0]))))
            throw new ReadRejected(400,"unsafe_delta_url");
        return uri;
    }

    public async Task<SyncPage> ReadAsync(ReadPolicy policy,string mode,
        string? next,string? delta,DateTimeOffset? watermark,CancellationToken ct)
    {
        if(mode is not ("delta" or "watermark") ||
           !policy.Allowed.Contains(policy.Table.PrimaryIdAttribute) ||
           mode=="watermark" && !policy.Allowed.Contains("modifiedon"))
            throw new ReadRejected(403,"sync_fields_denied");

        var initial="/api/data/v9.2/"+policy.Table.EntitySetName;
        var select=policy.Table.PrimaryIdAttribute+(mode=="watermark"?",modifiedon":"");
        var query="?$select="+Uri.EscapeDataString(select)+"&$top=100";
        if(mode=="watermark")
        {
            var after=(watermark??DateTimeOffset.UtcNow.AddDays(-7)).ToUniversalTime();
            query+="&$filter="+Uri.EscapeDataString("modifiedon ge "+
                after.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'",System.Globalization.CultureInfo.InvariantCulture));
            query+="&$orderby="+Uri.EscapeDataString("modifiedon asc,"+policy.Table.PrimaryIdAttribute+" asc");
        }
        var uri=ValidatedLink(policy,next??(mode=="delta"?delta:null)??initial+query);
        using var request=new HttpRequestMessage(HttpMethod.Get,uri);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",
            await tokens.AcquireAsync(policy.Connection,ct));
        request.Headers.TryAddWithoutValidation("OData-Version","4.0");
        request.Headers.TryAddWithoutValidation("OData-MaxVersion","4.0");
        request.Headers.TryAddWithoutValidation("Prefer","odata.maxpagesize=100");
        if(mode=="delta")request.Headers.TryAddWithoutValidation("Prefer","odata.track-changes");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode)
        {
            var retry=response.Headers.RetryAfter?.Delta is TimeSpan delay
                ? (int?)Math.Clamp(Math.Ceiling(delay.TotalSeconds),1,3600):null;
            if(response.Headers.RetryAfter?.Date is DateTimeOffset date)
                retry=(int)Math.Clamp(Math.Ceiling((date-DateTimeOffset.UtcNow).TotalSeconds),1,3600);
            throw new SyncRequestFailure((int)response.StatusCode,retry);
        }
        await response.Content.LoadIntoBufferAsync(256*1024,ct);
        using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if(body.RootElement.ValueKind!=JsonValueKind.Object ||
           !body.RootElement.TryGetProperty("value",out var records) ||
           records.ValueKind!=JsonValueKind.Array || records.GetArrayLength()>100)
            throw new ReadRejected(502,"invalid_dataverse_delta_page");
        var changes=new List<SyncChange>();
        foreach(var record in records.EnumerateArray())
        {
            if(record.ValueKind!=JsonValueKind.Object ||
                !record.TryGetProperty(policy.Table.PrimaryIdAttribute,out var identity) ||
                identity.ValueKind!=JsonValueKind.String ||
                !Guid.TryParse(identity.GetString(),out var id) || id==Guid.Empty)
                throw new ReadRejected(502,"invalid_dataverse_delta_record");
            var removed=record.TryGetProperty("@removed",out var tombstone) &&
                tombstone.ValueKind==JsonValueKind.Object;
            var revision=record.TryGetProperty("@odata.etag",out var etag) &&
                etag.ValueKind==JsonValueKind.String ? etag.GetString()! :
                record.TryGetProperty("modifiedon",out var modified) &&
                modified.ValueKind==JsonValueKind.String ? modified.GetString()! :
                "no-etag";
            var hash=Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(id.ToString("D")+"|"+revision+"|"+(removed?"delete":"upsert"))));
            changes.Add(new(id,hash,removed?"delete":"upsert"));
        }
        var nextLink=GetLink(body.RootElement,"@odata.nextLink",policy);
        var deltaLink=GetLink(body.RootElement,"@odata.deltaLink",policy);
        if(nextLink is not null && deltaLink is not null ||
            mode=="delta" && nextLink is null && deltaLink is null)
            throw new ReadRejected(502,"invalid_delta_continuation");
        return new(changes,nextLink,deltaLink);
    }

    private static string? GetLink(JsonElement root,string name,ReadPolicy policy)
    {
        if(!root.TryGetProperty(name,out var value))return null;
        if(value.ValueKind!=JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new ReadRejected(502,"invalid_delta_link");
        var raw=value.GetString()!;
        ValidatedLink(policy,raw);
        return raw;
    }
}
