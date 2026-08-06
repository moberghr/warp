using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Metrics;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Metrics;

/// <summary>
/// Database parity for <see cref="LocalMetricSource{TContext}"/> (both providers) — the four seam methods must
/// reproduce the existing merged Statistic+Counter read + <see cref="MetricTiers"/> down-bin semantics exactly, so
/// routing a reader through the seam moves no numbers. Keys are built with the real conventions (fine tier suffix,
/// pcth histogram) so the tests exercise the same classification the production readers use.
/// </summary>
[GenerateDatabaseTests]
public abstract class LocalMetricSourceTestsBase : IAsyncLifetime
{
    private static readonly DateTime T = new(2026, 8, 5, 10, 0, 0, DateTimeKind.Utc);

    private readonly IDatabaseFixture _fixture;

    protected LocalMetricSourceTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static MetricWindow Window(int hours) => new(T, T.AddHours(hours));

    // Logical refs (the storage keys they seed are qbacklog:{queue}:depth / qwait:{queue} / stats:succeeded).
    private static readonly MetricRef Succeeded = new(WarpMetricCatalog.Names.LifecycleSucceeded);

    private static MetricRef Depth(string queue)
        => new(WarpMetricCatalog.Names.QueueDepth, new Dictionary<string, string> { [WarpMetricCatalog.Tags.Queue] = queue });

    private static MetricRef QueueWait(string queue)
        => new(WarpMetricCatalog.Names.QueueWait, new Dictionary<string, string> { [WarpMetricCatalog.Tags.Queue] = queue });

    private LocalMetricSource<TestContext> Source() => new(_fixture.CreateContext());

    [TimedFact]
    public async Task GetTotal_Lifetime_CombinesStatisticAndPendingCounter()
    {
        await SeedStatistic("stats:succeeded", 100);
        await SeedCounter("stats:succeeded", 5); // not yet folded

        (await Source().GetTotalAsync(Succeeded, null, Ct)).ShouldBe(105);
    }

    [TimedFact]
    public async Task GetTotal_Windowed_SumsHistoryBucketsInWindow()
    {
        await SeedHistory("stats:succeeded", T, 10);
        await SeedHistory("stats:succeeded", T.AddHours(1), 20);
        await SeedHistory("stats:succeeded", T.AddHours(10), 99); // outside the 2h window

        (await Source().GetTotalAsync(Succeeded, Window(2), Ct)).ShouldBe(30);
    }

    [TimedFact]
    public async Task GetSeries_DownBinsFineBucketsToHourly()
    {
        await SeedHistory("stats:succeeded", T, 10);
        await SeedHistory("stats:succeeded", T.AddMinutes(5), 20);  // same hour
        await SeedHistory("stats:succeeded", T.AddHours(1), 5);     // next hour

        var series = await Source().GetSeriesAsync(
            new SeriesQuery(Succeeded, Window(2), MetricResolution.Hourly, MetricAggregation.Sum), Ct);

        series.Count.ShouldBe(2);
        series[0].BucketStart.ShouldBe(T);
        series[0].Value.ShouldBe(30);
        series[1].BucketStart.ShouldBe(T.AddHours(1));
        series[1].Value.ShouldBe(5);
    }

    [TimedFact]
    public async Task GetGauge_ReturnsLatestStatisticValueOrNull()
    {
        await SeedStatistic("qbacklog:default:depth", 250);

        (await Source().GetGaugeAsync(Depth("default"), Ct)).ShouldBe(250);
        (await Source().GetGaugeAsync(Depth("missing"), Ct)).ShouldBeNull();
    }

    [TimedFact]
    public async Task GetPercentile_WalksPcthHistogram()
    {
        // 4 samples ≤100ms, 96 ≤250ms → p95 lands in the 250 bucket.
        await SeedStatistic(QueueWaitKeys.PctHistory("default", 100, Suffix(T)), 4);
        await SeedStatistic(QueueWaitKeys.PctHistory("default", 250, Suffix(T)), 96);

        (await Source().GetPercentileAsync(QueueWait("default"), 95, Window(1), Ct)).ShouldBe(250);
    }

    [TimedFact]
    public async Task GetPercentile_AllSamplesOverCap_ReportsLadderOverflowBound_NotZero()
    {
        // Every claim waited past the 5-min ladder cap, so the only populated pcth bucket is the int.MaxValue
        // overflow. The percentile must report the ladder's largest finite bound (300000), NOT 0 — otherwise a
        // genuinely-breaching latency SLO reads observed==0 → NoData and renders grey instead of Breaching.
        await SeedStatistic(QueueWaitKeys.PctHistory("default", int.MaxValue, Suffix(T)), 50);

        (await Source().GetPercentileAsync(QueueWait("default"), 95, Window(1), Ct)).ShouldBe(QueueWaitKeys.Buckets[^2]);
    }

    [TimedFact]
    public async Task GetPercentile_PartialOverflow_CrossesToOverflowBound_NotLastPresentFinite()
    {
        // p95 crosses into the overflow bucket: 4 samples ≤60s, 96 over the cap. The result is the ladder cap
        // (300000), not the last present finite bound (60000) — matching SloMath / the lifetime walk.
        await SeedStatistic(QueueWaitKeys.PctHistory("default", 60000, Suffix(T)), 4);
        await SeedStatistic(QueueWaitKeys.PctHistory("default", int.MaxValue, Suffix(T)), 96);

        (await Source().GetPercentileAsync(QueueWait("default"), 95, Window(1), Ct)).ShouldBe(QueueWaitKeys.Buckets[^2]);
    }

