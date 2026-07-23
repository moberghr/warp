using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Applications;

/// <summary>
/// DB coverage for per-job-TYPE + per-HANDLER execution metrics (§8.19 / §4-decision-5 multi-app
/// observability). Runs real jobs of several types through a <see cref="WarpTestServer"/> (executor
/// <c>ApplicationName</c> set), lets them finalize, folds the counters via <c>CounterAggregator</c>, and
/// reads back per-type / per-handler count + error-rate + duration from <see cref="IJobQueryService"/>. The
/// metrics come from the durable <see cref="Statistic"/> aggregates, NOT the <see cref="Job"/> rows, so they
/// persist after the jobs are cleaned up; the hourly-history keys ride the generic hourly-stat prune. Heavy
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
    private static readonly string HandlerSingle = typeof(SingleMessageHandler).AssemblyQualifiedName!;

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

        // Duration + percentiles are real-time-dependent (a no-op handler may round to 0 ms), so assert only
        // they are populated non-negatively — the exact dur math is covered deterministically in the NoDb test.
        ByType(metrics, TypeUnit).AvgDurationMs.ShouldBeGreaterThanOrEqualTo(0);
        ByType(metrics, TypeUnit).P95DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    [TimedFact]
    public async Task Metrics_SurviveJobRowCleanup()
    {
        await SeedAndWaitAsync();
        await AggregateAsync();

        // Delete every Job (and its logs) — the raw source rows are gone.
        var wipe = Fixture.CreateContext();
        await wipe.Set<JobLog>().ExecuteDeleteAsync(Ct);
        await wipe.Set<Job>().ExecuteDeleteAsync(Ct);

        (await Fixture.CreateContext().Set<Job>().CountAsync(Ct)).ShouldBe(0);

        // Metrics still resolve — they come from Statistic, not Job (the whole point).
        var metrics = await Reader().GetJobExecutionMetrics();

        ByType(metrics, TypeThrow).ExecutedCount.ShouldBe(1);
        ByType(metrics, TypeThrow).ErrorRate.ShouldBe(1.0);
        ByType(metrics, TypeUnit).ExecutedCount.ShouldBe(2);
        ByHandler(metrics, HandlerSingle).ExecutedCount.ShouldBe(2);
    }

    [TimedFact]
    public async Task HourlyHistoryKeys_ArePrunedByExpirationCleanup_LifetimeTotalsPersist()
    {
        await SeedAndWaitAsync();
        await AggregateAsync();

        // Hourly history keys exist after the fold ...
        (await CountStatisticsWithHistMarkerAsync()).ShouldBeGreaterThan(0);
        var lifetimeBefore = await CountLifetimeJobStatStatisticsAsync();
        lifetimeBefore.ShouldBeGreaterThan(0);

        // ... run ExpirationCleanup at a time 8 days ahead: the generic hourly-stat sweep (retention 7 d)
        // deletes any key ending in a yyyy-MM-dd-HH bucket older than the cutoff.
        var future = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(8));
        await TestTasks.CreateExpirationCleanup(Fixture.CreateContext(), future).ExecuteAsync(Ct);

        // Hourly jobstat keys pruned; lifetime totals (no date suffix) persist.
        (await CountStatisticsWithHistMarkerAsync()).ShouldBe(0);
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

    private JobQueryService<TestContext> Reader() => new(Fixture.CreateContext(), TimeProvider.System);

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
