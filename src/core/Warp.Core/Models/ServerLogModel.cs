namespace Warp.Core.Models;

public class ServerTaskSummary
{
    public string TaskName { get; set; } = string.Empty;

    public string? LastStatus { get; set; }

    public string? LastMessage { get; set; }

    public DateTime? LastRun { get; set; }

    public double? LastDurationMs { get; set; }

    public double? IntervalSeconds { get; set; }

    /// <summary>A cluster-wide task (rule 6.23): one schedule for the whole cluster, run by one server per tick.</summary>
    public bool ClusterWide { get; set; }

    /// <summary>When a cluster-wide task is next due.</summary>
    public DateTime? NextRunAt { get; set; }

    /// <summary>The server that last recorded a run of a cluster-wide task.</summary>
    public Guid? LastServerId { get; set; }

    /// <summary>The server running a cluster-wide task right now, if any.</summary>
    public Guid? RunningOn { get; set; }
}

public class ServerLogModel
{
    public int Id { get; set; }

    public string TaskName { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? Message { get; set; }

    public DateTime Timestamp { get; set; }

    public double? DurationMs { get; set; }
}
