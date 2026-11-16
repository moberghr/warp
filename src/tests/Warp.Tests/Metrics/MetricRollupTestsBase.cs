using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Metrics;

/// <summary>
/// Database coverage for <see cref="MetricRollup{TContext}"/> (R15–R18): compaction exactness and the closed-bucket
/// margin, the fine → hourly → daily passes and daily pruning, bounds kept apart, the idle null return,
/// compaction-before-roll and the hourly-row prune past daily retention. The two heavy cap tests live in
/// <see cref="MetricRollupHeavyTestsBase"/>, serialized with the other heavy classes (§4.7.1).
/// <para>
/// Every stamp is relative to the fixed instant <see cref="Now"/>, which a <see cref="FakeTimeProvider"/> hands the
/// rollup: a stamp relative to the real clock can cross a retention edge between the seed and a later tick (seen on
/// SQL Server under load, a bucket crossing the 6h fine-retention edge between two ticks). <see cref="Rollable"/> is
/// past the 7-day hourly window and inside the 90-day daily window, <see cref="Ancient"/> is past the daily window.
/// Each test drives one class's method (§4.8).
/// </para>
/// </summary>
[GenerateDatabaseTests]
public abstract class MetricRollupTestsBase : IAsyncLifetime
{
    internal const string Bounds = "10,100";
    internal const string OtherBounds = "5,50";

    // On a 5-minute boundary, so offsets from it land on whole fine buckets.
    internal static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    // FineResolutionMinutes + 2 × MetricFlushInterval of Configuration: the closed-bucket margin R15 compacts behind.
    private static readonly TimeSpan CompactionMargin = TimeSpan.FromMinutes(5) + (2 * TimeSpan.FromSeconds(30));

    private readonly IDatabaseFixture _fixture;

    protected MetricRollupTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static DateTime Rollable => Bucket(MetricTier.Fine, Now.AddDays(-8));

    internal static DateTime Ancient => Bucket(MetricTier.Fine, Now.AddDays(-100));

    // Closed long enough to compact, too young to roll to hourly.
    internal static DateTime Closed => Bucket(MetricTier.Fine, Now.AddHours(-1));

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task ExecuteAsync_ThreePartialRowsOfOneSeries_CompactIntoOneExactRow()
    {
        await SeedAsync(
            Histogram("h", Closed, count: 2, sum: 30, min: 5, max: 25, buckets: "1,1,0"),
            Histogram("h", Closed, count: 3, sum: 300, min: 50, max: 150, buckets: "0,2,1"),
            Histogram("h", Closed, count: 1, sum: 2, min: 2, max: 2, buckets: "1,0,0"));

        await RunAsync();

        var rows = await RowsAsync("h");
        var row = rows.ShouldHaveSingleItem();
        row.Tier.ShouldBe(MetricTier.Fine);
        row.Compacted.ShouldBeTrue();
        row.BucketStart.ShouldBe(Closed);
        row.Count.ShouldBe(6);
        row.Sum.ShouldBe(332);
        row.Min.ShouldBe(2);
        row.Max.ShouldBe(150);
        row.Buckets.ShouldBe("2,3,1");
        row.Bounds.ShouldBe(Bounds);
    }

