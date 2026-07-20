using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Adapters;

/// <summary>
/// Adapter <see cref="Counter"/>-row coverage (SC4, SC15). Persisted calls write write-optimised
/// counter rows per adapter / operation / group / outcome (§6.2); <c>CounterAggregator</c> collapses
/// them into <see cref="Statistic"/> rows. Per-group counters include successes so per-group error
/// rates have a real denominator.
/// </summary>
[GenerateDatabaseTests]
public abstract class AdapterCounterTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected AdapterCounterTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Persist_WritesAdapterAndOperationCounters()
    {
        await PersistAsync(Record("vendor", "GetOrders", AdapterCallOutcome.Success));

        (await CounterValueAsync(AdapterCounterKeys.Total("vendor", "success"))).ShouldBe(1);
        (await CounterValueAsync(AdapterCounterKeys.Operation("vendor", "GetOrders", "success"))).ShouldBe(1);
    }

    [TimedFact]
    public async Task Persist_WritesDurationSumCounters_ForLatencyAggregate()
    {
        // Item 2 write side: the flusher emits a duration-SUM counter (the "dur" token) per adapter +
        // operation (+ group when present) so average latency is aggregate-backed. Value is the rounded ms.
        await PersistAsync(Record("vendor", "GetOrders", AdapterCallOutcome.Success, group: "shop-1", durationMs: 42.4));

        (await CounterValueAsync(AdapterCounterKeys.Total("vendor", AdapterCounterKeys.DurationToken))).ShouldBe(42);
        (await CounterValueAsync(AdapterCounterKeys.Operation("vendor", "GetOrders", AdapterCounterKeys.DurationToken))).ShouldBe(42);
        (await CounterValueAsync(AdapterCounterKeys.Group("vendor", "shop-1", AdapterCounterKeys.DurationToken))).ShouldBe(42);
    }

    [TimedFact]
    public async Task Persist_WritesLatencyHistogramBucket()
    {
        // 42ms rounds into the 50ms bucket (the smallest bound >= 42); no other bucket is touched. The
        // histogram is Total-dimension only (not per-operation/per-group) to bound counter volume.
        await PersistAsync(Record("vendor", "GetOrders", AdapterCallOutcome.Success, group: "shop-1", durationMs: 42.4));

        (await CounterValueAsync(AdapterCounterKeys.Pct("vendor", 50))).ShouldBe(1);
        (await CounterValueAsync(AdapterCounterKeys.Pct("vendor", 25))).ShouldBe(0);
    }

    [TimedFact]
    public async Task Persist_PerGroupCounter_IncludesSuccessOutcome()
    {
        await PersistAsync(Record("vendor", "Deliver", AdapterCallOutcome.Success, group: "shop-1"));

        (await CounterValueAsync(AdapterCounterKeys.Group("vendor", "shop-1", "success"))).ShouldBe(1);
    }

    [TimedFact]
    public async Task Persist_GrouplessCall_WritesNoGroupCounter()
    {
        await PersistAsync(Record("vendor", "GetOrders", AdapterCallOutcome.Success));

        var groupKeys = await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key.StartsWith("adapter:vendor:grp:"))
            .CountAsync(Xunit.TestContext.Current.CancellationToken);

        groupKeys.ShouldBe(0);
    }

    [TimedFact]
    public async Task Aggregator_CollapsesAdapterCounters_IntoStatistics()
    {
        await PersistAsync(
            Record("vendor", "GetOrders", AdapterCallOutcome.Success),
            Record("vendor", "GetOrders", AdapterCallOutcome.Success),
            Record("vendor", "GetOrders", AdapterCallOutcome.Failed));

        await TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(Xunit.TestContext.Current.CancellationToken);

        (await StatisticValueAsync(AdapterCounterKeys.Operation("vendor", "GetOrders", "success"))).ShouldBe(2);
        (await StatisticValueAsync(AdapterCounterKeys.Operation("vendor", "GetOrders", "failed"))).ShouldBe(1);

        var remainingCounters = await _fixture.CreateContext().Set<Counter>()
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        remainingCounters.ShouldBe(0);
    }

    [TimedFact]
    public async Task Persist_ConfiguredGroupLabel_LandsOnDefinition()
    {
        // The registry carries the AddAdapter-time GroupLabel; the flusher upserts it onto the definition
        // so dashboard-only processes (which never touch the registry) can read it (F3).
        var registry = new AdapterRegistry();
        registry.Register("vendor", new WarpAdapterOptions { GroupLabel = "Endpoint" });

        await AdapterCallFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(),
            [Record("vendor", "GetOrders", AdapterCallOutcome.Success)],
            registry,
            new WarpConfiguration(),
            TimeProvider.System,
            Xunit.TestContext.Current.CancellationToken);

        var definition = await _fixture.CreateContext().Set<AdapterDefinition>()
            .Where(x => x.Name == "vendor")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        definition.ShouldNotBeNull();
        definition.GroupLabel.ShouldBe("Endpoint");
    }

    private static AdapterCallRecord Record(string adapter, string operation, AdapterCallOutcome outcome, string? group = null, double durationMs = 5)
        => new()
        {
            AdapterName = adapter,
            Operation = operation,
            GroupName = group,
            Timestamp = DateTime.UtcNow,
            DurationMs = durationMs,
            Attempts = 1,
            Outcome = outcome,
            MachineName = "test-host",
        };

    private async Task PersistAsync(params AdapterCallRecord[] records)
    {
        await AdapterCallFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(),
            records,
            new AdapterRegistry(),
            new WarpConfiguration(),
            TimeProvider.System,
            Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<long> CounterValueAsync(string key)
    {
        return await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key == key)
            .SumAsync(x => (long)x.Value, Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<long> StatisticValueAsync(string key)
    {
        return await _fixture.CreateContext().Set<Statistic>()
            .Where(x => x.Key == key)
            .Select(x => x.Value)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
    }
}
