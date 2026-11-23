namespace Warp.Core.Data.Queries;

/// <summary>What one claim statement returned (rule 6.20).</summary>
public sealed record ClusterTaskClaims(IReadOnlyList<ClusterTaskState> Tasks, bool LegacyServersAlive);

/// <summary>One named task after a claim: whether this server now holds it, and when it is next due.</summary>
public sealed record ClusterTaskState(string Name, bool Claimed, DateTime DueAt);

/// <summary>The end of one run of a cluster-wide task (rule 6.20).</summary>
public sealed record ClusterTaskCompletion(
    string Name,
    Guid ServerId,
    DateTime Now,
    bool DidWork,
    bool Rerun,
    bool Record,
    string? Status,
    string? Message,
    double DurationMs);
