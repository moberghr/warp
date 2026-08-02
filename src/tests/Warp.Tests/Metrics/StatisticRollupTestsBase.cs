using System.Globalization;
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
/// (§8.30). Stamps are relative to the real clock the rollup reads: <see cref="Rollable"/> is past the 7-day
/// hourly window but within the 90-day daily window (so an hourly bucket rolls to daily), and <see cref="Ancient"/>
/// is past the daily window (so its day is pruned). Proves fine→hourly and hourly→daily summing, legacy migration,
/// the deferral that keeps a fine+hourly collision lossless, direct-prune when the daily parent is itself past
/// retention (no update-then-delete crash), keep-forever when daily retention is null, that pcth and per-app keys
/// roll, and that lifetime/gauge keys are never touched. Each test drives one <c>ExecuteAsync</c> (§4.8).
/// </summary>
[GenerateDatabaseTests]
public abstract class StatisticRollupTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected StatisticRollupTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    // Past the 7-day hourly retention, within the 90-day daily retention → an hourly bucket rolls to daily.
    private static DateTime Rollable => DateTime.UtcNow.AddDays(-8);

    // Past the 90-day daily retention → its day is pruned, never rolled into.
    private static DateTime Ancient => DateTime.UtcNow.AddDays(-100);

    private static string Fine(DateTime t) => MetricTiers.Suffix(MetricTier.Fine, t, 5);

    private static string Hourly(DateTime t) => MetricTiers.Suffix(MetricTier.Hourly, t, 5);

    private static string Daily(DateTime t) => MetricTiers.Suffix(MetricTier.Daily, t, 5);

    private static string LegacyHour(DateTime t) => ":" + MetricTiers.Stamp(MetricTier.Hourly, t, 5); // unmarked

    [TimedFact]
    public async Task Rollup_FineBucketsInSameHour_SumIntoHourly()
    {
        var h = new DateTime(Rollable.Year, Rollable.Month, Rollable.Day, Rollable.Hour, 0, 0, DateTimeKind.Utc);
        await SeedAsync($"jobstat:type:X:hist:succeeded{Fine(h)}", 3);
        await SeedAsync($"jobstat:type:X:hist:succeeded{Fine(h.AddMinutes(5))}", 2);

        await RunAsync();

        (await ValueAsync($"jobstat:type:X:hist:succeeded{Fine(h)}")).ShouldBeNull();
        (await ValueAsync($"jobstat:type:X:hist:succeeded{Fine(h.AddMinutes(5))}")).ShouldBeNull();
        (await ValueAsync($"jobstat:type:X:hist:succeeded{Hourly(h)}")).ShouldBe(5);
    }

    [TimedFact]
    public async Task Rollup_HourlyBucket_SumsIntoDaily()
    {
        await SeedAsync($"jobstat:type:X:hist:succeeded{Hourly(Rollable)}", 10);

        await RunAsync();

        (await ValueAsync($"jobstat:type:X:hist:succeeded{Hourly(Rollable)}")).ShouldBeNull();
        (await ValueAsync($"jobstat:type:X:hist:succeeded{Daily(Rollable)}")).ShouldBe(10);
    }

    [TimedFact]
    public async Task Rollup_LegacyUnmarkedHourly_MigratesToDaily()
    {
        await SeedAsync($"stats:succeeded{LegacyHour(Rollable)}", 7);

        await RunAsync();

        (await ValueAsync($"stats:succeeded{LegacyHour(Rollable)}")).ShouldBeNull();
        (await ValueAsync($"stats:succeeded{Daily(Rollable)}")).ShouldBe(7);
    }

    [TimedFact]
    public async Task Rollup_InProgressFineBucket_LeftAlone()
    {
        var key = $"jobstat:type:R:hist:succeeded{Fine(DateTime.UtcNow)}";
        await SeedAsync(key, 4);

        await RunAsync();

        (await ValueAsync(key)).ShouldBe(4);
    }

    [TimedFact]
    public async Task Rollup_RecentDaily_LeftUnchangedAndIdempotent()
    {
        var key = $"jobstat:type:D:hist:succeeded{Daily(DateTime.UtcNow.AddDays(-2))}";
        await SeedAsync(key, 9);

        await RunAsync();
        await RunAsync();

        (await ValueAsync(key)).ShouldBe(9); // within daily retention: terminal, not rolled, not double-counted
    }

    [TimedFact]
    public async Task Rollup_DailyPastRetention_Deleted()
    {
        await SeedAsync($"jobstat:type:X:hist:succeeded{Daily(Ancient)}", 5);

        await RunAsync();

        (await ValueAsync($"jobstat:type:X:hist:succeeded{Daily(Ancient)}")).ShouldBeNull();
    }

    [TimedFact]
    public async Task Rollup_HourlyWhoseDayIsPastDailyRetention_PrunedDirectlyNotRolled()
    {
        // The daily parent is itself past the 90-day retention. The hourly must be pruned directly, NOT rolled
        // into a daily row that is being deleted the same pass (which would collide → DbUpdateConcurrencyException).
        await SeedAsync($"jobstat:type:X:hist:succeeded{Hourly(Ancient)}", 8);

        await RunAsync();

        (await ValueAsync($"jobstat:type:X:hist:succeeded{Hourly(Ancient)}")).ShouldBeNull();
        (await ValueAsync($"jobstat:type:X:hist:succeeded{Daily(Ancient)}")).ShouldBeNull(); // never created
    }

    [TimedFact]
    public async Task Rollup_FineAndItsHourlyParentBothStale_NoValueLostAcrossTicks()
    {
        await SeedAsync($"jobstat:type:X:hist:succeeded{Hourly(Rollable)}", 4);
        await SeedAsync($"jobstat:type:X:hist:succeeded{Fine(Rollable)}", 6);

        await RunAsync(); // tick 1: fine → hourly; the hourly's own roll deferred so nothing is lost

        (await ValueAsync($"jobstat:type:X:hist:succeeded{Fine(Rollable)}")).ShouldBeNull();
        (await ValueAsync($"jobstat:type:X:hist:succeeded{Hourly(Rollable)}")).ShouldBe(10);

        await RunAsync(); // tick 2: the now-complete hourly rolls to daily

        (await ValueAsync($"jobstat:type:X:hist:succeeded{Hourly(Rollable)}")).ShouldBeNull();
        (await ValueAsync($"jobstat:type:X:hist:succeeded{Daily(Rollable)}")).ShouldBe(10); // full value preserved
    }

    [TimedFact]
    public async Task Rollup_PcthAndPerAppKeys_RollLikeHist()
    {
        // The rollup is family-agnostic — a pcth latency bucket and a per-app hist key must roll too.
        await SeedAsync($"jobstat:type:X:pcth:50{Fine(Rollable)}", 3);
        await SeedAsync($"jobstat-app:orders:type:X:hist:succeeded{Hourly(Rollable)}", 12);

        await RunAsync();

        (await ValueAsync($"jobstat:type:X:pcth:50{Fine(Rollable)}")).ShouldBeNull();
        (await ValueAsync($"jobstat:type:X:pcth:50{Hourly(Rollable)}")).ShouldBe(3);       // fine pcth → hourly pcth
        (await ValueAsync($"jobstat-app:orders:type:X:hist:succeeded{Daily(Rollable)}")).ShouldBe(12); // app hourly → daily
    }

    [TimedFact]
    public async Task Rollup_DailyKeptForever_WhenRetentionNull()
    {
        await SeedAsync($"jobstat:type:X:hist:succeeded{Daily(Ancient)}", 5);

        await RunKeepDailyForeverAsync();

        (await ValueAsync($"jobstat:type:X:hist:succeeded{Daily(Ancient)}")).ShouldBe(5);
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

    private async Task SeedAsync(string key, long value)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<Statistic>().Add(new Statistic { Key = key, Value = value });
        await ctx.SaveChangesAsync(Ct);
    }

    private Task RunAsync() => RunWithAsync(TimeSpan.FromDays(90));

    private Task RunKeepDailyForeverAsync() => RunWithAsync(null);

    private async Task RunWithAsync(TimeSpan? dailyRetention)
        => await new StatisticRollup<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            Options.Create(new WarpServerConfiguration
            {
                FineResolutionRetention = TimeSpan.FromHours(6),
                HourlyStatisticsRetention = TimeSpan.FromDays(7),
                DailyStatisticsRetention = dailyRetention,
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
