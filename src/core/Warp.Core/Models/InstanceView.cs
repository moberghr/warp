namespace Warp.Core.Models;

/// <summary>
/// Unified projection of a single running Warp process — server or non-server — for the Applications
/// view (§8.19 multi-app observability). "Every server is an application instance; not every instance
/// is a server": server processes are their own record on <c>Server</c> (<see cref="IsServer"/> true),
/// non-server processes (publisher/API/dashboard-only) write an <c>ApplicationInstance</c> row
/// (<see cref="IsServer"/> false). The query service reads both tables and merges them into this one
/// shape so the dashboard renders a single roster.
/// </summary>
public sealed class InstanceView
{
    /// <summary>The instance's primary key — a <c>Server.Id</c> when <see cref="IsServer"/>, else an <c>ApplicationInstance.Id</c>.</summary>
    public Guid Id { get; init; }

    public string Application { get; init; } = string.Empty;

    public string MachineName { get; init; } = string.Empty;

    public DateTime StartedAt { get; init; }

    public DateTime LastHeartbeatAt { get; init; }

    public double? CpuUsagePercent { get; init; }

    public long? MemoryWorkingSetBytes { get; init; }

    /// <summary>True for a <c>Server</c> row (job worker / server-task host); false for a non-server <c>ApplicationInstance</c>.</summary>
    public bool IsServer { get; init; }

    public string? Version { get; init; }

    public string? Environment { get; init; }

    /// <summary>
    /// True when <see cref="LastHeartbeatAt"/> is within the liveness window of "now". The window is
    /// <c>WarpConfiguration.ApplicationInstanceStaleGrace</c> for both server and non-server instances
    /// (a single grace for simplicity — servers heartbeat faster but the same generous window still
    /// classifies a healthy server as live).
    /// </summary>
    public bool IsLive { get; init; }
}
