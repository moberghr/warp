using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Worker;

// Durability guarantee under shutdown: when a dispatcher-mode server is disposed mid-flight,
// a replacement server must be able to pick up every remaining job and drive it to a terminal
// state. This is the pod-rolling-restart scenario, which is the most common way shutdown
// interacts with in-flight work. Covers all three recovery paths end-to-end:
//   * UnclaimUndelivered — claimed-but-not-delivered rows revert to Enqueued
//   * channel drain — buffered rows complete as the worker empties its channel
//   * StaleJobRecovery — any Processing orphans the first two paths miss are reclaimed
//     after InvisibilityTimeout
[GenerateDatabaseTests]
public abstract class DispatcherShutdownIntegrationTestsBase : IntegrationTestBase
{
    protected DispatcherShutdownIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenWorkInProgress_WhenServerReplaced_ThenAllJobsEventuallyComplete()
    {
        // Server A takes work, gets disposed mid-flight. Server B starts on the same queue.
        // Every enqueued job must reach Completed — whether server A finished it before shutdown,
        // UnclaimUndelivered reverted it for server B to claim, or StaleJobRecovery reclaimed a
        // Processing orphan after InvisibilityTimeout.
        const string queue = "dispatcher-shutdown-baton";
        var invisibilityTimeout = TimeSpan.FromSeconds(2);

        var serverA = await WarpTestServer.StartAsync(Fixture, cfg =>
        {
            cfg.UseDispatcher = true;
            cfg.WorkerCount = 2;
            cfg.Queues = [queue];
            cfg.PollingInterval = TimeSpan.FromMilliseconds(100);
            cfg.InvisibilityTimeout = invisibilityTimeout;
            cfg.StaleJobRecoveryInterval = TimeSpan.FromMilliseconds(500);
        });

        var publisher = serverA.CreatePublisher();
        var jobIds = new List<Guid>();
        for (var i = 0; i < 15; i++)
        {
            jobIds.Add(await publisher.Enqueue(new ShortDelayRequest(), queue: queue));
        }

        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await serverA.WaitForJobState(jobIds[0], State.Processing);

        // Dispose A mid-stream. Depending on timing, some jobs are Processing, some Enqueued,
        // some may already be Completed. The invariant the test proves is that whatever state
        // A leaves the DB in, B drives every job to Completed.
        await serverA.DisposeAsync();

        await using var serverB = await WarpTestServer.StartAsync(Fixture, cfg =>
        {
            cfg.UseDispatcher = true;
            cfg.WorkerCount = 2;
            cfg.Queues = [queue];
            cfg.PollingInterval = TimeSpan.FromMilliseconds(100);
            cfg.InvisibilityTimeout = invisibilityTimeout;
            cfg.StaleJobRecoveryInterval = TimeSpan.FromMilliseconds(500);
        });

        // Timeout sized for the worst case: any orphan must wait one InvisibilityTimeout (~2s) +
        // one recovery sweep (~0.5s) + handler time. 20s leaves comfortable CI headroom.
        await serverB.WaitForCompletion(timeout: TimeSpan.FromSeconds(20));

        var ctx = Fixture.CreateContext();
        var jobs = await ctx.Set<Job>()
            .Where(j => j.Queue == queue)
            .AsNoTracking()
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        jobs.Count.ShouldBe(15);
        jobs.ShouldAllBe(
            j => j.CurrentState == State.Completed,
            "After the baton pass, every job must be Completed — no job left unprocessed.");
    }
}
