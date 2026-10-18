namespace Dhole.Dynamics.Workers;

/// <summary>Finite retry classifier. Never repeat unsafe POST/PATCH/DELETE; this applies only to safe GET jobs.</summary>
public static class JobRetryPolicy
{
    public static bool Retryable(int status) => status == 429 || status is >= 500 and <= 599;
    public static int DelaySeconds(int attempt, int? retryAfter, int jitterSeconds = 0)
    {
        var power = Math.Min(Math.Max(attempt - 1, 0), 7);
        var exponential = Math.Min(3600, 15 * (1 << power));
        return Math.Clamp(Math.Max(exponential + Math.Clamp(jitterSeconds, 0, 15),
            retryAfter ?? 0), 15, 3600);
    }
}
public static class WorkerCapabilities
{
    // Explicitly opt in with Jobs:Enabled and per-entity scheduling. Never on by default.
    public const bool DurableQueueSupported = true;
    public const bool ExternalSynchronizationEnabledByDefault = false;
}
