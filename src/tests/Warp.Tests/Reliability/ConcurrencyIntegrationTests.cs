using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Reliability;

[GenerateDatabaseTests]
public abstract class ConcurrencyIntegrationTestsBase : IntegrationTestBase
{
    protected ConcurrencyIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenTwoBarrierJobs_WithFiveWorkers_ThenEachClaimedByExactlyOneWorker()
    {
        // Deterministic concurrency check: 2 jobs, 5 workers competing for them via FOR UPDATE
        // SKIP LOCKED. The barrier handler signals on entry and blocks until released, so once
        // we've observed two Running signals we *know* exactly two distinct workers are stuck
        // inside the handler with State=Processing committed. Any duplicate-claim regression
        // would surface as either a third Running signal (caught by Should.Throw on a third
        // WaitAsync with a short timeout below) or as a duplicate Processing JobLog row.
        // Spraying 50 jobs and hoping a race shows is gambling; pinning two workers in handler
        // proves the claim semantics directly.
        var barrier = new BarrierSignal();

        await using var server = await WarpTestServer.StartAsync(Fixture, cfg =>
        {
            // Distinct CurrentWorkerId per running job is single-worker semantics; a dispatcher group shares one (§6.12).
            cfg.UseDispatcher = false;
            cfg.Services.AddSingleton(barrier);
        });
        var publisher = server.CreatePublisher();
        var job1Id = await publisher.Enqueue(new BarrierRequest());
        var job2Id = await publisher.Enqueue(new BarrierRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // The handlers are released whatever the assertions say. A failed one used to leave them pinned, so
        // disposing the server waited for them until the test's own timeout, and the run reported
        // "timed out after 10000 milliseconds" instead of the assertion that failed.
        try
        {
            // Both handlers must enter (proves both jobs were claimed by some worker)
            await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);
            await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);

            // No third worker should be able to enter the handler — only two jobs exist. Wait a
            // brief polling cycle to give a duplicate-claim regression time to surface.
            var spuriousEntry = await barrier.Running.WaitAsync(TimeSpan.FromMilliseconds(300), Xunit.TestContext.Current.CancellationToken);
            spuriousEntry.ShouldBeFalse("Only two workers should be in the handler; a third entry indicates duplicate claim");

            // Both jobs are now Processing on distinct workers. Per-job assertions:
            var ctx = Fixture.CreateContext();
            foreach (var jobId in new[] { job1Id, job2Id })
            {
                var job = await ctx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
                job.CurrentState.ShouldBe(State.Processing);
                job.CurrentWorkerId.ShouldNotBeNull();
            }

            // The two workers in handler must be distinct (not the same worker double-claiming)
            var workerIds = await ctx.Set<Job>()
                .Where(j => j.Id == job1Id || j.Id == job2Id)
                .Select(j => j.CurrentWorkerId)
                .ToListAsync(Xunit.TestContext.Current.CancellationToken);
            workerIds.Distinct().Count().ShouldBe(2, "Two jobs must be processed by two distinct workers");
        }
        finally
        {
            barrier.CanFinish.Release(2);
        }

        await server.WaitForCompletion();

        var readCtx = Fixture.CreateContext();
        foreach (var jobId in new[] { job1Id, job2Id })
        {
            var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
            job.CurrentState.ShouldBe(State.Completed);

            var completedLogs = await readCtx.Set<JobLog>()
                .CountAsync(l => l.JobId == jobId && l.EventType == "Completed", Xunit.TestContext.Current.CancellationToken);
            completedLogs.ShouldBe(1, $"Job {jobId} should have exactly one Completed log");

            // Exactly one, read once the job is at rest: the row is deferred (rule 6.12), written by the
            // monitor's first flush or with the completion, so mid-run it may not exist yet. Asserting it
            // 300 ms after the handlers entered failed on CI (SQL Server 2025) when the flush came later.
            await server.WaitForJobLogCount(jobId, "Processing", 1);
        }
    }

    [TimedFact]
    public async Task GivenSingleJob_WithFiveWorkers_ThenOnlyOneProcessesIt()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();

        var job = await ctx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Completed);

        // Exactly one Processing and one Completed log — only one worker touched it
        await server.WaitForJobLogCount(jobId, "Processing", 1);

        var completedLogs = await ctx.Set<JobLog>()
            .CountAsync(l => l.JobId == jobId && l.EventType == "Completed", Xunit.TestContext.Current.CancellationToken);
        completedLogs.ShouldBe(1);
    }

    [TimedFact]
    public async Task GivenTwoBarrierMessages_WithFiveWorkers_ThenEachRoutedExactlyOnceAndClaimedByDistinctWorker()
    {
        // Deterministic message-routing-and-claim test: 2 messages → MessageRouter creates 2
        // handler jobs → 5 workers race to claim them. The barrier handler signals on entry
        // and blocks; once two Running signals fire, two distinct workers are pinned in handler
        // with distinct handler jobs. A double-routed message would produce 2 handler jobs per
        // message (4 total) and a third Running signal; a duplicate-claim would produce a
        // worker conflict on the same handler job. Spraying 5 messages and hoping a race
        // surfaces was the previous pattern; this version forces concurrency with N=2.
        var barrier = new BarrierSignal();

        await using var server = await WarpTestServer.StartAsync(Fixture, cfg =>
        {
            // Distinct CurrentWorkerId per running job is single-worker semantics; a dispatcher group shares one (§6.12).
            cfg.UseDispatcher = false;
            cfg.Services.AddSingleton(barrier);
        });
        var publisher = server.CreatePublisher();
        var msg1 = await publisher.Publish(new BarrierMessage());
        var msg2 = await publisher.Publish(new BarrierMessage());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Both handler jobs must enter the barrier — proves both messages routed AND each
        // routed handler job was claimed by a distinct worker.
        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);
        await barrier.Running.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        // No third worker can enter — only 2 handler jobs exist. A double-route would put
        // 4 handler jobs in the system and a third worker would Running-signal here.
        var spurious = await barrier.Running.WaitAsync(TimeSpan.FromMilliseconds(300), Xunit.TestContext.Current.CancellationToken);
        spurious.ShouldBeFalse("A third Running signal indicates double-routing or duplicate claim");

        var ctx = Fixture.CreateContext();
        foreach (var messageId in new[] { msg1, msg2 })
        {
            var handlerJobs = await ctx.Set<Job>()
                .Where(j => j.ParentJobId == messageId && j.Kind == JobKind.Job)
                .ToListAsync(Xunit.TestContext.Current.CancellationToken);
            handlerJobs.Count.ShouldBe(1, $"Message {messageId} must have exactly 1 handler job");
            handlerJobs[0].CurrentState.ShouldBe(State.Processing);
            handlerJobs[0].CurrentWorkerId.ShouldNotBeNull();
        }

        // Two distinct workers hold the two handler jobs
        var workerIds = await ctx.Set<Job>()
            .Where(j => (j.ParentJobId == msg1 || j.ParentJobId == msg2) && j.Kind == JobKind.Job)
            .Select(j => j.CurrentWorkerId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        workerIds.Distinct().Count().ShouldBe(2);

        // Release and verify clean completion
        barrier.CanFinish.Release(2);
        await server.WaitForCompletion();

        var readCtx = Fixture.CreateContext();
        foreach (var messageId in new[] { msg1, msg2 })
        {
            var message = await readCtx.Set<Job>().FirstAsync(j => j.Id == messageId, Xunit.TestContext.Current.CancellationToken);
            message.CurrentState.ShouldBe(State.Completed);
        }
    }
}
