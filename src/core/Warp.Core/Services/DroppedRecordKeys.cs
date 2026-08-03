using Warp.Core.Logging;

namespace Warp.Core.Services;

/// <summary>
/// Counter/Statistic keys for records dropped by the lossy pipelines (§8.19/§8.21/§8.27). Disjoint first-segment
/// namespace <c>warpsys</c> (first-segment-equality parsers reject it, §8.6/§8.19). Only a tiered history is
/// written — <c>warpsys:records-dropped:{pipeline}:{tier}:{stamp}</c> — so the dashboard can show "dropped in the
/// last N hours" (which returns to zero as buckets age out) rather than a sticky lifetime total, and it rolls up
/// through the generic <see cref="MetricTiers"/> reader/rollup like every other tiered series.
/// </summary>
public static class DroppedRecordKeys
{
    public const string Prefix = "warpsys:records-dropped";

    public static string Token(DropPipeline pipeline) => pipeline switch
    {
        DropPipeline.Adapter => "adapter",
        DropPipeline.Endpoint => "endpoint",
        DropPipeline.Client => "client",
        _ => "unknown",
    };

    /// <summary>The base key for a pipeline (no tier/stamp): <c>warpsys:records-dropped:{pipeline}</c>.</summary>
    public static string Base(DropPipeline pipeline) => $"{Prefix}:{Token(pipeline)}";

    /// <summary>A tiered history key: base + <paramref name="tierSuffix"/> (<c>:{marker}:{stamp}</c> from <see cref="MetricTiers.Suffix"/>).</summary>
    public static string History(DropPipeline pipeline, string tierSuffix) => $"{Base(pipeline)}{tierSuffix}";
}
