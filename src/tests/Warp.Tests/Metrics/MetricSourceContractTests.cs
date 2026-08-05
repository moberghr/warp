using Shouldly;
using Warp.Core.Metrics;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb tests that pin the <see cref="IMetricSource"/> contract's semantics against the in-memory
/// <see cref="FakeMetricSource"/> — the same expectations the DB-backed <c>LocalMetricSource</c> (and a later
/// Prometheus backend) must satisfy. Anchors the seam before any read site is routed through it.
/// </summary>
[Trait("Category", "NoDb")]
public class MetricSourceContractTests
{
    private static readonly DateTime T0 = new(2026, 8, 5, 10, 0, 0, DateTimeKind.Utc);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static MetricWindow Window(int hours) => new(T0, T0.AddHours(hours));

    [Fact]
    public async Task GetTotal_SumsMatchingSamples_AndRespectsWindow()
    {
        var src = new FakeMetricSource()
            .Add("auth", T0, 5)
            .Add("auth", T0.AddHours(1), 3)
            .Add("auth", T0.AddHours(5), 7); // outside a 2h window

        (await src.GetTotalAsync(new MetricRef("auth"), null, Ct)).ShouldBe(15);
        (await src.GetTotalAsync(new MetricRef("auth"), Window(2), Ct)).ShouldBe(8);
    }

    [Fact]
    public async Task GetTotal_TagFilter_IsExactSubsetMatch()
    {
        var src = new FakeMetricSource()
            .Add("auth", T0, 10, ("outcome", "approved"))
            .Add("auth", T0, 2, ("outcome", "declined"));

        (await src.GetTotalAsync(new MetricRef("auth", Tags(("outcome", "approved"))), null, Ct)).ShouldBe(10);
    }

    [Fact]
    public async Task GetSeries_BreakdownByTag_GroupsPerBucketAndValue()
    {
        var src = new FakeMetricSource()
            .Add("auth", T0.AddMinutes(5), 4, ("outcome", "approved"))
            .Add("auth", T0.AddMinutes(50), 6, ("outcome", "approved")) // same hour bucket
            .Add("auth", T0.AddMinutes(20), 1, ("outcome", "declined"))
            .Add("auth", T0.AddHours(1), 5, ("outcome", "approved")); // next hour

        var series = await src.GetSeriesAsync(
            new SeriesQuery(new MetricRef("auth"), Window(2), MetricResolution.Hourly, MetricAggregation.Sum, BreakdownBy: "outcome"),
            Ct);

        series.Count.ShouldBe(3);
        series.ShouldContain(b => b.BucketStart == T0 && b.TagValue == "approved" && b.Value == 10);
        series.ShouldContain(b => b.BucketStart == T0 && b.TagValue == "declined" && b.Value == 1);
        series.ShouldContain(b => b.BucketStart == T0.AddHours(1) && b.TagValue == "approved" && b.Value == 5);
    }

    [Fact]
    public async Task GetSeries_Resolution_BucketsFineVsHourly()
    {
        var src = new FakeMetricSource()
            .Add("m", T0.AddMinutes(2), 1)
            .Add("m", T0.AddMinutes(7), 1); // different 5-min bucket, same hour

        var fine = await src.GetSeriesAsync(new SeriesQuery(new MetricRef("m"), Window(1), MetricResolution.Fine, MetricAggregation.Sum), Ct);
        var hourly = await src.GetSeriesAsync(new SeriesQuery(new MetricRef("m"), Window(1), MetricResolution.Hourly, MetricAggregation.Sum), Ct);

        fine.Count.ShouldBe(2);
        hourly.Count.ShouldBe(1);
        hourly[0].Value.ShouldBe(2);
    }

    [Fact]
    public async Task GetSeries_LastAggregation_TakesLatestPerBucket()
    {
        var src = new FakeMetricSource()
            .Add("depth", T0.AddMinutes(1), 3)
            .Add("depth", T0.AddMinutes(40), 9); // latest in the hour

        var series = await src.GetSeriesAsync(new SeriesQuery(new MetricRef("depth"), Window(1), MetricResolution.Hourly, MetricAggregation.Last), Ct);

        series.ShouldHaveSingleItem().Value.ShouldBe(9);
    }

    [Fact]
    public async Task GetPercentile_NearestRank_OverMatchingLatencies()
    {
        var src = new FakeMetricSource();
        for (var i = 1; i <= 100; i++)
        {
            src.AddLatency("lat", i, ("net", "visa"));
        }

        src.AddLatency("lat", 9999, ("net", "mc")); // filtered out

        (await src.GetPercentileAsync(new MetricRef("lat", Tags(("net", "visa"))), 95, Window(1), Ct)).ShouldBe(95);
        (await src.GetPercentileAsync(new MetricRef("lat", Tags(("net", "visa"))), 50, Window(1), Ct)).ShouldBe(50);
    }

