namespace Warp.Core.Data.Entities;

/// <summary>
/// One cluster-wide server task (one with a lock key): its schedule and its last run, shared by every
/// server. A server runs the task only while it holds the row's lease, taken by one statement for every
/// due task, so the cluster runs each task once per tick instead of every server attempting a lock.
/// Per-server tasks (the heartbeat) keep their <see cref="ServerTask"/> rows.
/// </summary>
public class ClusterTask
{
    /// <summary>The task's <c>IServerTask.Name</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Seconds between runs. Null disables the task (the dashboard can set it).</summary>
    public double? IntervalSeconds { get; set; }

    /// <summary>
    /// The most an idle task's next run is pushed out, in seconds: each run that found nothing doubles
    /// the wait from <see cref="IntervalSeconds"/> up to this. Null or not above the interval: no backoff.
    /// </summary>
    public double? IdleBackoffLimitSeconds { get; set; }

    /// <summary>Consecutive runs that found nothing; drives the idle backoff. Reset by a run that did work or by a signal.</summary>
    public int IdleStreak { get; set; }

    /// <summary>When the task is next due.</summary>
    public DateTime NextRunAt { get; set; }

    /// <summary>The current claim's expiry. A server holds the task until then; null when free.</summary>
    public DateTime? LeaseUntil { get; set; }

    /// <summary>The server holding the lease.</summary>
    public Guid? ClaimedBy { get; set; }

    /// <summary>When the current lease was taken.</summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>A signal asked for a run while another server held the lease; the next claim runs it at once.</summary>
    public DateTime? RunRequestedAt { get; set; }

    public DateTime? LastRunAt { get; set; }

    public Guid? LastServerId { get; set; }

    public string? LastStatus { get; set; }

    public string? LastMessage { get; set; }

    public double? LastDurationMs { get; set; }
}
