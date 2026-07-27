namespace Warp.Core.Models;

/// <summary>
/// Everything for one trace id (§8.28), unioned from the rows Warp already persists — the browser request
/// (<c>ClientEventLog</c>), the server endpoint call (<c>EndpointCallLog</c>), the jobs (<c>Job</c>), and the
/// outbound calls those jobs made (<c>AdapterCallLog</c>). Each is already a span (trace id + start + duration
/// + status); no separate span store. Powers the single-screen trace view.
/// </summary>
public class TraceOverviewModel
{
    public Guid TraceId { get; init; }

    /// <summary>Every span for the trace, ordered by start time.</summary>
    public IReadOnlyList<TraceSpanModel> Spans { get; init; } = [];

    public int JobCount { get; init; }

    public int EndpointCount { get; init; }

    public int AdapterCount { get; init; }

    public int ClientCount { get; init; }

    public int ErrorCount { get; init; }
}

/// <summary>
/// One span on the unified trace view. <see cref="Source"/> discriminates the origin row
/// (<c>client</c>/<c>endpoint</c>/<c>job</c>/<c>adapter</c>); <see cref="ParentId"/> links a job to its
/// spawning job (<c>SpawnedByJobId</c>) for the DAG. <see cref="DurationMs"/> is null when the source row
/// doesn't record execution duration (jobs — see spec).
/// </summary>
public class TraceSpanModel
{
    public string Source { get; init; } = string.Empty;

    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public DateTime StartTime { get; init; }

    public double? DurationMs { get; init; }

    public string Status { get; init; } = string.Empty;

    /// <summary>True when this span's status represents a failure (drives error highlighting).</summary>
    public bool IsError { get; init; }

    public Guid? ParentId { get; init; }
}
