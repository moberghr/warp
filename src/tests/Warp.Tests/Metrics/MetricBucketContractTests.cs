using Shouldly;
using Warp.Core.Adapters;
using Warp.Core.ClientObservability;
using Warp.Core.Endpoints;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Services;

namespace Warp.Tests.Metrics;

/// <summary>
/// Guards the single source of truth for latency-histogram buckets (§8.33): the DB percentile ladders
/// (<c>*Keys.Buckets</c>) and the exported OTel histogram Views must both come from <see cref="WarpHistogramBuckets"/>
/// — otherwise a hand-copied View drifts from its family's ladder and a Prometheus <c>histogram_quantile</c> reads a
/// different distribution than the local dashboard (the exact bug that shipped when <c>client.vitals</c> Views used
/// the generic HTTP ladder instead of the vitals ladder). Turns that class of drift into a CI failure.
/// </summary>
[Trait("Category", "NoDb")]
public class MetricBucketContractTests
{
    // The canonical instrument → DB-ladder pairing. A new latency family must be added here (and to
    // WarpHistogramBuckets.Views), so "forgot to wire the View" fails loudly.
    private static readonly (string Instrument, int[] DbLadder)[] Families =
    [
        ("warp.job.execution.duration", JobStatsKeys.Buckets),
        ("warp.job.queue.wait", QueueWaitKeys.Buckets),
        ("warp.adapter.duration", AdapterCounterKeys.Buckets),
        ("warp.endpoint.duration", EndpointCounterKeys.Buckets),
        ("warp.client.vitals", ClientEventKeys.Buckets),
    ];

    [Fact]
    public void EveryDbLadder_IsAFiniteLadderPlusTheOverflowRung()
    {
        // Each DB ladder is ascending, unique, and ends in the int.MaxValue overflow rung the percentile walk needs.
        foreach (var (_, ladder) in Families)
        {
            ladder[^1].ShouldBe(int.MaxValue);
            ladder.ShouldBe([.. ladder.Order()], $"ladder must be ascending: [{string.Join(",", ladder)}]");
            ladder.Distinct().Count().ShouldBe(ladder.Length);
        }
    }

    [Fact]
    public void HistogramViews_MatchTheDbLadder_ForEveryLatencyInstrument()
    {
        var views = WarpHistogramBuckets.Views.ToDictionary(v => v.Instrument, v => v.Bounds, StringComparer.Ordinal);

        // The exported View boundaries must equal the DB ladder's FINITE bounds (the +Inf overflow is implicit).
        foreach (var (instrument, ladder) in Families)
        {
            views.ShouldContainKey(instrument);
            views[instrument].ShouldBe([.. ladder.Where(b => b != int.MaxValue)]);
        }

        // No stray / missing View entries either way.
        views.Keys.OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe(Families.Select(f => f.Instrument).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void OutcomeToken_IsOneLowercaseVocabulary_AcrossEveryEmitter()
    {
        // The DB counter keys, the OTel meters, and the trace spans must all render an outcome through the single
        // canonical lowercase token (§8.33) — so a Prometheus read-back agrees with the local dashboard instead of
        // the two sides disagreeing on casing. The key builders delegate to the canonical map here; the meter and
        // span emission is pinned lowercase by AdapterTelemetryTests / OTelAggregateMetricsTests.
        foreach (var outcome in Enum.GetValues<AdapterCallOutcome>())
        {
            var canonical = WarpMetricCatalog.OutcomeToken(outcome);
            canonical.ShouldBe(canonical.ToLowerInvariant());
            AdapterCounterKeys.OutcomeToken(outcome).ShouldBe(canonical);
            EndpointCounterKeys.OutcomeToken(outcome).ShouldBe(canonical);
        }
    }
}
