using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Metrics;

/// <summary>
/// Read side of the queue SLIs (§8.26): <c>IJobQueryService.GetQueueMetrics</c> reads the durable
/// <c>qwait:</c> fold (avg + p95/p99) merged with the latest <c>qbacklog:</c> backlog gauge, app-agnostic and
/// per-application. Seeds only <see cref="Statistic"/> rows (no <see cref="Warp.Core.Entities.Job"/> rows) —
/// so it also proves the metrics survive Job-row cleanup.
/// </summary>
[GenerateDatabaseTests]
public abstract class QueueMetricsQueryTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected QueueMetricsQueryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task GetQueueMetrics_AppAgnostic_MergesWaitPercentilesAndBacklog()
    {
        await SeedStatsAsync(
            ("qwait:default:count", 4),
            ("qwait:default:dur", 2000),          // avg = 2000 / 4 = 500ms
            ("qwait:default:pct:500", 2),
            ("qwait:default:pct:1000", 2),         // p95/p99 land in the 1000 bucket
            ("qbacklog:default:depth", 3),
            ("qbacklog:default:oldest_age_seconds", 42));

        var result = await new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System).GetQueueMetrics();

        var q = result.Queues.ShouldHaveSingleItem();
        q.Queue.ShouldBe("default");
        q.ClaimedCount.ShouldBe(4);
        q.AvgWaitMs.ShouldBe(500);
        q.P95WaitMs.ShouldBe(1000);
        q.P99WaitMs.ShouldBe(1000);
        q.BacklogDepth.ShouldBe(3);
        q.OldestAgeSeconds.ShouldBe(42);
    }

    [TimedFact]
    public async Task GetQueueMetrics_PerApplication_ReadsWaitFromDisjointSlice_BacklogStaysGlobal()
    {
        await SeedStatsAsync(
            ("qwait:default:count", 100),                       // app-agnostic wait — must NOT bleed into the app slice
            ("qwait-app:orders:default:count", 2),
            ("qwait-app:orders:default:dur", 1000),             // avg = 500ms
            ("qbacklog:default:depth", 7));                     // backlog is queue-GLOBAL (no qbacklog-app family)

        var result = await new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System).GetQueueMetrics("orders");

        var q = result.Queues.ShouldHaveSingleItem();
        q.ClaimedCount.ShouldBe(2);         // the app's own wait, not the app-agnostic 100
        q.AvgWaitMs.ShouldBe(500);
        q.P95WaitMs.ShouldBe(0);            // per-app family carries no histogram
        q.BacklogDepth.ShouldBe(7);         // the queue's overall backlog, shown alongside the app's wait
    }

    private async Task SeedStatsAsync(params (string Key, long Value)[] rows)
    {
        var ctx = _fixture.CreateContext();
        foreach (var (key, value) in rows)
        {
            ctx.Set<Statistic>().Add(new Statistic { Key = key, Value = value });
        }

        await ctx.SaveChangesAsync(Ct);
    }
}
