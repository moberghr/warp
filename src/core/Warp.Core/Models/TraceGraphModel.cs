namespace Warp.Core.Models;

/// <summary>
/// The jobs of one trace for the dashboard's job graph, capped at <see cref="MaxJobs"/>.
/// </summary>
/// <remarks>
/// A batch of 100,000 jobs shares one trace id, and the dashboard lays every job out as a graph node,
/// so an uncapped read froze the browser. The graph shows the earliest jobs, which include the root.
/// </remarks>
public class TraceGraphModel
{
    /// <summary>
    /// The most jobs the graph returns. Matches the unified trace view's per-source cap.
    /// </summary>
    public const int MaxJobs = 500;

    /// <summary>
    /// The trace's earliest jobs, oldest first, at most <see cref="MaxJobs"/>.
    /// </summary>
    public List<TraceJobModel> Jobs { get; set; } = [];

    /// <summary>
    /// Whether the trace has more jobs than <see cref="Jobs"/> holds.
    /// </summary>
    public bool IsTruncated { get; set; }
}