    [Fact]
    public async Task Gauge_ReturnsValueOrNull()
    {
        var src = new FakeMetricSource().SetGauge("backlog", 42);

        (await src.GetGaugeAsync(new MetricRef("backlog"), Ct)).ShouldBe(42);
        (await src.GetGaugeAsync(new MetricRef("missing"), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task GetBreakdown_GroupsTotalsByTags_OverWindow()
    {
        var src = new FakeMetricSource()
            .Add("calls", T0, 3, ("adapter", "stripe"), ("outcome", "ok"))
            .Add("calls", T0.AddHours(1), 2, ("adapter", "stripe"), ("outcome", "ok"))
            .Add("calls", T0, 1, ("adapter", "stripe"), ("outcome", "failed"))
            .Add("calls", T0, 4, ("adapter", "twilio"), ("outcome", "ok"))
            .Add("calls", T0.AddHours(5), 9, ("adapter", "stripe"), ("outcome", "ok")); // outside 2h window

        var rows = await src.GetBreakdownAsync(new MetricRef("calls"), ["adapter", "outcome"], Window(2), Ct);

        rows.Count.ShouldBe(3);
        rows.ShouldContain(r => r.Tags["adapter"] == "stripe" && r.Tags["outcome"] == "ok" && r.Value == 5);
        rows.ShouldContain(r => r.Tags["adapter"] == "stripe" && r.Tags["outcome"] == "failed" && r.Value == 1);
        rows.ShouldContain(r => r.Tags["adapter"] == "twilio" && r.Tags["outcome"] == "ok" && r.Value == 4);
    }

    [Fact]
    public async Task GetBreakdown_TagFilterNarrowsBeforeGrouping()
    {
        var src = new FakeMetricSource()
            .Add("calls", T0, 3, ("adapter", "stripe"), ("outcome", "ok"))
            .Add("calls", T0, 4, ("adapter", "twilio"), ("outcome", "ok"));

        var rows = await src.GetBreakdownAsync(new MetricRef("calls", Tags(("adapter", "stripe"))), ["outcome"], null, Ct);

        rows.ShouldHaveSingleItem().Value.ShouldBe(3);
    }

    [Fact]
    public async Task GetPercentileBreakdown_PerGroupNearestRank()
    {
        var src = new FakeMetricSource();
        for (var i = 1; i <= 100; i++)
        {
            src.AddLatency("lat", i, ("route", "a"));
            src.AddLatency("lat", i * 2, ("route", "b"));
        }

        var rows = await src.GetPercentileBreakdownAsync(new MetricRef("lat"), 95, ["route"], Window(1), Ct);

        rows.Count.ShouldBe(2);
        rows.Single(r => string.Equals(r.Tags["route"], "a", StringComparison.Ordinal)).Value.ShouldBe(95);
        rows.Single(r => string.Equals(r.Tags["route"], "b", StringComparison.Ordinal)).Value.ShouldBe(190);
    }

    [Fact]
    public async Task GetTagValues_DistinctSortedValues()
    {
        var src = new FakeMetricSource()
            .Add("calls", T0, 1, ("adapter", "stripe"))
            .Add("calls", T0, 1, ("adapter", "twilio"))
            .Add("calls", T0, 1, ("adapter", "stripe")); // duplicate

        var values = await src.GetTagValuesAsync(new MetricRef("calls"), "adapter", null, Ct);

        values.ShouldBe(["stripe", "twilio"]);
    }

    [Fact]
    public async Task Empty_ReturnsZeroesAndEmpties()
    {
        var src = new FakeMetricSource();

        (await src.GetTotalAsync(new MetricRef("x"), null, Ct)).ShouldBe(0);
        (await src.GetSeriesAsync(new SeriesQuery(new MetricRef("x"), Window(1), MetricResolution.Hourly, MetricAggregation.Sum), Ct)).ShouldBeEmpty();
        (await src.GetPercentileAsync(new MetricRef("x"), 95, Window(1), Ct)).ShouldBe(0);
        (await src.GetGaugeAsync(new MetricRef("x"), Ct)).ShouldBeNull();
        (await src.GetBreakdownAsync(new MetricRef("x"), ["adapter"], null, Ct)).ShouldBeEmpty();
        (await src.GetPercentileBreakdownAsync(new MetricRef("x"), 95, ["route"], Window(1), Ct)).ShouldBeEmpty();
        (await src.GetTagValuesAsync(new MetricRef("x"), "adapter", null, Ct)).ShouldBeEmpty();
    }

    private static Dictionary<string, string> Tags(params (string Key, string Value)[] tags)
        => tags.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
}
