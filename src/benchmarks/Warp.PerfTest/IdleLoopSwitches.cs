using Warp.Worker;

namespace Warp.PerfTest;

/// <summary>
/// Benchmark-only harness knob. Maps a friendly loop name to the
/// <see cref="WarpServerConfiguration"/> interval that drives it, so the idle scenario can be
/// run with individual background loops switched off and the per-loop DB cost measured as a
/// delta against the all-on baseline.
/// <para>
/// Production defaults are NOT touched: nothing here runs unless the harness is invoked with
/// <c>--disable</c>. A <c>null</c> interval makes <c>ServerTaskHost</c> skip building the loop
/// entirely (no ServerTask row, no bookkeeping, no ticks) — the same mechanism
/// <c>WarpTestServer</c> uses for test determinism.
/// </para>
/// </summary>
public static class IdleLoopSwitches
{
    /// <summary>
    /// Loops introduced after the 2026-05-14 idle baseline was recorded. Disabling the group
    /// answers "how much of the drift is new tasks?" in one run.
    /// </summary>
    public const string PostMayGroup = "post-may";

    private static readonly Dictionary<string, Action<WarpServerConfiguration>> Switches =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["heartbeat"] = c => c.HealthCheckInterval = null,
            ["server-cleanup"] = c => c.ServerCleanupInterval = null,
            ["expiration-cleanup"] = c => c.ExpirationCleanupInterval = null,
            ["stale-job-recovery"] = c => c.StaleJobRecoveryInterval = null,
            ["counter-aggregator"] = c => c.CounterAggregationInterval = null,
            ["recurring-scheduler"] = c => c.RecurringJobSchedulerInterval = null,
            ["orchestrator"] = c => c.OrchestrationInterval = null,
            ["message-router"] = c => c.MessageRoutingInterval = null,
            ["error-grouping"] = c => c.ErrorGroupingInterval = null,
            ["backlog-sampler"] = c => c.BacklogSampleInterval = null,
            ["slo-evaluator"] = c => c.SloEvaluationInterval = null,
            ["statistic-rollup"] = c => c.StatisticRollupInterval = null,

            // ScheduledActivationInterval is a non-nullable TimeSpan — the loop cannot be
            // switched off through config the way the others can. Pushing it well past the
            // measurement window is the closest honest equivalent: the loop is still built and
            // still registers its ServerTask row during warm-up, it simply never ticks inside
            // the window. Reported as "no ticks in window", not as "disabled".
            ["scheduled-activation"] = c => c.ScheduledActivationInterval = TimeSpan.FromHours(1),
        };

    private static readonly string[] PostMayLoops =
    [
        "backlog-sampler",
        "error-grouping",
        "slo-evaluator",
        "statistic-rollup",
    ];

    public static IReadOnlyCollection<string> Names => Switches.Keys;

    public static IReadOnlyList<string> Expand(IEnumerable<string> requested)
    {
        var expanded = new List<string>();
        foreach (var name in requested)
        {
            if (string.Equals(name, PostMayGroup, StringComparison.OrdinalIgnoreCase))
            {
                expanded.AddRange(PostMayLoops);

                continue;
            }

            if (!Switches.ContainsKey(name))
            {
                throw new ArgumentException(
                    $"Unknown loop '{name}'. Known: {string.Join(", ", Switches.Keys)}, {PostMayGroup}.",
                    nameof(requested));
            }

            expanded.Add(name);
        }

        return [.. expanded.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public static void Apply(WarpServerConfiguration configuration, IEnumerable<string> loops)
    {
        foreach (var loop in loops)
        {
            Switches[loop](configuration);
        }
    }
}
