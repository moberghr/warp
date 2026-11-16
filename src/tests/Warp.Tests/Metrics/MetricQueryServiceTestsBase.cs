using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Core.Slo;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Metrics;

/// <summary>
/// Database coverage for <see cref="MetricQueryService{TContext}"/> and the dashboard metrics routes (R19–R21): the
/// registry list, tier by range, partial fine rows summed on read, breakdown top 10 plus <c>(rest)</c>, exact and
/// wildcard filters, compare overlays labelled with the resolution they were served at, histogram percentiles from
/// merged buckets (and the overflow bucket), gauge average/min/max, the application filter and the unknown
/// instrument, and the still-filling newest bucket. "Now" is a fixed instant; every row and range is seeded relative to
/// it, and the tests that depend on the clock pass a <see cref="FakeTimeProvider"/> set relative to it.
/// </summary>
[GenerateDatabaseTests]
public abstract class MetricQueryServiceTestsBase : IAsyncLifetime
{
    private const string Bounds = "10,100,1000";

    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly IDatabaseFixture _fixture;

    // Query strings the parser must reject for their length or shape (N4); built here because InlineData needs constants.
    public static TheoryData<string> OverlongQueries =>
    [
        $"from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&breakdown={new string('b', 129)}",
        $"from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&application={new string('a', 129)}",
        $"from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&filter={new string('k', 129)}:v",
        $"from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&filter=k:{new string('v', 513)}",
    ];

