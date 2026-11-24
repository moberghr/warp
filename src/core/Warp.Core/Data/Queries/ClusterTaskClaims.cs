namespace Warp.Core.Data.Queries;

/// <summary>What one claim statement returned (rule 6.23).</summary>
public sealed record ClusterTaskClaims(IReadOnlyList<ClusterTaskState> Tasks, bool LegacyServersAlive)
{
    /// <summary>
    /// Whether a server other than the claiming one is alive (heartbeat within the live window, on the
    /// database's clock). A lone server need not release the lease an idle run of a signal task leaves
    /// behind: no other server can claim it. Defaults to true, the answer that always releases.
    /// </summary>
    public bool OtherServersAlive { get; init; } = true;
}

/// <summary>
/// One named task after a claim: whether this server now holds it, and how long until it is next due,
/// measured on the database's clock (rule 6.23), so a server's own clock decides only how long it sleeps.
/// </summary>
public sealed record ClusterTaskState(string Name, bool Claimed, TimeSpan DueIn)
{
    /// <summary>
    /// Whether this claim took the task because its scheduled time had come, rather than only for a run
    /// request a signal left on the row. False when not claimed, and by default.
    /// </summary>
    public bool Scheduled { get; init; }
}

/// <summary>The end of one run of a cluster-wide task (rule 6.23).</summary>
public sealed record ClusterTaskCompletion(
    string Name,
    Guid ServerId,
    bool DidWork,
    bool Rerun,
    bool Record,
    string? Status,
    string? Message,
    double DurationMs);

/// <summary>One task a server registers at start (rule 6.23): its name and this server's interval for it.</summary>
public sealed record ClusterTaskRegistration(string Name, double IntervalSeconds);
