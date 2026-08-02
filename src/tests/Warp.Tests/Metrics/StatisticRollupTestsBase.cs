using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Metrics;

/// <summary>
/// Database coverage for <see cref="StatisticRollup{TContext}"/> — the metrics-retention downsampling task
/// (§8.30). Proves fine→hourly and hourly→daily buckets sum into their coarser parent then delete, legacy
/// unmarked hourly keys migrate to daily, in-progress and recent buckets are left alone, daily past retention is
/// deleted, the roll is idempotent (no double count on re-run), and lifetime/gauge keys are never touched.
/// Buckets stamped in 2020 are past every retention window relative to the real clock; recent buckets use
/// <c>now</c>. Each test drives one <c>ExecuteAsync</c> (§4.8).
/// </summary>
[GenerateDatabaseTests]
public abstract class StatisticRollupTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected StatisticRollupTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Rollup_FineBucketsInSameHour_SumIntoHourly()
    {
        await SeedAsync("jobstat:type:X:hist:succeeded:m5:2020-01-01-08-00", 3);
        await SeedAsync("jobstat:type:X:hist:succeeded:m5:2020-01-01-08-05", 2);

        await RunAsync();

        (await ValueAsync("jobstat:type:X:hist:succeeded:m5:2020-01-01-08-00")).ShouldBeNull();
        (await ValueAsync("jobstat:type:X:hist:succeeded:m5:2020-01-01-08-05")).ShouldBeNull();
        (await ValueAsync("jobstat:type:X:hist:succeeded:h1:2020-01-01-08")).ShouldBe(5);
    }

    [TimedFact]
    public async Task Rollup_HourlyBucket_SumsIntoDaily()
    {
        await SeedAsync("jobstat:type:X:hist:succeeded:h1:2020-01-01-08", 10);

        await RunAsync();

        (await ValueAsync("jobstat:type:X:hist:succeeded:h1:2020-01-01-08")).ShouldBeNull();
        (await ValueAsync("jobstat:type:X:hist:succeeded:d1:2020-01-01")).ShouldBe(10);
    }

    [TimedFact]
    public async Task Rollup_LegacyUnmarkedHourly_MigratesToDaily()
    {
        await SeedAsync("stats:succeeded:2020-01-01-08", 7);

        await RunAsync();

        (await ValueAsync("stats:succeeded:2020-01-01-08")).ShouldBeNull();
        (await ValueAsync("stats:succeeded:d1:2020-01-01")).ShouldBe(7);
    }

    [TimedFact]
    public async Task Rollup_InProgressFineBucket_LeftAlone()
    {
        var key = $"jobstat:type:R:hist:succeeded{MetricTiers.Suffix(MetricTier.Fine, DateTime.UtcNow, 5)}";
        await SeedAsync(key, 4);

        await RunAsync();

        (await ValueAsync(key)).ShouldBe(4);
    }

    [TimedFact]
    public async Task Rollup_RecentDaily_LeftUnchangedAndIdempotent()
    {
        var key = $"jobstat:type:D:hist:succeeded{MetricTiers.Suffix(MetricTier.Daily, DateTime.UtcNow.AddDays(-2), 5)}";
        await SeedAsync(key, 9);

        await RunAsync();
        await RunAsync();

        // Within the 90-day daily retention: terminal tier, never rolled, never double-counted, never deleted.
        (await ValueAsync(key)).ShouldBe(9);
    }

    [TimedFact]
    public async Task Rollup_DailyPastRetention_Deleted()
    {
        await SeedAsync("jobstat:type:X:hist:succeeded:d1:2020-01-01", 5);

        await RunAsync();

        (await ValueAsync("jobstat:type:X:hist:succeeded:d1:2020-01-01")).ShouldBeNull();
    }

    [TimedFact]
    public async Task Rollup_LifetimeAndGaugeKeys_Untouched()
    {
        await SeedAsync("jobstat:type:X:succeeded", 100);
        await SeedAsync("jobstat:type:X:pct:100", 50);
        await SeedAsync("qbacklog:default:depth", 3);

        await RunAsync();

        (await ValueAsync("jobstat:type:X:succeeded")).ShouldBe(100);
        (await ValueAsync("jobstat:type:X:pct:100")).ShouldBe(50);
        (await ValueAsync("qbacklog:default:depth")).ShouldBe(3);
    }

    [TimedFact]
    public async Task Rollup_FineRoll_AccumulatesIntoExistingRecentHourly()
    {
        // 7 hours ago: past the 6h fine window (fine rolls) but well within the 7-day hourly window (the hourly
        // parent stays). A prior roll already produced the hourly parent; this fine bucket must ADD to it.
        var t = DateTime.UtcNow.AddHours(-7);
        var fineKey = $"jobstat:type:X:hist:succeeded{MetricTiers.Suffix(MetricTier.Fine, t, 5)}";
        var hourKey = $"jobstat:type:X:hist:succeeded{MetricTiers.Suffix(MetricTier.Hourly, t, 5)}";
        await SeedAsync(hourKey, 4);
        await SeedAsync(fineKey, 6);

        await RunAsync();

        (await ValueAsync(fineKey)).ShouldBeNull();     // fine rolled up
        (await ValueAsync(hourKey)).ShouldBe(10);       // 4 + 6, not itself rolled (within the hourly window)
    }

    private async Task SeedAsync(string key, long value)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<Statistic>().Add(new Statistic { Key = key, Value = value });
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task RunAsync()
        => await new StatisticRollup<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            Options.Create(new WarpServerConfiguration
            {
                FineResolutionRetention = TimeSpan.FromHours(6),
                HourlyStatisticsRetention = TimeSpan.FromDays(7),
                DailyStatisticsRetention = TimeSpan.FromDays(90),
                FineResolutionMinutes = 5,
            }),
            TimeProvider.System)
            .ExecuteAsync(Ct);

    private async Task<long?> ValueAsync(string key)
    {
        var ctx = _fixture.CreateContext();
        var row = await ctx.Set<Statistic>().AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, Ct);

        return row?.Value;
    }
}
