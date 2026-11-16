using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Worker.Services;
using static Warp.Tests.Metrics.MetricRollupTestsBase;

namespace Warp.Tests.Metrics;

/// <summary>
/// The two heavy <see cref="MetricRollup{TContext}"/> cap tests (R18, §6.2.1): the 20,500-row backlog at the
/// production cap and the up-to-200-tick small-cap drain. Split out of <see cref="MetricRollupTestsBase"/> and
/// serialized in <c>HeavyIntegration</c> (§4.7.1): each one is real bulk database work, and running beside other
/// heavy classes is what pushed the cap test into its budget on SQL Server. Budgets are unchanged (§4.4). Stamps and
/// the rollup clock share the fixed <see cref="MetricRollupTestsBase.Now"/>.
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class MetricRollupHeavyTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected MetricRollupHeavyTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    /// <summary>
    /// A capped tick defers the remainder: consecutive small-cap ticks must end with exactly the per-series totals
    /// an uncapped tick produces, every value present once — none lost, none counted twice.
    /// </summary>
    [TimedFact(30_000)]
    public async Task ExecuteAsync_CappedTicks_ReachTheUncappedTotalsWithoutLossOrDoubleCount()
    {
        // Reference: the same backlog under a different instrument, settled by one uncapped tick first. Its final
        // rows sit within retention, so the capped ticks that follow leave them untouched.
        await SeedAsync(_fixture, [.. BacklogAcrossTiers("uncapped")]);
        (await RunAsync()).ShouldNotBeNull();

        var points = BacklogAcrossTiers("capped");
        await SeedAsync(_fixture, [.. points]);
        var expected = Totals(points.Select(x => (x.Tags, x.Bounds, x.Count, x.Sum, x.Buckets)));

        (await RunAsync(cap: 3)).ShouldNotBeNull();
        var ticks = 1;
        while (await RunAsync(cap: 3) is not null)
        {
            ticks++;
            ticks.ShouldBeLessThan(200, "a capped rollup must drain");
        }

        ticks.ShouldBeGreaterThan(1, "the backlog must need more than one capped tick");

        var cappedRows = await RowsAsync(_fixture, "capped");
        var uncappedRows = await RowsAsync(_fixture, "uncapped");

        Totals(cappedRows.Select(x => (x.Tags, x.Bounds, x.Count, x.Sum, x.Buckets))).ShouldBe(expected);
        Totals(uncappedRows.Select(x => (x.Tags, x.Bounds, x.Count, x.Sum, x.Buckets))).ShouldBe(expected);
        Layout(cappedRows).ShouldBe(Layout(uncappedRows));
    }

    /// <summary>
    /// The production cap with a backlog larger than it (mirrors <c>StatisticRollupTestsBase</c>'s
    /// <c>Rollup_BacklogLargerThanTheTickCap…</c>): the first tick consumes at most
    /// <see cref="MetricRollup{TContext}.MaxMetricRollupRowsPerTick"/> source rows and later ticks finish the rest,
    /// with every count and sum landing exactly once. 30s: seeding 20,500 rows through EF and compacting 20,000 of
    /// them in one tick is real database work on both providers, well past the 10s default for a unit-style test.
    /// </summary>
    [TimedFact(30_000)]
    public async Task ExecuteAsync_BacklogLargerThanTheTickCap_FirstTickIsCappedAndLaterTicksFinishExactly()
    {
        const int extra = 500;
        const int total = MetricRollup<TestContext>.MaxMetricRollupRowsPerTick + extra;
        const int seriesCount = 50;

        // Closed but inside fine retention, so the whole backlog is compaction work only: one source row each.
        var points = Enumerable.Range(0, total)
            .Select(x =>
                new MetricPoint
                {
                    Instrument = "cap",
                    Tags = $"s={x % seriesCount}",
                    Tier = MetricTier.Fine,
                    BucketStart = Closed,
                    Count = 1,
                    Sum = x,
                    Compacted = false,
                })
            .ToArray();
        await SeedAsync(_fixture, points);

        var first = await RunAsync();

        first.ShouldNotBeNull();
        first.ShouldStartWith($"Compacted {MetricRollup<TestContext>.MaxMetricRollupRowsPerTick},");

        var ticks = 1;
        while (await RunAsync() is not null)
        {
            ticks++;
            ticks.ShouldBeLessThan(10, "a capped rollup must drain");
        }

        var rows = await RowsAsync(_fixture, "cap");
        rows.Count.ShouldBe(seriesCount);
        rows.ShouldAllBe(x => x.Compacted && x.Tier == MetricTier.Fine);
        rows.Sum(x => x.Count).ShouldBe(total);
        rows.Sum(x => x.Sum).ShouldBe(points.Sum(x => x.Sum));
    }

    private async Task<string?> RunAsync(int cap = MetricRollup<TestContext>.MaxMetricRollupRowsPerTick)
        => await CreateRollup(_fixture, TimeSpan.FromDays(90), cap).ExecuteAsync(Ct);
}
