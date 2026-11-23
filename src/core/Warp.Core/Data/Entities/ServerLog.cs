namespace Warp.Core.Data.Entities;

public class ServerLog
{
    public int Id { get; set; }

    public Guid ServerId { get; set; }

    public int? ServerTaskId { get; set; }

    public ServerTask? ServerTask { get; set; }

    /// <summary>The task of a cluster-wide run (rule 6.20), which has no per-server <see cref="ServerTask"/> row.</summary>
    public string? TaskName { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? Message { get; set; }

    public DateTime Timestamp { get; set; }

    public double? DurationMs { get; set; }
}
