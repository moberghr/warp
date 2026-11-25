namespace Warp.Core.Models;

public class ServerModel
{
    public Guid Id { get; set; }

    public string ServerName { get; set; } = string.Empty;

    public DateTime StartedTime { get; set; }

    public DateTime LastHeartbeatTime { get; set; }

    public int ServiceCount { get; set; }

    public double? CpuUsagePercent { get; set; }

    /// <summary>The database clock minus this server's clock, in ms, from its last heartbeat (#350).</summary>
    public double? ClockOffsetMs { get; set; }

    public long? MemoryWorkingSetBytes { get; set; }

    public DateTime? PausedAt { get; set; }

    public List<WorkerModel> Workers { get; set; } = [];
}

public class WorkerModel
{
    public Guid WorkerId { get; set; }

    public DateTime StartedTime { get; set; }

    public DateTime? LastHeartbeatTime { get; set; }

    public Guid? CurrentJobId { get; set; }

    public string? CurrentJobType { get; set; }

    public string? Queues { get; set; }

    public double? PollingIntervalMs { get; set; }

    public Guid? WorkerGroupId { get; set; }

    public DateTime? WorkerGroupPausedAt { get; set; }
}
