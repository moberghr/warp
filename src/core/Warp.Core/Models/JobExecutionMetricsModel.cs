namespace Warp.Core.Models;

/// <summary>
/// Per-job-TYPE and per-HANDLER execution metrics (§8.19 multi-app observability), read from the durable
/// <c>Statistic</c> aggregates (folded from the <c>jobstat:</c> / <c>jobstat-app:</c> counter family), so
/// they survive Job-row cleanup. Optionally scoped to a single executor application.
/// </summary>
public class JobExecutionMetricsModel
{
    /// <summary>Metrics grouped by job <c>Type</c> (assembly-qualified name).</summary>
    public IReadOnlyList<JobExecutionStatModel> ByType { get; init; } = [];

    /// <summary>Metrics grouped by <c>HandlerType</c> (routed-message handlers; jobs without a handler are absent).</summary>
    public IReadOnlyList<JobExecutionStatModel> ByHandler { get; init; } = [];
}

/// <summary>
/// Execution metrics for a single job type or handler. <see cref="ExecutedCount"/> = terminal executions
/// (succeeded + failed); <see cref="ErrorRate"/> = failed ÷ executed; latency comes from the duration-sum ÷
/// count aggregate. Percentiles are populated for the app-agnostic (unfiltered) read; they are 0 for a
/// per-application slice (the app family carries no latency histogram, to bound counter volume).
/// </summary>
public class JobExecutionStatModel
{
    /// <summary>The job <c>Type</c> or <c>HandlerType</c> assembly-qualified name this row aggregates.</summary>
    public string Identifier { get; init; } = string.Empty;

    public long ExecutedCount { get; init; }

    public long ErrorCount { get; init; }

    public double ErrorRate { get; init; }

    public double AvgDurationMs { get; init; }

    public double P95DurationMs { get; init; }

    public double P99DurationMs { get; init; }
}
