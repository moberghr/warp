namespace Warp.Core.Services;

/// <summary>
/// <see cref="Warp.Core.Data.Entities.Statistic"/> keys for the per-queue backlog gauges (§8.26) — current
/// depth (count of eligible Enqueued jobs) and oldest-job age (seconds). Unlike the <c>qwait:</c> /
/// <c>jobstat:</c> Counter families (which ACCUMULATE via the CounterAggregator fold), backlog is a
/// point-in-time gauge, so <c>BacklogSampler</c> UPSERTS these <c>Statistic</c> rows directly each tick
/// (overwrite, never increment) and never writes <c>Counter</c> rows under this prefix — so the aggregator
/// never doubles them. Own top-level prefix (<see cref="Prefix"/> <c>"qbacklog"</c>), disjoint from every
/// other family (§8.6/§8.19). The queue name is <see cref="Sanitize"/>d so the colon-delimited key parses
/// unambiguously. Backlog is deliberately NOT sliced by application: an eligible-but-unclaimed job has no
/// executor yet, and slicing by the PUBLISHING app (<c>Job.Application</c>) would be a per-creator metric,
/// which §8.23 omits — so backlog is a queue-global signal only (contrast <c>qwait-app</c>, which IS
/// executor-attributed at claim time).
/// </summary>
internal static class QueueBacklogKeys
{
    public const string Prefix = "qbacklog";

    public const string DepthToken = "depth";

    public const string OldestAgeToken = "oldest_age_seconds";

    public static string Total(string queue, string token) => $"{Prefix}:{queue}:{token}";

    public static string Sanitize(string value) => value.Replace(':', '-');

    // Parses a per-queue backlog key (qbacklog:{queue}:{token}); token ∈ {depth, oldest_age_seconds}.
    public static bool TryParseTotal(string key, out string queue, out string token)
    {
        queue = string.Empty;
        token = string.Empty;

        var parts = key.Split(':');
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        queue = parts[1];
        token = parts[2];

        return true;
    }
}
