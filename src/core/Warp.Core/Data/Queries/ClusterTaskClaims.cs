namespace Warp.Core.Data.Queries;

/// <summary>What one claim statement returned (rule 6.23).</summary>
public sealed record ClusterTaskClaims(IReadOnlyList<ClusterTaskState> Tasks, bool LegacyServersAlive);

/// <summary>
/// One named task after a claim: whether this server now holds it, and how long until it is next due,
/// measured on the database's clock (rule 6.23), so a server's own clock decides only how long it sleeps.
/// </summary>
public sealed record ClusterTaskState(string Name, bool Claimed, TimeSpan DueIn);

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
