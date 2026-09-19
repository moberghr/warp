using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Observability;

/// <summary>
/// The aggregators must drain a backlog across several ticks rather than in one.
/// <para>
/// They used to drain in <c>while (true)</c> inside the task host's lock transaction, so a backlog
/// held one transaction open until it cleared — measured at 51 minutes against a 500k-job backlog.
/// An open transaction pins the vacuum horizon, so autovacuum cannot reclaim any dead tuple newer
/// than it: the job table grew, every claim scanned more corpses, and throughput collapsed while the
/// queue was still draining. The aggregator running longer made the bloat worse, which made the
/// aggregator slower.
/// </para>
/// <para>
/// Both halves of the fix are load-bearing and neither is visible in any other test, so removing the
/// cap or setting <c>RerunImmediately</c> back to false would otherwise leave the whole suite green
/// while the collapse came back.
/// </para>
/// </summary>
[GenerateDatabaseTests]
public abstract class AggregatorDrainBoundTestsBase : IAsyncLifetime
{
    // One tick takes at most MaxBatchesPerTick (20) batches of AggregationBatchSize (1000). Seeding
    // just past that is what makes "did it stop?" observable: at or under the cap, a bounded drain
    // and an unbounded one are indistinguishable.
    private const int OneTickCapacity = 20 * 1000;
    private const int Seeded = OneTickCapacity + 500;

    private readonly IDatabaseFixture _fixture;

    protected AggregatorDrainBoundTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact(120_000)]
    public async Task AggregateCounters_WithBacklogPastOneTick_StopsAtTheCapAndLeavesTheRest()
    {
        await SeedCountersAsync(Seeded);

        var drained = await TestTasks.CreateCounterAggregator(_fixture.CreateContext())
            .AggregateCountersAsync(Ct);

        drained.ShouldBe(OneTickCapacity, "one tick must stop at the cap, not drain the whole backlog");

        var remaining = await _fixture.CreateContext().Set<Counter>().CountAsync(Ct);
        remaining.ShouldBe(Seeded - OneTickCapacity);
    }

    /// <summary>
    /// The leftover is only harmless because the host re-ticks immediately. Without it the remainder
    /// waits a full CounterAggregationInterval, so bounding the drain would have traded a long
    /// transaction for an ever-growing Counter table.
    /// </summary>
    [TimedFact(120_000)]
    public async Task AggregateCounters_AfterTheCap_DrainsTheRemainderOnTheNextTick()
    {
        await SeedCountersAsync(Seeded);
        var context = _fixture.CreateContext();

        await TestTasks.CreateCounterAggregator(context).AggregateCountersAsync(Ct);
        var second = await TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(Ct);

        second.ShouldBe(Seeded - OneTickCapacity);
        (await _fixture.CreateContext().Set<Counter>().CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public void CounterAggregator_AsksToBeRerunImmediately()
    {
        TestTasks.CreateCounterAggregator(_fixture.CreateContext())
            .RerunImmediately
            .ShouldBeTrue("a capped drain leaves a remainder that must not wait out the interval");
    }

    /// <summary>
    /// Every counter lands under one key so the fold stays cheap: the drain bound is what is under
    /// test, not the grouping.
    /// </summary>
    private async Task SeedCountersAsync(int count)
    {
        var context = _fixture.CreateContext();
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        for (var i = 0; i < count; i++)
        {
            context.Set<Counter>().Add(new Counter { Key = "stats:succeeded", Value = 1 });
        }

        await context.SaveChangesAsync(Ct);
    }
}