    [TimedFact]
    public async Task ExecuteAsync_MalformedBucketsRowInACompactionGroup_CompactsExactCountAndSum_DropsItsDetail_AndProceeds()
    {
        // One partial row's bucket CSV is corrupt. It used to throw inside the lock transaction on every tick, and
        // being the oldest row it was picked first every time: the rollup stalled for good.
        var older = Closed.AddMinutes(-5);
        await SeedAsync(
            Histogram("h", Closed, count: 2, sum: 30, min: 5, max: 25, buckets: "1,1,0"),
            Histogram("h", Closed, count: 3, sum: 300, min: 50, max: 150, buckets: "0,x,1"),
            Histogram("h", Closed, count: 1, sum: 2, min: 2, max: 2, buckets: "1,0,0"),
            Histogram("m", older, count: 4, sum: 40, min: 1, max: 20, buckets: "2,2"),
            Histogram("m", older, count: 1, sum: 200, min: 200, max: 200, buckets: "0,0,1"),
            Counter("c", Closed, 1),
            Counter("c", Closed, 2));
        var rollup = CreateRollup(_fixture, TimeSpan.FromDays(90));

        var result = await rollup.ExecuteAsync(Ct);

        result.ShouldNotBeNull();
        var row = (await RowsAsync("h")).ShouldHaveSingleItem();
        row.Compacted.ShouldBeTrue();
        row.Count.ShouldBe(6);
        row.Sum.ShouldBe(332);
        row.Min.ShouldBe(2);
        row.Max.ShouldBe(150);
        row.Buckets.ShouldBe("2,1,0");

        // A row whose detail does not match its bounds (two counts for a two-bound ladder) keeps its counts too.
        var mismatched = (await RowsAsync("m")).ShouldHaveSingleItem();
        mismatched.Compacted.ShouldBeTrue();
        mismatched.Count.ShouldBe(5);
        mismatched.Sum.ShouldBe(240);
        mismatched.Buckets.ShouldBe("0,0,1");

        // The tick went past the bad rows: the other series compacted in the same tick.
        var counter = (await RowsAsync("c")).ShouldHaveSingleItem();
        counter.Compacted.ShouldBeTrue();
        counter.Count.ShouldBe(3);
        rollup.MalformedBucketRows.ShouldBe(2);
        (await RunAsync()).ShouldBeNull();
    }

    [TimedFact]
    public async Task ExecuteAsync_OpenBucket_LeftUncompacted()
    {
        // The current bucket is still open (and inside the 2 × flush interval margin), so its partial rows stay.
        var open = Bucket(MetricTier.Fine, Now);
        await SeedAsync(Counter("c", open, 1), Counter("c", open, 2));

        var result = await RunAsync();

        result.ShouldBeNull();
        var rows = await RowsAsync("c");
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(x => !x.Compacted);
    }

