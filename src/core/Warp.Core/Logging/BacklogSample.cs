using System.Diagnostics.Metrics;

namespace Warp.Core.Logging;

/// <summary>
/// One per-queue backlog reading produced by the BacklogSampler server task and reported by the
/// <c>warp.job.queue.depth</c> / <c>warp.job.queue.oldest_age_seconds</c> ObservableGauges
/// (<see cref="WarpTelemetry.SetBacklogSnapshot"/>). Backlog is a queue-GLOBAL signal — an eligible-but-
/// unclaimed job has no executor yet, so it carries no application tag (contrast queue-wait, which IS
/// executor-attributed at claim). Only one sampler runs cluster-wide (a shared lock), so the snapshot is the
/// whole cluster's per-queue backlog regardless of which server produced it.
/// </summary>
public sealed record BacklogSample(string Queue, long Depth, double OldestAgeSeconds)
{
    /// <summary>Meter tags for this sample: queue only (backlog is not application-attributable).</summary>
    public KeyValuePair<string, object?>[] Tags() =>
        [new(WarpTelemetryAttributes.QueueMeterQueue, Queue)];
}
