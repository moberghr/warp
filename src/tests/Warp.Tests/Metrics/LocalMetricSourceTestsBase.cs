using Microsoft.EntityFrameworkCore;
using Shouldly;
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
