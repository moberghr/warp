namespace Warp.Core.Models;

/// <summary>
/// Per-queue SLIs (§8.26): queue-wait latency (from the durable <c>qwait:</c> / <c>qwait-app:</c> fold, so it
/// survives Job-row cleanup) merged with the latest backlog gauge (<c>qbacklog:</c> Statistic upserted by the
/// BacklogSampler). Optionally scoped to a single executor application.
/// </summary>
public class QueueMetricsModel
{
    public IReadOnlyList<QueueMetricModel> Queues { get; init; } = [];
}

/// <summary>
/// Queue-wait + backlog for a single queue. Wait latency = time a job spent eligible-but-unclaimed
/// (claim − ScheduleTime); percentiles are populated for the app-agnostic read and 0 for a per-application
/// slice (the app family carries no histogram, to bound counter volume). Backlog is the most recent sample.
/// </summary>
public class QueueMetricModel
{
    public string Queue { get; init; } = string.Empty;

    /// <summary>Number of claims observed (the queue-wait sample count).</summary>
    public long ClaimedCount { get; init; }

    public double AvgWaitMs { get; init; }

    public double P95WaitMs { get; init; }

    public double P99WaitMs { get; init; }

    /// <summary>Latest sampled count of eligible (Enqueued, ScheduleTime ≤ now) jobs waiting on this queue.</summary>
    public long BacklogDepth { get; init; }

    /// <summary>Latest sampled age (seconds) of the oldest eligible job on this queue.</summary>
    public long OldestAgeSeconds { get; init; }
}
