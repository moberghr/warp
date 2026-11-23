namespace Warp.Core.Data.Entities;

public class Server
{
    public required Guid Id { get; set; }

    public string ServerName { get; set; } = System.Environment.MachineName;

    /// <summary>Opt-in logical application name (<c>WarpConfiguration.ApplicationName</c>), stamped at registration. Null ⇒ feature off.</summary>
    public string? Application { get; set; }

    /// <summary>Opt-in self-reported build/assembly version, stamped at registration. Per-instance (mixed values during a rolling deploy).</summary>
    public string? Version { get; set; }

    /// <summary>Opt-in self-reported environment (prod/staging/…), stamped at registration.</summary>
    public string? Environment { get; set; }

    public required DateTime StartedTime { get; set; }

    public required DateTime LastHeartbeatTime { get; set; }

    public int ServiceCount { get; set; }

    /// <summary>
    /// How this server coordinates cluster-wide server tasks. Null: a server from before the lease (it
    /// takes each task's lock every interval); while one is alive, newer servers also take that lock
    /// around each run, so a task never runs on an old and a new server at once during a deploy.
    /// </summary>
    public int? CoordinationVersion { get; set; }

    public double? CpuUsagePercent { get; set; }

    public long? MemoryWorkingSetBytes { get; set; }

    public DateTime? PausedAt { get; set; }
}
