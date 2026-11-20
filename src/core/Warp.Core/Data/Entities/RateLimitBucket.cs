namespace Warp.Core.Data.Entities;

public class RateLimitBucket
{
    public string Name { get; set; } = string.Empty;

    public DateTime WindowStartUtc { get; set; }

    public int CurrentCount { get; set; }

    public string? TimestampsJson { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// The first Wait-mode reschedule slot not yet handed out, cluster-wide. Throttled jobs are spread
    /// across successive slots instead of all being rescheduled onto the instant the key reopens, so a
    /// backlog drains at the limit's rate instead of re-executing in full at every boundary. Null until
    /// the key first throttles a Wait-mode job.
    /// </summary>
    public DateTime? NextSlotUtc { get; set; }
}
