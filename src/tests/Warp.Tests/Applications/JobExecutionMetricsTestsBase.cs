using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Metrics;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Applications;

/// <summary>
/// DB coverage for per-job-TYPE + per-HANDLER execution metrics (§8.19 / §4-decision-5 multi-app
/// observability). Runs real jobs of several types through a <see cref="WarpTestServer"/> (executor
/// <c>ApplicationName</c> set), lets them finalize, folds the counters via <c>CounterAggregator</c>, and
/// reads back per-type / per-handler count + error-rate + duration from <see cref="IJobQueryService"/>. The
/// metrics come from the durable <see cref="Statistic"/> aggregates, NOT the <see cref="Job"/> rows, so they
/// persist after the jobs are cleaned up; the history keys are downsampled by StatisticRollup (§8.30). Heavy
/// (boots a server) → serialized (§4.7.1). Fresh context per phase (§4.8).
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class JobExecutionMetricsTestsBase : IntegrationTestBase
{
    private const string AppName = "worker-app";

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static readonly string TypeUnit = typeof(UnitRequest).AssemblyQualifiedName!;
    private static readonly string TypeThrow = typeof(ThrowExceptionRequest).AssemblyQualifiedName!;
    private static readonly string TypeMessage = typeof(SingleHandlerMessage).AssemblyQualifiedName!;
    private static readonly string TypeMulti = typeof(MultiRequest).AssemblyQualifiedName!;
    private static readonly string HandlerSingle = typeof(SingleMessageHandler).AssemblyQualifiedName!;
    private static readonly string HandlerMultiA = typeof(MultiHandlerA).AssemblyQualifiedName!;
    private static readonly string HandlerMultiB = typeof(MultiHandlerB).AssemblyQualifiedName!;

    protected JobExecutionMetricsTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task Metrics_ByTypeAndByHandler_ReflectExecutionsAndErrorRate()
    {
        await SeedAndWaitAsync();
        await AggregateAsync();

        var metrics = await Reader().GetJobExecutionMetrics();

        // By TYPE: two UnitRequest succeed (0 errors), one ThrowExceptionRequest fails (rate 1.0), two
        // routed child jobs carry the message type.
        ByType(metrics, TypeUnit).ExecutedCount.ShouldBe(2);
        ByType(metrics, TypeUnit).ErrorCount.ShouldBe(0);
        ByType(metrics, TypeUnit).ErrorRate.ShouldBe(0);

        ByType(metrics, TypeThrow).ExecutedCount.ShouldBe(1);
        ByType(metrics, TypeThrow).ErrorCount.ShouldBe(1);
        ByType(metrics, TypeThrow).ErrorRate.ShouldBe(1.0);

        ByType(metrics, TypeMessage).ExecutedCount.ShouldBe(2);

        // By HANDLER: only routed-message jobs carry a HandlerType — the direct UnitRequest / ThrowException
        // jobs have none and are absent from the handler view.
        ByHandler(metrics, HandlerSingle).ExecutedCount.ShouldBe(2);
        ByHandler(metrics, HandlerSingle).ErrorCount.ShouldBe(0);
        metrics.ByHandler.ShouldNotContain(x => string.Equals(x.Identifier, TypeUnit, StringComparison.Ordinal));

        // A sub-millisecond no-op handler can round its AVG to 0 ms, but the latency HISTOGRAM always lands an
        // executed job in the smallest bucket (>= 5 ms bound), so P95 is a reliable nonzero signal that a real
        // duration was recorded and folded (avg's exact value is covered deterministically in the NoDb test).
        ByType(metrics, TypeUnit).P95DurationMs.ShouldBeGreaterThan(0);
        ByType(metrics, TypeUnit).AvgDurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    [TimedFact]
    public async Task Metrics_SurviveJobRowCleanup()
    {
        await SeedAndWaitAsync();
        await AggregateAsync();

        // Snapshot the duration aggregates BEFORE deleting the source rows. P95 is bucket-backed so it is a
        // reliable nonzero value for a job that actually ran.
        var before = await Reader().GetJobExecutionMetrics();
        var unitBefore = ByType(before, TypeUnit);
        unitBefore.P95DurationMs.ShouldBeGreaterThan(0);

        // Delete every Job (and its logs) — the raw source rows are gone.
        var wipe = Fixture.CreateContext();
        await wipe.Set<JobLog>().ExecuteDeleteAsync(Ct);
        await wipe.Set<Job>().ExecuteDeleteAsync(Ct);

        (await Fixture.CreateContext().Set<Job>().CountAsync(Ct)).ShouldBe(0);

        // Metrics still resolve — they come from Statistic, not Job (the whole point).
        var after = await Reader().GetJobExecutionMetrics();

        ByType(after, TypeThrow).ExecutedCount.ShouldBe(1);
        ByType(after, TypeThrow).ErrorRate.ShouldBe(1.0);
        ByType(after, TypeUnit).ExecutedCount.ShouldBe(2);
        ByHandler(after, HandlerSingle).ExecutedCount.ShouldBe(2);

        // The duration aggregates (avg via dur-sum, p95 via the histogram) are UNCHANGED after the Job rows
        // are deleted — proving they ride the durable Statistic rows, not the wiped Job rows (the dur-token
        // survives). P95 is still the same nonzero bucket value.
        var unitAfter = ByType(after, TypeUnit);
        unitAfter.AvgDurationMs.ShouldBe(unitBefore.AvgDurationMs);
        unitAfter.P95DurationMs.ShouldBe(unitBefore.P95DurationMs);
        unitAfter.P95DurationMs.ShouldBeGreaterThan(0);
    }

    [TimedFact]
    public async Task Metrics_ByHandler_IsIndependentOfType()
    {
        // One message TYPE (MultiRequest) fans out to TWO handlers (MultiHandlerA + MultiHandlerB), so a
        // single type's executions split across two DISTINCT handler buckets — proving the ByHandler
        // dimension is folded independently of ByType (not a 1:1 type↔handler mirror).
        await using (var server = await WarpTestServer.StartAsync(Fixture, cfg =>
        {
            cfg.WorkerCount = 1;
            cfg.ApplicationName = AppName;
        }))
        {
            var publisher = server.CreatePublisher();
            await publisher.Publish(new MultiRequest());
            await publisher.SaveChangesAsync(Ct);

            await server.WaitForCompletion();
        }

        await AggregateAsync();

        var metrics = await Reader().GetJobExecutionMetrics();

        // The single type ran twice (once per handler) ...
        ByType(metrics, TypeMulti).ExecutedCount.ShouldBe(2);

        // ... but each handler bucket saw exactly ONE execution.
        ByHandler(metrics, HandlerMultiA).ExecutedCount.ShouldBe(1);
        ByHandler(metrics, HandlerMultiB).ExecutedCount.ShouldBe(1);

        // The handler bucket count (1) differs from the single type's count (2) — the handler dimension is
        // not a mirror of the type.
        ByHandler(metrics, HandlerMultiA).ExecutedCount.ShouldNotBe(ByType(metrics, TypeMulti).ExecutedCount);
    }

    [TimedFact]
    public async Task HistoryKeys_DownsampledByRollup_LifetimeTotalsAndMetricsPersist()
    {
        await SeedAndWaitAsync();
        await AggregateAsync();

        // Hourly (now fine, §8.30) history keys exist after the fold ...
        (await CountStatisticsWithHistMarkerAsync()).ShouldBeGreaterThan(0);
        var lifetimeBefore = await CountLifetimeJobStatStatisticsAsync();
        lifetimeBefore.ShouldBeGreaterThan(0);

        // ... run StatisticRollup 8 days ahead. As of §8.30 the history detail is DOWNSAMPLED (fine→hourly→daily),
        // not deleted — so the history rows are retained at a coarser tier, and the lifetime totals (no date
        // suffix) are untouched. (ExpirationCleanup no longer prunes time-bucketed stats.)
        var future = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(8));
        await new StatisticRollup<TestContext>(
            new TestServerContext(Fixture.CreateContext()),
            Options.Create(new WarpServerConfiguration()),
            future).ExecuteAsync(Ct);

        // History rows persist (rolled to a coarser tier, not pruned); lifetime totals persist unchanged.
        (await CountStatisticsWithHistMarkerAsync()).ShouldBeGreaterThan(0);
        (await CountLifetimeJobStatStatisticsAsync()).ShouldBe(lifetimeBefore);

        // And the metrics still read (from the surviving lifetime totals).
        var metrics = await Reader().GetJobExecutionMetrics();
        ByType(metrics, TypeThrow).ErrorCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task Metrics_AreSliceableByExecutorApplication()
    {
        await SeedAndWaitAsync();
        await AggregateAsync();

        // Scoped to the executor app that ran the work: same per-type view.
        var scoped = await Reader().GetJobExecutionMetrics(AppName);
        ByType(scoped, TypeThrow).ExecutedCount.ShouldBe(1);
        ByType(scoped, TypeThrow).ErrorRate.ShouldBe(1.0);
        ByType(scoped, TypeUnit).ExecutedCount.ShouldBe(2);
        ByHandler(scoped, HandlerSingle).ExecutedCount.ShouldBe(2);

        // A different application has no slice.
        var other = await Reader().GetJobExecutionMetrics("some-other-app");
        other.ByType.ShouldBeEmpty();
        other.ByHandler.ShouldBeEmpty();
    }

    private static JobExecutionStatModel ByType(JobExecutionMetricsModel metrics, string identifier)
        => metrics.ByType.Where(x => string.Equals(x.Identifier, identifier, StringComparison.Ordinal)).ShouldHaveSingleItem();

    private static JobExecutionStatModel ByHandler(JobExecutionMetricsModel metrics, string identifier)
        => metrics.ByHandler.Where(x => string.Equals(x.Identifier, identifier, StringComparison.Ordinal)).ShouldHaveSingleItem();

    private JobQueryService<TestContext> Reader() => new(Fixture.CreateContext(), TimeProvider.System, new LocalMetricSource<TestContext>(Fixture.CreateContext()));

    private async Task AggregateAsync()
        => await TestTasks.CreateCounterAggregator(Fixture.CreateContext()).AggregateCountersAsync(Ct);

    // Boots a WorkerCount=1 server (good neighbour §4.7.1) with the executor ApplicationName set, publishes a
    // small mix of two direct job types (one succeeding, one failing) plus a routed message (handler jobs
    // carry HandlerType), and waits for everything to finalize.
    private async Task SeedAndWaitAsync()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture, cfg =>
        {
            cfg.WorkerCount = 1;
            cfg.ApplicationName = AppName;
        });

        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new UnitRequest());
        await publisher.Enqueue(new UnitRequest());
        await publisher.Enqueue(new ThrowExceptionRequest());
        await publisher.Publish(new SingleHandlerMessage());
        await publisher.Publish(new SingleHandlerMessage());
        await publisher.SaveChangesAsync(Ct);

        await server.WaitForCompletion();
    }

    private async Task<int> CountStatisticsWithHistMarkerAsync()
    {
        return await Fixture.CreateContext().Set<Statistic>()
            .Where(x => x.Key.StartsWith(JobStatsKeys.Prefix))
            .Where(x => x.Key.Contains($":{JobStatsKeys.HistoryMarker}:"))
            .CountAsync(Ct);
    }

    private async Task<int> CountLifetimeJobStatStatisticsAsync()
    {
        // Lifetime totals = jobstat: keys that are neither hourly history nor a pct bucket.
        return await Fixture.CreateContext().Set<Statistic>()
            .Where(x => x.Key.StartsWith(JobStatsKeys.Prefix + ":"))
            .Where(x => !x.Key.Contains($":{JobStatsKeys.HistoryMarker}:"))
            .Where(x => !x.Key.Contains($":{JobStatsKeys.PctMarker}:"))
            .CountAsync(Ct);
    }
}
