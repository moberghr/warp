namespace Warp.Core.Data.Entities;

/// <summary>
/// One row per running NON-server Warp process (publisher-only / API-only / dashboard-only —
/// an <c>AddWarp</c> process that never calls <c>AddWarpServer</c>). Server processes are their own
/// instance record on <c>Server</c> (which gains <c>Application</c>/<c>Version</c>/<c>Environment</c>) —
/// "every server is an application instance, not every instance is a server", so each process writes
/// exactly one physical row. Written only when <c>WarpConfiguration.ApplicationName</c> is set; the
/// lightweight heartbeat host registers → heartbeats CPU/RAM → deregisters on graceful shutdown, and
/// <c>ExpirationCleanup</c> sweeps rows stale past <c>ApplicationInstanceStaleGrace</c>.
/// </summary>
public class ApplicationInstance
{
    public required Guid Id { get; set; }

    public required string ApplicationName { get; set; }

    public string MachineName { get; set; } = System.Environment.MachineName;

    public required DateTime StartedAt { get; set; }

    public required DateTime LastHeartbeatAt { get; set; }

    public double? CpuUsagePercent { get; set; }

    public long? MemoryWorkingSetBytes { get; set; }

    public string? Version { get; set; }

    public string? Environment { get; set; }
}