    protected MetricQueryServiceTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task ListInstruments_RegistryRows_ReturnsThemWithParsedMetadata()
    {
        var seen = Now.AddHours(-2);
        await SeedAsync(
            [
                new MetricInstrument
                {
                    Name = "orders.latency",
                    Meter = "shop",
                    Kind = MetricKind.Histogram,
                    Unit = "ms",
                    Description = "Order latency",
                    Bounds = Bounds,
                    TagKeys = "route,status",
                    FirstSeenAt = seen.AddDays(-1),
                    LastSeenAt = seen,
                    CollapsedAt = seen,
                    CollapsedTags = "route",
                },
                Instrument("orders.count", MetricKind.Counter),
            ],
            []);

        var result = await CreateService().ListInstruments(Ct);

        result.Select(x => x.Name).ShouldBe(["orders.count", "orders.latency"]);
        var latency = result[1];
        latency.Meter.ShouldBe("shop");
        latency.Kind.ShouldBe(MetricKind.Histogram);
        latency.Unit.ShouldBe("ms");
        latency.Bounds.ShouldBe([10d, 100d, 1000d]);
        latency.TagKeys.ShouldBe(["route", "status"]);
        latency.CollapsedAt.ShouldBe(seen);
        latency.CollapsedTags.ShouldBe(["route"]);
        result[0].Bounds.ShouldBeEmpty();
        result[0].TagKeys.ShouldBeEmpty();
        result[0].CollapsedAt.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetSeries_UnknownInstrument_ReturnsNull()
    {
        await SeedAsync([Instrument("known", MetricKind.Counter)], [Point("known", Now.AddHours(-1), sum: 1)]);

        var result = await CreateService().GetSeries("missing", Query(Now.AddHours(-2), Now), Ct);

        result.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetSeries_SixHourRangeOfFineRows_LabelsTheLineFine()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddHours(-1), sum: 1)]);

        var result = await CreateService().GetSeries("c", Query(Now.AddHours(-6), Now), Ct);

        result.ShouldNotBeNull();
        result.Resolution.ShouldBe(MetricTier.Fine);
        var line = result.Series.ShouldHaveSingleItem();
        line.Resolution.ShouldBe(MetricTier.Fine);
        line.Points.ShouldHaveSingleItem().Sum.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetSeries_SixHourRangeHoldingOnlyAnHourlyRow_KeepsItsHourAndLabelsTheLineHourly()
    {
        var hour = new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", hour, sum: 9, tier: MetricTier.Hourly, compacted: true)]);

        var result = await CreateService().GetSeries("c", Query(Now.AddHours(-6), Now), Ct);

        result.ShouldNotBeNull().Resolution.ShouldBe(MetricTier.Fine);
        var line = result.Series.ShouldHaveSingleItem();
        line.Resolution.ShouldBe(MetricTier.Hourly);
        var point = line.Points.ShouldHaveSingleItem();
        point.Timestamp.ShouldBe(hour);
        point.Sum.ShouldBe(9);
    }

    [TimedFact]
    public async Task GetSeries_TwentyFourHourRange_ReadsHourlyAndNewestFineRowsBinnedToHoursExactlyOnce()
    {
        // Hourly rows for the hours older than 6h (12:00 yesterday .. 05:00 today), plus fine rows for the last 6h
        // that rollup has not moved yet — compacted and partial. Hour 05:00 is split across both tiers (rollup is
        // mid-way through it), which is the case a single-tier read gets wrong.
        var yesterdayNoon = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        var points = new List<MetricPoint>();
        var expected = new SortedDictionary<DateTime, double>();
        for (var i = 0; i < 18; i++)
        {
            var hour = yesterdayNoon.AddHours(i);
            points.Add(Point("c", hour, sum: 100 + i, tier: MetricTier.Hourly, compacted: true));
            expected[hour] = 100 + i;
        }

        var five = new DateTime(2026, 11, 13, 5, 0, 0, DateTimeKind.Utc);
        points.Add(Point("c", five.AddMinutes(55), sum: 3, compacted: true));
        expected[five] += 3;

        var six = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);
        points.Add(Point("c", six, sum: 10, compacted: true));
        points.Add(Point("c", six.AddMinutes(5), sum: 20, compacted: true));
        points.Add(Point("c", six.AddMinutes(10), sum: 1));
        points.Add(Point("c", six.AddMinutes(10), sum: 2));
        expected[six] = 33;

        var eleven = new DateTime(2026, 11, 13, 11, 0, 0, DateTimeKind.Utc);
        points.Add(Point("c", eleven.AddMinutes(55), sum: 5));
        expected[eleven] = 5;
        await SeedAsync([Instrument("c", MetricKind.Counter)], [.. points]);

        var result = await CreateService().GetSeries("c", Query(Now.AddHours(-24), Now), Ct);

        result.ShouldNotBeNull().Resolution.ShouldBe(MetricTier.Hourly);
        var line = result.Series.ShouldHaveSingleItem();
        line.Resolution.ShouldBe(MetricTier.Hourly);
        line.Points.Select(x => (x.Timestamp, x.Sum)).ShouldBe(expected.Select(x => (x.Key, x.Value)));
        line.Points.Sum(x => x.Sum).ShouldBe(points.Sum(x => x.Sum));
    }

    [TimedFact]
    public async Task GetSeries_ThirtyDayRange_BinsDailyHourlyAndFineRowsToDaysIncludingTheNewestData()
    {
        var points = new List<MetricPoint>
        {
            Point("c", new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc), sum: 1000, tier: MetricTier.Daily, compacted: true),
            Point("c", new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc), sum: 900, tier: MetricTier.Daily, compacted: true),
            Point("c", new DateTime(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc), sum: 20, tier: MetricTier.Hourly, compacted: true),
            Point("c", new DateTime(2026, 11, 10, 15, 0, 0, DateTimeKind.Utc), sum: 30, tier: MetricTier.Hourly, compacted: true),
            Point("c", new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc), sum: 50, tier: MetricTier.Hourly, compacted: true),
            Point("c", new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc), sum: 7, compacted: true),
            Point("c", new DateTime(2026, 11, 13, 11, 55, 0, DateTimeKind.Utc), sum: 3),
        };
        await SeedAsync([Instrument("c", MetricKind.Counter)], [.. points]);

        var result = await CreateService().GetSeries("c", Query(Now.AddDays(-30), Now), Ct);

        result.ShouldNotBeNull().Resolution.ShouldBe(MetricTier.Daily);
        var line = result.Series.ShouldHaveSingleItem();
        line.Resolution.ShouldBe(MetricTier.Daily);
        line.Points.Select(x => (x.Timestamp, x.Sum)).ShouldBe(
        [
            (new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc), 1000d),
            (new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc), 900d),
            (new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc), 50d),
            (new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), 60d),
        ]);
    }

    [TimedFact]
    public async Task GetSeries_ThirtyDayRangeHoldingOnlyFineRows_BinsToDaysAndLabelsTheLineDaily()
    {
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [Point("c", new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc), sum: 4), Point("c", new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc), sum: 6)]);

        var result = await CreateService().GetSeries("c", Query(Now.AddDays(-30), Now), Ct);

        // The resolution is the coarser of the display tier (daily) and the stored tier read (fine): the points are days.
        var line = result.ShouldNotBeNull().Series.ShouldHaveSingleItem();
        line.Resolution.ShouldBe(MetricTier.Daily);
        var point = line.Points.ShouldHaveSingleItem();
        point.Timestamp.ShouldBe(new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc));
        point.Sum.ShouldBe(10);
    }

    [TimedFact]
    public async Task GetSeries_FromInsideAnHourlyRow_ReadsTheStraddlingHourlyRowAndTheFineRowsAfterIt()
    {
        // R21a: From = 06:30 falls inside the 06:00 hourly row. That row is read (whole, on its own hour bucket) along
        // with the fine rows after it; a fine row before From is not.
        var six = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", six, sum: 40, tier: MetricTier.Hourly, compacted: true),
                Point("c", six.AddMinutes(25), sum: 1000),
                Point("c", six.AddHours(1), sum: 3),
                Point("c", Now.AddMinutes(-5), sum: 5),
            ]);

        var result = await CreateService().GetSeries("c", Query(six.AddMinutes(30), Now), Ct);

        var line = result.ShouldNotBeNull().Series.ShouldHaveSingleItem();
        line.Points.Select(x => (x.Timestamp, x.Sum)).ShouldBe([(six, 40d), (six.AddHours(1), 3d), (Now.AddMinutes(-5), 5d)]);
        line.Points.Sum(x => x.Sum).ShouldBe(48);
        line.Resolution.ShouldBe(MetricTier.Hourly);
    }

    [TimedFact]
    public async Task GetSeries_ThirtyDayRangeFromInsideADailyRow_ReadsTheStraddlingDailyRow()
    {
        var from = Now.AddDays(-30);
        var fromDay = from.Date;
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", fromDay.AddDays(-1), sum: 9999, tier: MetricTier.Daily, compacted: true),
                Point("c", fromDay, sum: 500, tier: MetricTier.Daily, compacted: true),
                Point("c", new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc), sum: 7),
            ]);

        var result = await CreateService().GetSeries("c", Query(from, Now), Ct);

        var line = result.ShouldNotBeNull().Series.ShouldHaveSingleItem();
        line.Points.Select(x => (x.Timestamp, x.Sum)).ShouldBe([(fromDay, 500d), (new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), 7d)]);
    }

    [TimedFact]
    public async Task GetSeries_CounterTotalOnly_EqualsThePerSeriesRead()
    {
        // No breakdown and no filter totals in SQL by (tier, bucket); a match-everything filter takes the per-series
        // path. Same rows, so the same points — across tiers, partial rows and several tag sets.
        await SeedAsync([Instrument("c", MetricKind.Counter)], [.. MultiTierRows("c", null, null)]);

        var service = CreateService();
        var range = (From: Now.AddHours(-24), To: Now);
        var total = await service.GetSeries("c", Query(range.From, range.To), Ct);
        var perSeries = await service.GetSeries("c", Query(range.From, range.To, filters: new Dictionary<string, string> { ["route"] = "*" }), Ct);

        var totalPoints = total.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points;
        totalPoints.Count.ShouldBeGreaterThan(3);
        Project(totalPoints).ShouldBe(Project(perSeries.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points));
        total.Series[0].Resolution.ShouldBe(perSeries.Series[0].Resolution);
    }

    [TimedFact]
    public async Task GetSeries_HistogramWithoutPercentile_EqualsTheBucketReadAndTheTotalRead()
    {
        // Without a percentile a histogram groups in SQL and never fetches its bucket CSVs; with one it reads the rows
        // as stored. Count, sum, min and max must not depend on which.
        await SeedAsync([Instrument("h", MetricKind.Histogram, Bounds)], [.. MultiTierRows("h", Bounds, "5,3,2,0")]);

        var service = CreateService();
        var from = Now.AddHours(-24);
        var routes = new Dictionary<string, string> { ["route"] = "*" };
        var total = await service.GetSeries("h", Query(from, Now), Ct);
        var grouped = await service.GetSeries("h", Query(from, Now, filters: routes), Ct);
        var withBuckets = await service.GetSeries("h", Query(from, Now, percentile: 95), Ct);

        var totalPoints = total.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points;
        totalPoints.Count.ShouldBeGreaterThan(3);
        totalPoints.ShouldAllBe(x => x.Percentile == null);
        Project(grouped.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points).ShouldBe(Project(totalPoints));
        var bucketPoints = withBuckets.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points;
        Project(bucketPoints).ShouldBe(Project(totalPoints));
        bucketPoints.ShouldAllBe(x => x.Percentile != null);
    }

    [TimedFact]
    public async Task GetSeries_TwoUncompactedFineRowsOfOneKey_SumsThemIntoOnePoint()
    {
        var bucket = Now.AddMinutes(-20);
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", bucket, count: 2, sum: 5, tags: "route=a"),
                Point("c", bucket, count: 3, sum: 7, tags: "route=a"),
            ]);

        var result = await CreateService().GetSeries("c", Query(Now.AddHours(-1), Now), Ct);

        var point = result.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.ShouldHaveSingleItem();
        point.Timestamp.ShouldBe(bucket);
        point.Count.ShouldBe(5);
        point.Sum.ShouldBe(12);
        point.Value.ShouldBe(12);
    }

    [TimedFact]
    public async Task GetSeries_TwelveBreakdownValues_ReturnsTopTenAndTheRest()
    {
        var bucket = Now.AddMinutes(-30);
        var points = Enumerable.Range(1, 12)
            .Select(i => Point("c", bucket, count: i, sum: i * 10, tags: $"route=r{i:D2}"))
            .ToArray();
        await SeedAsync([Instrument("c", MetricKind.Counter)], points);

        var query = Query(Now.AddHours(-1), Now, breakdown: "route");
        var result = await CreateService().GetSeries("c", query, Ct);

        var lines = result.ShouldNotBeNull().Series;
        lines.Count.ShouldBe(11);
        lines.Take(10).Select(x => x.Name).ShouldBe(Enumerable.Range(3, 10).Reverse().Select(i => $"r{i:D2}"));
        var rest = lines[10];
        rest.Name.ShouldBe("(rest)");
        var restPoint = rest.Points.ShouldHaveSingleItem();
        restPoint.Sum.ShouldBe(30);
        restPoint.Count.ShouldBe(3);
    }

    [TimedFact]
    public async Task GetSeries_BreakdownWithEqualCountsButDifferentSums_RanksBySumThenByValue()
    {
        // Counters rank by |sum| over the range, not by measurement count; equal sums fall back to ordinal value order.
        var bucket = Now.AddMinutes(-30);
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", bucket, count: 5, sum: 10, tags: "route=a"),
                Point("c", bucket, count: 5, sum: 100, tags: "route=b"),
                Point("c", bucket, count: 50, sum: 1, tags: "route=c"),
                Point("c", bucket, count: 1, sum: 7, tags: "route=e"),
                Point("c", bucket, count: 9, sum: 7, tags: "route=d"),
            ]);

        var result = await CreateService().GetSeries("c", Query(Now.AddHours(-1), Now, breakdown: "route"), Ct);

        result.ShouldNotBeNull().Series.Select(x => x.Name).ShouldBe(["b", "a", "d", "e", "c"]);
    }

    [TimedFact]
    public async Task GetSeries_CompareWithBreakdown_OverlayKeepsThePrimaryValuesAndFoldsTheRestIntoRest()
    {
        var current = Now.AddMinutes(-30);
        var yesterday = current.AddDays(-1);
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", current, sum: 50, tags: "route=a"),
                Point("c", current, sum: 30, tags: "route=b"),
                Point("c", yesterday, sum: 5, tags: "route=a"),
                Point("c", yesterday, sum: 1, tags: "route=b"),
                Point("c", yesterday, sum: 100, tags: "route=c"),
            ]);

        var query = Query(Now.AddHours(-6), Now, breakdown: "route", compare: [MetricCompareOffset.Day]);
        var result = await CreateService().GetSeries("c", query, Ct);

        var lines = result.ShouldNotBeNull().Series;
        lines.Where(x => x.Compare is null).Select(x => x.Name).ShouldBe(["a", "b"]);
        var overlay = lines.Where(x => string.Equals(x.Compare, "1d", StringComparison.Ordinal)).ToList();
        overlay.Select(x => x.Name).ShouldBe(["a", "b", "(rest)"]);
        overlay.Select(x => x.Points.ShouldHaveSingleItem().Sum).ShouldBe([5d, 1d, 100d]);
        overlay[0].Points[0].Timestamp.ShouldBe(current);
    }

    [TimedTheory]
    [InlineData("500", 2d)]
    [InlineData("5*", 30d)]
    [InlineData("*", 382d)]
    [InlineData("{none}", 385d)]
    [InlineData("{no*", 256d)]
    [InlineData("403", 32d)]
    public async Task GetSeries_Filter_MatchesExactlyWhatTheSloFilterMatches(string pattern, double expected)
    {
        // W2: one matcher. The page's series filter and an SLO TagFilter agree on every row: absent tag ({none}),
        // a literal "{none}" value, a bare *, a prefix and a literal.
        string[] tags = ["-", "status=500", "status=5000", "status=503", "status=5", "status=403", "status=200", "other=x", "status={none}"];
        var bucket = Now.AddMinutes(-30);
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [.. tags.Select((x, i) => Point("c", bucket, sum: 1 << i, tags: x))]);
        MetricFilter.TryParse($"status={pattern}", out var slo, out _).ShouldBeTrue();
        var sloSum = tags
            .Select((x, i) => (Tags: x, Sum: (double)(1 << i)))
            .Where(x => slo.Matches(MetricFilter.ParseCanonicalTags(x.Tags)))
            .Sum(x => x.Sum);

        var result = await CreateService().GetSeries("c", Query(Now.AddHours(-1), Now, filters: new Dictionary<string, string> { ["status"] = pattern }), Ct);

        var pageSum = result.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.Sum(x => x.Sum);
        pageSum.ShouldBe(expected);
        sloSum.ShouldBe(expected);
    }

    [TimedFact]
    public async Task GetSeries_FromTooCloseToMinValue_ThrowsArgumentOutOfRange()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], []);
        var from = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => CreateService().GetSeries("c", Query(from, from.AddDays(1), compare: [MetricCompareOffset.Week]), Ct));
    }

    [TimedFact]
    public async Task GetSeries_ExactFilter_KeepsOnlyMatchingTagSets()
    {
        var bucket = Now.AddMinutes(-30);
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", bucket, sum: 1, tags: "route=a,status=200"),
                Point("c", bucket, sum: 10, tags: "route=b,status=200"),
                Point("c", bucket, sum: 100, tags: "route=a,status=500"),
            ]);

        var query = Query(Now.AddHours(-1), Now, filters: new Dictionary<string, string> { ["route"] = "a" });
        var result = await CreateService().GetSeries("c", query, Ct);

        result.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.ShouldHaveSingleItem().Sum.ShouldBe(101);
    }

    [TimedFact]
    public async Task GetSeries_WildcardFilter_MatchesThePrefix()
    {
        var bucket = Now.AddMinutes(-30);
        await SeedAsync(
            [Instrument("http.server.requests", MetricKind.Counter)],
            [
                Point("http.server.requests", bucket, sum: 1, tags: "http.response.status_code=200"),
                Point("http.server.requests", bucket, sum: 10, tags: "http.response.status_code=500"),
                Point("http.server.requests", bucket, sum: 100, tags: "http.response.status_code=503"),
            ]);

        var query = Query(Now.AddHours(-1), Now, filters: new Dictionary<string, string> { ["http.response.status_code"] = "5*" });
        var result = await CreateService().GetSeries("http.server.requests", query, Ct);

        result.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.ShouldHaveSingleItem().Sum.ShouldBe(110);
    }

    [TimedFact]
    public async Task GetSeries_CompareOneDayOverSixHours_ReturnsTheShiftedSeriesAtHourlyResolution()
    {
        var current = Now.AddMinutes(-30);
        var yesterdayHour = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", current, sum: 5),
                Point("c", yesterdayHour, sum: 40, tier: MetricTier.Hourly, compacted: true),
            ]);

        var query = Query(Now.AddHours(-6), Now, compare: [MetricCompareOffset.Day]);
        var result = await CreateService().GetSeries("c", query, Ct);

        var lines = result.ShouldNotBeNull().Series;
        lines.Count.ShouldBe(2);
        var primary = lines[0];
        primary.Compare.ShouldBeNull();
        primary.Resolution.ShouldBe(MetricTier.Fine);
        primary.Points.ShouldHaveSingleItem().Sum.ShouldBe(5);
        var overlay = lines[1];
        overlay.Compare.ShouldBe("1d");
        overlay.Resolution.ShouldBe(MetricTier.Hourly);
        var point = overlay.Points.ShouldHaveSingleItem();
        point.Sum.ShouldBe(40);
        point.Timestamp.ShouldBe(yesterdayHour.AddDays(1));
    }

    [TimedFact]
    public async Task GetSeries_HistogramPartialRows_ComputesP95FromTheMergedBuckets()
    {
        var bucket = Now.AddMinutes(-20);
        await SeedAsync(
            [Instrument("h", MetricKind.Histogram, Bounds)],
            [
                Point("h", bucket, count: 10, sum: 100, min: 1, max: 900, buckets: "5,3,2,0", bounds: Bounds),
                Point("h", bucket, count: 5, sum: 20, min: 2, max: 9, buckets: "5,0,0,0", bounds: Bounds),
            ]);

        var result = await CreateService().GetSeries("h", Query(Now.AddHours(-1), Now, percentile: 95), Ct);

        // Merged buckets are 10,3,2,0 over 15 measurements: ceil(0.95 * 15) = 15 is reached in the third bucket.
        var point = result.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.ShouldHaveSingleItem();
        point.Count.ShouldBe(15);
        point.Value.ShouldBe(15);
        point.Percentile.ShouldBe(1000);
        point.Min.ShouldBe(1);
        point.Max.ShouldBe(900);
    }

    [TimedFact]
    public async Task GetSeries_CorruptBucketCsv_KeepsTheRowsCountsAndSkipsItsPercentileShare()
    {
        var name = $"h.{Guid.NewGuid():N}";
        var bucket = Now.AddMinutes(-20);
        await SeedAsync(
            [Instrument(name, MetricKind.Histogram, Bounds)],
            [
                Point(name, bucket, count: 10, sum: 100, min: 1, max: 900, buckets: "5,3,2,0", bounds: Bounds),
                Point(name, bucket, count: 5, sum: 20, min: 2, max: 9, buckets: "5,x,0,0", bounds: Bounds),
                Point(name, bucket, count: 2, sum: 4, min: 2, max: 2, buckets: "2,0", bounds: Bounds),
            ]);

        var result = await CreateService().GetSeries(name, Query(Now.AddHours(-1), Now, percentile: 95), Ct);

        // Only the first row's ladder (5,3,2,0 over 10) feeds the percentile: ceil(0.95 * 10) = 10 is reached in the
        // third bucket. Count and sum still include every row.
        var point = result.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.ShouldHaveSingleItem();
        point.Count.ShouldBe(17);
        point.Sum.ShouldBe(124);
        point.Percentile.ShouldBe(1000);
    }

    [TimedFact]
    public async Task GetSeries_RangeOverTheServiceCap_ThrowsMetricQueryValidationException()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], []);

        var exception = await Should.ThrowAsync<MetricQueryValidationException>(
            () => CreateService().GetSeries("c", Query(Now.AddDays(-(IMetricQueryService.MaxRangeDays + 1)), Now), Ct));

        exception.Message.ShouldContain($"{IMetricQueryService.MaxRangeDays} days");
    }

    [TimedFact]
    public async Task GetSeries_MoreFiltersThanTheServiceCap_ThrowsMetricQueryValidationException()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], []);
        var filters = Enumerable.Range(0, IMetricQueryService.MaxFilters + 1).ToDictionary(x => $"k{x}", _ => "v", StringComparer.Ordinal);

        await Should.ThrowAsync<MetricQueryValidationException>(
            () => CreateService().GetSeries("c", Query(Now.AddHours(-1), Now, filters: filters), Ct));
    }

    [TimedFact]
    public async Task GetSeries_HistogramOverflowBucket_ReportsTheLargestFiniteBound()
    {
        await SeedAsync(
            [Instrument("h", MetricKind.Histogram, Bounds)],
            [Point("h", Now.AddMinutes(-20), count: 4, sum: 40000, min: 5000, max: 9000, buckets: "0,0,0,4", bounds: Bounds)]);

        var result = await CreateService().GetSeries("h", Query(Now.AddHours(-1), Now, percentile: 95), Ct);

        result.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.ShouldHaveSingleItem().Percentile.ShouldBe(1000);
    }

    [TimedFact]
    public async Task GetSeries_GaugePartialRows_ReturnsAverageMinAndMax()
    {
        var bucket = Now.AddMinutes(-20);
        await SeedAsync(
            [Instrument("g", MetricKind.Gauge)],
            [
                Point("g", bucket, count: 2, sum: 10, min: 3, max: 7),
                Point("g", bucket, count: 3, sum: 30, min: 1, max: 20),
            ]);

        var result = await CreateService().GetSeries("g", Query(Now.AddHours(-1), Now), Ct);

        var point = result.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.ShouldHaveSingleItem();
        point.Value.ShouldBe(8);
        point.Min.ShouldBe(1);
        point.Max.ShouldBe(20);
    }

    [TimedFact]
    public async Task GetSeries_ApplicationFilter_ReadsOnlyThatApplication()
    {
        var bucket = Now.AddMinutes(-30);
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", bucket, sum: 1, application: "orders"),
                Point("c", bucket, sum: 10, application: "billing"),
            ]);

        var result = await CreateService().GetSeries("c", Query(Now.AddHours(-1), Now, application: "orders"), Ct);

        result.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.ShouldHaveSingleItem().Sum.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetSeries_ApplicationFilterWithCommaAndOverLongName_MatchesTheSanitizedStoredForm()
    {
        // The collector stores MetricSeries.Sanitize(ApplicationName): ',' and '=' become '-', capped at 64 characters.
        var longName = new string('a', 70);
        var bucket = Now.AddMinutes(-30);
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", bucket, sum: 1, application: "shop-eu-west"),
                Point("c", bucket, sum: 10, application: new string('a', 64)),
            ]);

        var service = CreateService();
        var comma = await service.GetSeries("c", Query(Now.AddHours(-1), Now, application: " shop,eu=west "), Ct);
        var tooLong = await service.GetSeries("c", Query(Now.AddHours(-1), Now, application: longName), Ct);

        comma.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.ShouldHaveSingleItem().Sum.ShouldBe(1);
        tooLong.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.ShouldHaveSingleItem().Sum.ShouldBe(10);
    }

    [TimedFact]
    public async Task GetSeries_NoPointsInRange_ReturnsOneEmptyTotalLine()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], []);

        var result = await CreateService().GetSeries("c", Query(Now.AddHours(-1), Now), Ct);

        var line = result.ShouldNotBeNull().Series.ShouldHaveSingleItem();
        line.Name.ShouldBe("total");
        line.Points.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task GetMetrics_Route_ListsTheRegistry()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], []);
        var (app, client) = await CreateDashboardHost();
        try
        {
            var response = await client.GetAsync("/warp/api/metrics", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            json.RootElement.GetArrayLength().ShouldBe(1);
            json.RootElement[0].GetProperty("name").GetString().ShouldBe("c");
        }
        finally
        {
            await DisposeHostAsync(app, client);
        }
    }

    [TimedFact]
    public async Task GetMetricSeries_Route_ReturnsTheSeriesLabelledWithItsResolution()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddMinutes(-10), sum: 3)]);
        var (app, client) = await CreateDashboardHost();
        try
        {
            var response = await client.GetAsync(SeriesUrl("c", Now.AddHours(-1), Now), Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var line = json.RootElement.GetProperty("series")[0];
            line.GetProperty("name").GetString().ShouldBe("total");
            line.GetProperty("resolution").GetInt32().ShouldBe((int)MetricTier.Fine);
            line.GetProperty("points")[0].GetProperty("sum").GetDouble().ShouldBe(3);
        }
        finally
        {
            await DisposeHostAsync(app, client);
        }
    }

    [TimedFact]
    public async Task GetMetricSeries_UndecodableId_ReturnsNotFound()
    {
        var (app, client) = await CreateDashboardHost();
        try
        {
            var response = await client.GetAsync($"/warp/api/metrics/!!!/series?{Range(Now.AddHours(-1), Now)}", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await DisposeHostAsync(app, client);
        }
    }

    [TimedFact]
    public async Task GetMetricSeries_UnknownInstrument_ReturnsNotFound()
    {
        var (app, client) = await CreateDashboardHost();
        try
        {
            var response = await client.GetAsync(SeriesUrl("missing", Now.AddHours(-1), Now), Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await DisposeHostAsync(app, client);
        }
    }

    [TimedTheory]
    [InlineData("")]
    [InlineData("from=2026-11-13T10:00:00Z")]
    [InlineData("from=nonsense&to=2026-11-13T10:00:00Z")]
    [InlineData("from=2026-11-13T10:00:00Z&to=2026-11-13T09:00:00Z")]
    [InlineData("from=2026-11-13T10:00:00Z&to=2026-11-13T10:00:00Z")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2026-11-13T10:00:00Z")]
    [InlineData("from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&percentile=0")]
    [InlineData("from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&percentile=100")]
    [InlineData("from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&percentile=p95")]
    [InlineData("from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&compare=2d")]
    [InlineData("from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&compare=1d,30d")]
    [InlineData("from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&filter=nocolon")]
    [InlineData("from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&filter=:value")]
    [InlineData("from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&filter=key:")]
    [InlineData("from=0001-01-01T00:00:00Z&to=0001-01-02T00:00:00Z&compare=7d")]
    [InlineData("from=9999-12-31T00:00:00Z&to=9999-12-31T12:00:00Z")]
    [InlineData("from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&filter=a:1&filter=a:2")]
    [InlineData("from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&filter=a:x*y")]
    [InlineData("from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z&filter=a*:1")]
    [InlineData("from=2026-11-13 09:00:00&to=2026-11-13T10:00:00Z")]
    [InlineData("from=11/13/2026 09:00:00&to=2026-11-13T10:00:00Z")]
    [MemberData(nameof(OverlongQueries))]
    public async Task GetMetricSeries_InvalidQuery_ReturnsBadRequest(string queryString)
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], []);
        var (app, client) = await CreateDashboardHost();
        try
        {
            var response = await client.GetAsync($"/warp/api/metrics/{UrlSafeId.Encode("c")}/series?{queryString}", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            await DisposeHostAsync(app, client);
        }
    }

    [TimedTheory]
    [InlineData("from=2026-11-13T09:00:00Z&to=2026-11-13T10:00:00Z")]
    [InlineData("from=2026-11-13T09:00:00.000Z&to=2026-11-13T10:00:00.000Z")]
    [InlineData("from=2026-11-13T09:00:00.0000000Z&to=2026-11-13T10:00:00.0000000Z")]
    [InlineData("from=2026-11-13T09:00:00.0000000%2B00:00&to=2026-11-13T10:00:00.0000000%2B00:00")]
    public async Task GetMetricSeries_IsoInstantForms_ReturnOk(string queryString)
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], []);
        var (app, client) = await CreateDashboardHost();
        try
        {
            var response = await client.GetAsync($"/warp/api/metrics/{UrlSafeId.Encode("c")}/series?{queryString}", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await DisposeHostAsync(app, client);
        }
    }

    [TimedFact]
    public async Task GetMetricSeries_BreakdownAndFilter_AreParsedFromTheQueryString()
    {
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", Now.AddMinutes(-10), sum: 1, tags: "route=a,status=200"),
                Point("c", Now.AddMinutes(-10), sum: 10, tags: "route=b,status=503"),
            ]);
        var (app, client) = await CreateDashboardHost();
        try
        {
            var response = await client.GetAsync(SeriesUrl("c", Now.AddHours(-1), Now) + "&breakdown=route&filter=status:5*", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var line = json.RootElement.GetProperty("series").EnumerateArray().ShouldHaveSingleItem();
            line.GetProperty("name").GetString().ShouldBe("b");
            line.GetProperty("points")[0].GetProperty("sum").GetDouble().ShouldBe(10);
        }
        finally
        {
            await DisposeHostAsync(app, client);
        }
    }

    [TimedFact]
    public async Task GetMetricSeries_CompareList_ReturnsOneLabelledOverlayPerOffset()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddMinutes(-10), sum: 1)]);
        var (app, client) = await CreateDashboardHost();
        try
        {
            var response = await client.GetAsync(SeriesUrl("c", Now.AddHours(-1), Now) + "&compare=1d,7d", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var series = json.RootElement.GetProperty("series").EnumerateArray().ToList();
            series.Count.ShouldBe(3);
            series[0].GetProperty("compare").ValueKind.ShouldBe(JsonValueKind.Null);
            series[1].GetProperty("compare").GetString().ShouldBe("1d");
            series[2].GetProperty("compare").GetString().ShouldBe("7d");
        }
        finally
        {
            await DisposeHostAsync(app, client);
        }
    }

    [TimedFact]
    public async Task GetSeries_CurrentBucketHalfElapsed_FlagsItIncompleteAndTheHeadlineLeavesItOut()
    {
        var clock = Now.AddMinutes(2.5);
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", Now.AddMinutes(-10), count: 2, sum: 10),
                Point("c", Now.AddMinutes(-5), count: 4, sum: 20),
                Point("c", Now, count: 1, sum: 3),
            ]);

        var result = await CreateService(clock).GetSeries("c", Query(clock.AddHours(-1), clock), Ct);

        result.ShouldNotBeNull();
        var points = result.Series.ShouldHaveSingleItem().Points;
        points.Select(x => (x.Timestamp, x.Incomplete)).ShouldBe([(Now.AddMinutes(-10), false), (Now.AddMinutes(-5), false), (Now, true)]);
        points[^1].Sum.ShouldBe(3);
        var headline = result.Headlines.ShouldHaveSingleItem();
        headline.Compare.ShouldBeNull();
        headline.Value.ShouldBe(30);
        headline.Count.ShouldBe(6);
        result.CompleteUntil.ShouldBe(Now);
    }

    [TimedFact]
    public async Task GetSeries_BucketEndedWithinOneFlushInterval_StaysIncomplete()
    {
        var clock = Now.AddSeconds(10);
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", Now.AddMinutes(-10), sum: 10),
                Point("c", Now.AddMinutes(-5), sum: 20),
            ]);

        var result = await CreateService(clock).GetSeries("c", Query(clock.AddHours(-1), clock), Ct);

        var points = result.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points;
        points.Select(x => x.Incomplete).ShouldBe([false, true]);
        result.Headlines.ShouldHaveSingleItem().Value.ShouldBe(10);
        result.CompleteUntil.ShouldBe(Now.AddMinutes(-5));
    }

    [TimedFact]
    public async Task GetSeries_BucketClosedForLongerThanAFlushInterval_IsComplete()
    {
        var clock = Now.AddSeconds(31);
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddMinutes(-5), sum: 20)]);

        var result = await CreateService(clock).GetSeries("c", Query(clock.AddHours(-1), clock), Ct);

        result.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.ShouldHaveSingleItem().Incomplete.ShouldBeFalse();
        result.Headlines.ShouldHaveSingleItem().Value.ShouldBe(20);
    }

    [TimedFact]
    public async Task GetSeries_CompareWithAFillingBucket_OverlayHeadlineLeavesOutTheAlignedBucketAndFlagsNothing()
    {
        var clock = Now.AddMinutes(2.5);
        var yesterday = Now.AddDays(-1);
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", Now.AddMinutes(-5), sum: 20),
                Point("c", Now, sum: 3),
                Point("c", yesterday.AddMinutes(-5), sum: 5),
                Point("c", yesterday, sum: 7),
            ]);

        var result = await CreateService(clock).GetSeries("c", Query(clock.AddHours(-1), clock, compare: [MetricCompareOffset.Day]), Ct);

        result.ShouldNotBeNull();
        result.Headlines.Select(x => (x.Compare, x.Value)).ShouldBe([(null, 20d), ("1d", 5d)]);
        var overlay = result.Series.Single(x => string.Equals(x.Compare, "1d", StringComparison.Ordinal));
        overlay.Points.Select(x => (x.Timestamp, x.Sum, x.Incomplete)).ShouldBe([(Now.AddMinutes(-5), 5d, false), (Now, 7d, false)]);
    }

    [TimedFact]
    public async Task GetSeries_BreakdownWithAFillingBucket_HeadlineCoversEveryLineIncludingRest()
    {
        var clock = Now.AddMinutes(2.5);
        var points = Enumerable.Range(0, 12)
            .SelectMany(x =>
                new[]
                {
                    Point("c", Now.AddMinutes(-5), sum: x + 1, tags: $"route=r{x:D2}"),
                    Point("c", Now, sum: 100, tags: $"route=r{x:D2}"),
                })
            .ToArray();
        await SeedAsync([Instrument("c", MetricKind.Counter)], points);

        var result = await CreateService(clock).GetSeries("c", Query(clock.AddHours(-1), clock, breakdown: "route"), Ct);

        result.ShouldNotBeNull();
        result.Series.Select(x => x.Name).ShouldContain("(rest)");
        result.Series.ShouldAllBe(x => x.Points.Single(y => y.Timestamp == Now).Incomplete);
        result.Headlines.ShouldHaveSingleItem().Value.ShouldBe(78);
    }

    [TimedFact]
    public async Task GetSeries_GaugeWithAFillingBucket_HeadlineAveragesTheCompleteBucketsOnly()
    {
        var clock = Now.AddMinutes(2.5);
        await SeedAsync(
            [Instrument("g", MetricKind.Gauge)],
            [
                Point("g", Now.AddMinutes(-10), count: 2, sum: 20),
                Point("g", Now.AddMinutes(-5), count: 2, sum: 40),
                Point("g", Now, count: 1, sum: 1000),
            ]);

        var result = await CreateService(clock).GetSeries("g", Query(clock.AddHours(-1), clock), Ct);

        result.ShouldNotBeNull().Headlines.ShouldHaveSingleItem().Value.ShouldBe(15);
    }

    [TimedFact]
    public async Task GetSeries_OnlyTheFillingBucketHasData_HeadlineIsNull()
    {
        var clock = Now.AddMinutes(2.5);
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now, sum: 3)]);

        var result = await CreateService(clock).GetSeries("c", Query(clock.AddHours(-1), clock), Ct);

        var headline = result.ShouldNotBeNull().Headlines.ShouldHaveSingleItem();
        headline.Value.ShouldBeNull();
        headline.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task GetSeries_RangeEndingInThePast_FlagsNothingAndCompleteUntilIsTheRangeEnd()
    {
        var clock = Now.AddDays(1);
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddMinutes(-5), sum: 20)]);

        var result = await CreateService(clock).GetSeries("c", Query(Now.AddHours(-1), Now), Ct);

        result.ShouldNotBeNull().Series.ShouldHaveSingleItem().Points.ShouldAllBe(x => !x.Incomplete);
        result.CompleteUntil.ShouldBe(Now);
        result.Headlines.ShouldHaveSingleItem().Value.ShouldBe(20);
    }

    [TimedFact]
    public async Task GetMetricSeries_FillingBucket_SerializesTheFlagAndTheHeadlines()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddMinutes(-10), sum: 4), Point("c", Now, sum: 1)]);
        var (app, client) = await CreateDashboardHost(Now.AddMinutes(2.5));
        try
        {
            var response = await client.GetAsync(SeriesUrl("c", Now.AddHours(-1), Now.AddMinutes(2.5)), Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var points = json.RootElement.GetProperty("series")[0].GetProperty("points").EnumerateArray().ToList();
            points.Select(x => x.GetProperty("incomplete").GetBoolean()).ShouldBe([false, true]);
            var headline = json.RootElement.GetProperty("headlines").EnumerateArray().ShouldHaveSingleItem();
            headline.GetProperty("value").GetDouble().ShouldBe(4);
            json.RootElement.GetProperty("completeUntil").GetDateTime().ShouldBe(Now);
        }
        finally
        {
            await DisposeHostAsync(app, client);
        }
    }

    private static MetricSeriesQuery Query(
        DateTime from,
        DateTime to,
        string? application = null,
        string? breakdown = null,
        IReadOnlyDictionary<string, string>? filters = null,
        MetricCompareOffset[]? compare = null,
        int? percentile = null)
        => new()
        {
            From = from,
            To = to,
            Application = application,
            Breakdown = breakdown,
            Filters = filters ?? new Dictionary<string, string>(),
            Compare = compare ?? [],
            Percentile = percentile,
        };

    private static MetricInstrument Instrument(string name, MetricKind kind, string? bounds = null)
        => new()
        {
            Name = name,
            Meter = "test",
            Kind = kind,
            Bounds = bounds,
            FirstSeenAt = Now.AddDays(-2),
            LastSeenAt = Now,
        };

    private static MetricPoint Point(
        string instrument,
        DateTime bucket,
        long count = 1,
        double sum = 0,
        double? min = null,
        double? max = null,
        string tags = "-",
        string? application = null,
        MetricTier tier = MetricTier.Fine,
        bool compacted = false,
        string? buckets = null,
        string? bounds = null)
        => new()
        {
            Instrument = instrument,
            Application = application,
            Tags = tags,
            Tier = tier,
            BucketStart = bucket,
            Count = count,
            Sum = sum,
            Min = min,
            Max = max,
            Buckets = buckets,
            Bounds = bounds,
            Compacted = compacted,
        };

    private static List<(DateTime Timestamp, long Count, double Sum, double? Min, double? Max)> Project(IReadOnlyList<MetricPointModel> points)
        => [.. points.Select(x => (x.Timestamp, x.Count, x.Sum, x.Min, x.Max))];

    // Rows across all three tiers and two tag sets, with a split hour and partial fine rows: what a 24h read meets.
    private static List<MetricPoint> MultiTierRows(string instrument, string? bounds, string? buckets)
    {
        var rows = new List<MetricPoint>();
        var yesterdayNoon = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 18; i++)
        {
            rows.Add(Point(instrument, yesterdayNoon.AddHours(i), count: 10 + i, sum: 100 + i, min: 1 + i, max: 50 + i, tags: "route=a", tier: MetricTier.Hourly, compacted: true, buckets: buckets, bounds: bounds));
            rows.Add(Point(instrument, yesterdayNoon.AddHours(i), count: 3, sum: 30, min: 2, max: 9, tags: "route=b", tier: MetricTier.Hourly, compacted: true, buckets: buckets, bounds: bounds));
        }

        var six = new DateTime(2026, 11, 13, 6, 0, 0, DateTimeKind.Utc);
        rows.Add(Point(instrument, six.AddMinutes(-5), count: 4, sum: 40, min: 3, max: 12, tags: "route=a", compacted: true, buckets: buckets, bounds: bounds));
        rows.Add(Point(instrument, six, count: 5, sum: 50, min: 4, max: 20, tags: "route=a", compacted: true, buckets: buckets, bounds: bounds));
        rows.Add(Point(instrument, six, count: 2, sum: 20, min: 6, max: 14, tags: "route=a", buckets: buckets, bounds: bounds));
        rows.Add(Point(instrument, six, count: 1, sum: 5, min: 5, max: 5, tags: "route=b", buckets: buckets, bounds: bounds));
        rows.Add(Point(instrument, six.AddMinutes(5), count: 6, sum: 60, min: 1, max: 99, tags: "route=b", buckets: buckets, bounds: bounds));
        rows.Add(Point(instrument, Now.AddMinutes(-5), count: 7, sum: 70, min: 2, max: 30, tags: "route=a", buckets: buckets, bounds: bounds));

        return rows;
    }

    private static string Range(DateTime from, DateTime to)
        => $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    private static string SeriesUrl(string instrument, DateTime from, DateTime to)
        => $"/warp/api/metrics/{UrlSafeId.Encode(instrument)}/series?{Range(from, to)}";

    private static async Task DisposeHostAsync(WebApplication app, HttpClient client)
    {
        client.Dispose();
        await app.DisposeAsync();
    }

    private MetricQueryService<TestContext> CreateService()
        => new(_fixture.CreateContext(), Options.Create(new WarpConfiguration()));

    private MetricQueryService<TestContext> CreateService(DateTime now)
        => new(_fixture.CreateContext(), Options.Create(new WarpConfiguration()), new FakeTimeProvider(new DateTimeOffset(now)));

    private async Task SeedAsync(MetricInstrument[] instruments, MetricPoint[] points)
    {
        var context = _fixture.CreateContext();
        context.Set<MetricInstrument>().AddRange(instruments);
        context.Set<MetricPoint>().AddRange(points);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<(WebApplication App, HttpClient Client)> CreateDashboardHost(DateTime? now = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        builder.Services.AddScoped<IMetricQueryService>(_ => new MetricQueryService<TestContext>(
            _fixture.CreateContext(),
            Options.Create(new WarpConfiguration()),
            now is null ? TimeProvider.System : new FakeTimeProvider(new DateTimeOffset(now.Value))));

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);

        await app.StartAsync(CancellationToken.None);

        return (app, app.GetTestClient());
    }
}
