namespace Dhole.Dynamics.Workers;

/// <summary>Workers will be activated only when durable queues and permissions exist (Phase 11).</summary>
public static class WorkerCapabilities
{
    public const bool ExternalSynchronizationEnabled = false;
}
