using System.Text.Json;
using Dhole.Dynamics.Api;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dhole.Dynamics.UnitTests;

public sealed class F6DynamicsRealtimeTests
{
    private static IConfiguration Flags(bool approved)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Features:UniversalRealtimeEnabled"]="true",
            ["Realtime:Outbox:Enabled"]="true",
            ["Realtime:BatchApproved"]=approved.ToString(),
            ["Realtime:ReleaseId"]=DynamicsRealtimeJobEvent.ReleaseId
        }).Build();

    [Fact]
    public void Batch_flags_fail_closed_unless_exact_release_is_approved()
    {
        Assert.False(DynamicsRealtimeJobEvent.IsEnabled(null));
        Assert.False(DynamicsRealtimeJobEvent.IsEnabled(Flags(false)));
        var flags=Flags(true);
        Assert.True(DynamicsRealtimeJobEvent.IsEnabled(flags));
        flags["Realtime:ReleaseId"]="other";
        Assert.False(DynamicsRealtimeJobEvent.IsEnabled(flags));
        flags["Realtime:ReleaseId"]=DynamicsRealtimeJobEvent.ReleaseId;
        flags["Realtime:Outbox:Enabled"]="false";
        Assert.False(DynamicsRealtimeJobEvent.IsEnabled(flags));
    }

    [Fact]
    public void Job_notification_is_identity_only_and_never_exposes_tenant_credentials()
    {
        var eventId=Guid.NewGuid();
        var job=Guid.NewGuid();
        var json=DynamicsRealtimeJobEvent.Serialize(eventId,job,
            DynamicsRealtimeJobEvent.NextVersionHint(),DateTimeOffset.UtcNow,"ci-f6");
        using var doc=JsonDocument.Parse(json);
        var root=doc.RootElement;
        Assert.Equal(DynamicsRealtimeJobEvent.EventType,root.GetProperty("eventType").GetString());
        Assert.Equal($"dynamics:job:{job:D}",root.GetProperty("audienceRef").GetString());
        Assert.Equal(new[]{"changedFields"},
            root.GetProperty("payload").EnumerateObject().Select(x=>x.Name));
        foreach(var forbidden in new[]{"connectionId","tenantId","clientSecret","clientName","payloadUrl","accessToken","dataverseUrl"})
            Assert.DoesNotContain(forbidden,json,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_resource_ID_and_invalid_revision_are_rejected()
    {
        var id=Guid.NewGuid();
        Assert.Throws<ArgumentException>(() =>
            DynamicsRealtimeJobEvent.Serialize(id,Guid.Empty,1,DateTimeOffset.UtcNow,"ci"));
        Assert.Throws<ArgumentException>(() =>
            DynamicsRealtimeJobEvent.Serialize(id,id,0,DateTimeOffset.UtcNow,"ci"));
        Assert.True(DynamicsRealtimeJobEvent.NextVersionHint() > 0);
    }
}