    /// <summary>
    /// R15's closed-bucket margin: a bucket compacts only once <c>BucketStart + FineResolutionMinutes +
    /// 2 × MetricFlushInterval &lt; now</c>, strictly. Up to and including the margin edge a late flush could still
    /// add a partial row, so the bucket stays partial; one second past it, it compacts.
    /// </summary>
    [TimedTheory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task ExecuteAsync_BucketAtTheCompactionMargin_CompactsOnlyOnceStrictlyPastIt(int secondsPastMargin, bool compacts)
    {
        var bucket = Bucket(MetricTier.Fine, Now.AddHours(-2));
        await SeedAsync(Counter("c", bucket, 1), Counter("c", bucket, 2));
        var now = bucket + CompactionMargin + TimeSpan.FromSeconds(secondsPastMargin);

        var result = await CreateRollup(_fixture, TimeSpan.FromDays(90), now: now).ExecuteAsync(Ct);

        var rows = await RowsAsync("c");
        if (compacts)
        {
            result.ShouldNotBeNull();
            var row = rows.ShouldHaveSingleItem();
            row.Compacted.ShouldBeTrue();
            row.Count.ShouldBe(3);

            return;
        }

        result.ShouldBeNull();
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(x => !x.Compacted);
    }

    [TimedFact]
    public async Task ExecuteAsync_CompactedFineRowsOfOneHour_SumIntoTheHourlyRow()
    {
        var hour = Bucket(MetricTier.Hourly, Now.AddDays(-1));
        await SeedAsync(
            Counter("c", hour, 4, compacted: true),
            Counter("c", hour.AddMinutes(5), 6, compacted: true),
            Counter("c", hour, 10, tier: MetricTier.Hourly, compacted: true));

        await RunAsync();

        var row = (await RowsAsync("c")).ShouldHaveSingleItem();
        row.Tier.ShouldBe(MetricTier.Hourly);
        row.BucketStart.ShouldBe(hour);
        row.Count.ShouldBe(20);
        row.Sum.ShouldBe(20);
    }

    [TimedFact]
    public async Task ExecuteAsync_HourlyRowPastHourlyRetention_SumsIntoTheDailyRow()
    {
        var hour = Bucket(MetricTier.Hourly, Rollable);
        await SeedAsync(
            Histogram("h", hour, count: 2, sum: 20, min: 1, max: 19, buckets: "1,1,0", tier: MetricTier.Hourly, compacted: true),
            Histogram("h", Bucket(MetricTier.Daily, hour), count: 1, sum: 500, min: 500, max: 500, buckets: "0,0,1", tier: MetricTier.Daily, compacted: true));

        await RunAsync();

        var row = (await RowsAsync("h")).ShouldHaveSingleItem();
        row.Tier.ShouldBe(MetricTier.Daily);
        row.BucketStart.ShouldBe(Bucket(MetricTier.Daily, hour));
        row.Count.ShouldBe(3);
        row.Sum.ShouldBe(520);
        row.Min.ShouldBe(1);
        row.Max.ShouldBe(500);
        row.Buckets.ShouldBe("1,1,1");
    }

    [TimedFact]
    public async Task ExecuteAsync_DailyRowPastDailyRetention_Pruned()
    {
        await SeedAsync(
            Counter("c", Bucket(MetricTier.Daily, Ancient), 5, tier: MetricTier.Daily, compacted: true),
            Counter("c", Bucket(MetricTier.Daily, Rollable), 7, tier: MetricTier.Daily, compacted: true));

        await RunAsync();

        var row = (await RowsAsync("c")).ShouldHaveSingleItem();
        row.Count.ShouldBe(7);
    }

    [TimedFact]
    public async Task ExecuteAsync_NullDailyRetention_KeepsEveryDailyRowForever()
    {
        await SeedAsync(
            Counter("c", Bucket(MetricTier.Daily, Ancient), 5, tier: MetricTier.Daily, compacted: true),
            Counter("c", Bucket(MetricTier.Hourly, Ancient), 3, tier: MetricTier.Hourly, compacted: true));

        await RunAsync(dailyRetention: null);

        var row = (await RowsAsync("c")).ShouldHaveSingleItem();
        row.Tier.ShouldBe(MetricTier.Daily);
        row.Count.ShouldBe(8);
    }

    [TimedFact]
    public async Task ExecuteAsync_SameSeriesWithDifferentBounds_NeverMerges()
    {
        // Partial fine rows old enough to pass through every tier in one tick: compact, roll to hourly, roll to daily.
        await SeedAsync(
            Histogram("h", Rollable, count: 1, sum: 1, min: 1, max: 1, buckets: "1,0,0"),
            Histogram("h", Rollable, count: 1, sum: 60, min: 60, max: 60, buckets: "0,1,0"),
            Histogram("h", Rollable, count: 1, sum: 7, min: 7, max: 7, buckets: "0,1,0", bounds: OtherBounds),
            Histogram("h", Rollable, count: 1, sum: 70, min: 70, max: 70, buckets: "0,0,1", bounds: OtherBounds));

        await RunAsync();

        var rows = await RowsAsync("h");
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(x => x.Tier == MetricTier.Daily);

        var first = rows.Single(x => string.Equals(x.Bounds, Bounds, StringComparison.Ordinal));
        first.Count.ShouldBe(2);
        first.Sum.ShouldBe(61);
        first.Buckets.ShouldBe("1,1,0");

        var second = rows.Single(x => string.Equals(x.Bounds, OtherBounds, StringComparison.Ordinal));
        second.Count.ShouldBe(2);
        second.Sum.ShouldBe(77);
        second.Buckets.ShouldBe("0,1,1");
    }

    [TimedFact]
    public async Task ExecuteAsync_IdleTick_ReturnsNull()
    {
        await SeedAsync(Counter("c", Bucket(MetricTier.Daily, Rollable), 1, tier: MetricTier.Daily, compacted: true));

        var result = await RunAsync();

        result.ShouldBeNull();
    }

    [TimedFact]
    public async Task ExecuteAsync_PartialRowWithUncompactedSibling_IsNotRolledBeforeItIsCompacted()
    {
        // Past fine retention, so both rows are eligible to roll once compacted. A cap of one forces the first tick
        // to stop after compacting a single row: that row must not reach the hourly tier while its sibling is still
        // partial.
        var bucket = Bucket(MetricTier.Fine, Now.AddDays(-1));
        await SeedAsync(Counter("c", bucket, 3), Counter("c", bucket, 4));

        await RunAsync(cap: 1);

        var afterFirst = await RowsAsync("c");
        afterFirst.ShouldAllBe(x => x.Tier == MetricTier.Fine);
        afterFirst.Count(x => x.Compacted).ShouldBe(1);
        afterFirst.Count(x => !x.Compacted).ShouldBe(1);

        await RunAsync(cap: 1);

        var afterSecond = (await RowsAsync("c")).ShouldHaveSingleItem();
        afterSecond.Tier.ShouldBe(MetricTier.Fine);
        afterSecond.Compacted.ShouldBeTrue();
        afterSecond.Count.ShouldBe(7);

        await RunAsync(cap: 1);

        var afterThird = (await RowsAsync("c")).ShouldHaveSingleItem();
        afterThird.Tier.ShouldBe(MetricTier.Hourly);
        afterThird.Count.ShouldBe(7);
    }

    [TimedFact]
    public async Task ExecuteAsync_HourlyRowWhoseDayIsPastDailyRetention_IsPrunedNotRolled()
    {
        // The hourly row is past both retentions: rolling it would create a daily row the same pass must delete, so
        // it is pruned directly. The younger hourly row of the same series still rolls into its daily row.
        var expiredHour = Bucket(MetricTier.Hourly, Ancient);
        var rollableHour = Bucket(MetricTier.Hourly, Rollable);
        await SeedAsync(
            Counter("c", expiredHour, 5, tier: MetricTier.Hourly, compacted: true),
            Counter("c", rollableHour, 7, tier: MetricTier.Hourly, compacted: true));

        var result = await RunAsync();

        result.ShouldNotBeNull();
        result.ShouldContain("pruned 1");
        var row = (await RowsAsync("c")).ShouldHaveSingleItem();
        row.Tier.ShouldBe(MetricTier.Daily);
        row.BucketStart.ShouldBe(Bucket(MetricTier.Daily, rollableHour));
        row.Count.ShouldBe(7);
    }

    // Several series spread over every pass: partial fine rows that compact and roll all the way to daily,
    // compacted fine rows that roll to hourly, hourly rows that roll into an existing daily row, and a second bounds.
    internal static List<MetricPoint> BacklogAcrossTiers(string instrument)
    {
        var recent = Bucket(MetricTier.Fine, Now.AddDays(-1));
        var points = new List<MetricPoint>();
        for (var series = 0; series < 3; series++)
        {
            var tags = $"s={series}";
            for (var i = 0; i < 3; i++)
            {
                points.Add(Histogram(instrument, Rollable.AddMinutes(5 * i), count: 1, sum: i + 1, min: i + 1, max: i + 1, buckets: "1,0,0", tags: tags));
                points.Add(Histogram(instrument, Rollable, count: 2, sum: 200, min: 100, max: 100, buckets: "0,0,2", tags: tags, bounds: OtherBounds));
                points.Add(Histogram(instrument, recent.AddMinutes(5 * i), count: 1, sum: 50, min: 50, max: 50, buckets: "0,1,0", tags: tags, compacted: true));
            }

            points.Add(Histogram(instrument, Bucket(MetricTier.Hourly, Rollable), count: 4, sum: 40, min: 10, max: 10, buckets: "0,4,0", tags: tags, tier: MetricTier.Hourly, compacted: true));
            points.Add(Histogram(instrument, Bucket(MetricTier.Daily, Rollable), count: 1, sum: 9, min: 9, max: 9, buckets: "1,0,0", tags: tags, tier: MetricTier.Daily, compacted: true));
        }

        return points;
    }

    // One "tags|bounds=count;sum;buckets" line per series, summed over every tier and bucket, ordinal-sorted.
    internal static List<string> Totals(IEnumerable<(string Tags, string? Bounds, long Count, double Sum, string? Buckets)> rows)
        =>
        [
            .. rows
                .GroupBy(x => $"{x.Tags}|{x.Bounds}", StringComparer.Ordinal)
                .Select(x =>
                {
                    var buckets = x
                        .Select(y => y.Buckets!.Split(',').Select(z => long.Parse(z, CultureInfo.InvariantCulture)).ToArray())
                        .Aggregate((a, b) => [.. a.Zip(b, (p, q) => p + q)]);

                    return $"{x.Key}={x.Sum(y => y.Count)};{x.Sum(y => y.Sum).ToString(CultureInfo.InvariantCulture)};{string.Join(',', buckets)}";
                })
                .Order(StringComparer.Ordinal),
        ];

    internal static List<string> Layout(List<MetricPoint> rows)
        =>
        [
            .. rows
                .Select(x => $"{x.Tags}|{x.Bounds}|{x.Tier}|{x.BucketStart:O}|{x.Compacted}|{x.Count}|{x.Buckets}")
                .Order(StringComparer.Ordinal),
        ];

    internal static DateTime Bucket(MetricTier tier, DateTime utc) => MetricTiers.BucketStart(tier, utc, 5);

    internal static MetricPoint Counter(string instrument, DateTime bucket, long count, MetricTier tier = MetricTier.Fine, bool compacted = false)
        => new()
        {
            Instrument = instrument,
            Tags = "-",
            Tier = tier,
            BucketStart = bucket,
            Count = count,
            Sum = count,
            Compacted = compacted,
        };

    internal static MetricPoint Histogram(
        string instrument,
        DateTime bucket,
        long count,
        double sum,
        double min,
        double max,
        string buckets,
        string tags = "-",
        string bounds = Bounds,
        MetricTier tier = MetricTier.Fine,
        bool compacted = false)
        => new()
        {
            Instrument = instrument,
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

    internal static async Task SeedAsync(IDatabaseFixture fixture, params MetricPoint[] points)
    {
        var ctx = fixture.CreateContext();
        ctx.Set<MetricPoint>().AddRange(points);
        await ctx.SaveChangesAsync(Ct);
    }

    internal static async Task<List<MetricPoint>> RowsAsync(IDatabaseFixture fixture, string instrument)
        => await fixture.CreateContext().Set<MetricPoint>()
            .AsNoTracking()
            .Where(x => x.Instrument == instrument)
            .OrderBy(x => x.Id)
            .ToListAsync(Ct);

    // The rollup always reads the fixed clock: Now unless a test pins another instant.
    internal static MetricRollup<TestContext> CreateRollup(
        IDatabaseFixture fixture,
        TimeSpan? dailyRetention,
        int cap = MetricRollup<TestContext>.MaxMetricRollupRowsPerTick,
        DateTime? now = null)
        => new(
            new TestServerContext(fixture.CreateContext()),
            Options.Create(Configuration(dailyRetention)),
            new FakeTimeProvider(new DateTimeOffset(now ?? Now)),
            NullLogger<MetricRollup<TestContext>>.Instance)
        {
            MaxRowsPerTick = cap,
        };

    private static WarpServerConfiguration Configuration(TimeSpan? dailyRetention) => new()
    {
        FineResolutionMinutes = 5,
        FineResolutionRetention = TimeSpan.FromHours(6),
        HourlyStatisticsRetention = TimeSpan.FromDays(7),
        DailyStatisticsRetention = dailyRetention,
        MetricFlushInterval = TimeSpan.FromSeconds(30),
    };

    private Task SeedAsync(params MetricPoint[] points) => SeedAsync(_fixture, points);

    private Task<List<MetricPoint>> RowsAsync(string instrument) => RowsAsync(_fixture, instrument);

    private Task<string?> RunAsync(int cap = MetricRollup<TestContext>.MaxMetricRollupRowsPerTick)
        => RunAsync(TimeSpan.FromDays(90), cap);

    private async Task<string?> RunAsync(TimeSpan? dailyRetention, int cap = MetricRollup<TestContext>.MaxMetricRollupRowsPerTick)
        => await CreateRollup(_fixture, dailyRetention, cap).ExecuteAsync(Ct);
}