    [TimedFact]
    public async Task GetBreakdown_AdapterCalls_ByAdapterAndOutcome()
    {
        await SeedStatistic(AdapterCounterKeys.Total("stripe", "success"), 8);
        await SeedStatistic(AdapterCounterKeys.Total("stripe", "failed"), 2);
        await SeedStatistic(AdapterCounterKeys.Total("twilio", "success"), 5);
        await SeedCounter(AdapterCounterKeys.Total("stripe", "success"), 1); // not-yet-folded, merges to 9

        var rows = await Source().GetBreakdownAsync(Calls(), ["adapter", "outcome"], null, Ct);

        rows.Count.ShouldBe(3);
        rows.Single(r => TagIs(r.Tags, "adapter", "stripe") && TagIs(r.Tags, "outcome", "success")).Value.ShouldBe(9);
        rows.Single(r => TagIs(r.Tags, "adapter", "stripe") && TagIs(r.Tags, "outcome", "failed")).Value.ShouldBe(2);
        rows.Single(r => TagIs(r.Tags, "adapter", "twilio") && TagIs(r.Tags, "outcome", "success")).Value.ShouldBe(5);
    }

    [TimedFact]
    public async Task GetBreakdown_AdapterCalls_DimensionExclusivity()
    {
        // Total and per-Operation rows both carry adapter+outcome; a [outcome] breakdown scoped to the adapter
        // must return ONLY the Total-dimension rows (the Operation row carries an extra 'operation' tag).
        await SeedStatistic(AdapterCounterKeys.Total("stripe", "success"), 10);
        await SeedStatistic(AdapterCounterKeys.Operation("stripe", "charge", "success"), 4);

        var byOutcome = await Source().GetBreakdownAsync(Calls("stripe"), ["outcome"], null, Ct);
        byOutcome.Single().Value.ShouldBe(10); // Total only — the op row's 4 is excluded

        var byOp = await Source().GetBreakdownAsync(Calls("stripe"), ["operation", "outcome"], null, Ct);
        byOp.Single(r => TagIs(r.Tags, "operation", "charge")).Value.ShouldBe(4);
    }

    [TimedFact]
    public async Task GetBreakdown_AdapterDuration_SumsDurTokenNotCounts()
    {
        await SeedStatistic(AdapterCounterKeys.Total("stripe", "success"), 3);      // a count, not duration
        await SeedStatistic(AdapterCounterKeys.Total("stripe", AdapterCounterKeys.DurationToken), 450);

        var rows = await Source().GetBreakdownAsync(Duration(), ["adapter"], null, Ct);

        rows.Single(r => TagIs(r.Tags, "adapter", "stripe")).Value.ShouldBe(450);
    }

    [TimedFact]
    public async Task GetPercentileBreakdown_AdapterDuration_WalksLifetimePct()
    {
        // 4 samples ≤100ms, 96 ≤250ms → p95 lands in the 250 bucket.
        await SeedStatistic(AdapterCounterKeys.Pct("stripe", 100), 4);
        await SeedStatistic(AdapterCounterKeys.Pct("stripe", 250), 96);

        var rows = await Source().GetPercentileBreakdownAsync(Duration(), 95, ["adapter"], null, Ct);

        rows.Single(r => TagIs(r.Tags, "adapter", "stripe")).Value.ShouldBe(250);
    }

    [TimedFact]
    public async Task GetPercentileBreakdown_WithWindow_ThrowsNotSupported_NotSilentLifetime()
    {
        // Local has no windowed grouped-percentile path (it reads the lifetime pct histogram). A windowed request
        // must fail loudly rather than return lifetime data — the Prometheus backend honors the window, so silently
        // ignoring it here would diverge the two backends. No production caller passes a non-null window today.
        await Should.ThrowAsync<NotSupportedException>(async () =>
            await Source().GetPercentileBreakdownAsync(Duration(), 95, ["adapter"], Window(1), Ct));
    }

    [TimedFact]
    public async Task GetTagValues_AdapterCalls_DistinctAdapters()
    {
        await SeedStatistic(AdapterCounterKeys.Total("stripe", "success"), 1);
        await SeedStatistic(AdapterCounterKeys.Total("twilio", "success"), 1);
        await SeedStatistic(AdapterCounterKeys.Total("stripe", "failed"), 1);

        (await Source().GetTagValuesAsync(Calls(), "adapter", null, Ct)).ShouldBe(["stripe", "twilio"]);
    }

    private static bool TagIs(IReadOnlyDictionary<string, string> tags, string key, string value)
        => tags.TryGetValue(key, out var v) && string.Equals(v, value, StringComparison.Ordinal);

    private static MetricRef Calls(string? adapter = null)
        => new(WarpMetricCatalog.Names.AdapterCalls, adapter is null ? null : new Dictionary<string, string> { [WarpMetricCatalog.Tags.Adapter] = adapter });

    private static MetricRef Duration(string? adapter = null)
        => new(WarpMetricCatalog.Names.AdapterDuration, adapter is null ? null : new Dictionary<string, string> { [WarpMetricCatalog.Tags.Adapter] = adapter });

    private static string Suffix(DateTime ts) => MetricTiers.Suffix(MetricTier.Fine, ts, 5);

    private async Task SeedStatistic(string key, long value)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<Statistic>().Add(new Statistic { Key = key, Value = value });
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task SeedCounter(string key, int value)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<Counter>().Add(new Counter { Key = key, Value = value });
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task SeedHistory(string baseKey, DateTime ts, long value)
        => await SeedStatistic(baseKey + Suffix(ts), value);
}
