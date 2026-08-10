using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Worker;

// Pins how many jobs dispatcher mode holds in the Processing state at once. The dispatcher claims
// into a bounded channel sized at WorkerCount, and workers pull jobs OUT of that channel to run
// them — so Reader.Count drops to zero while they are busy and the dispatcher immediately claims
// another full WorkerCount. The buffered jobs are already Processing but nothing has started them,
// and their LastKeepAlive is only stamped at claim time (renewal begins in RunJobMonitor, i.e. at
// execution). A job that waits in the channel longer than InvisibilityTimeout is therefore visible
// to StaleJobRecovery as stale while a healthy server still intends to run it.
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class DispatcherPrefetchTestsBase : IntegrationTestBase
{
    protected DispatcherPrefetchTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task DispatcherMode_WithDefaultPrefetch_ClaimsOnlyWhatWorkersCanStart()
    {
        var processing = await RunAndCountProcessingAsync(prefetchCount: 0);

        // 2 workers pinned in the handler, nothing claimed speculatively.
        processing.ShouldBe(2);
    }

    [TimedFact]
    public async Task DispatcherMode_WithPrefetchCount_ClaimsBeyondIdleWorkers()
    {
        var processing = await RunAndCountProcessingAsync(prefetchCount: 2);

        // 2 executing + 2 deliberately prefetched, sitting in the channel unstarted.
        processing.ShouldBe(4);
    }

    // RED until MarkWorkerOwnership becomes conditional. A prefetched job sits in the channel with
    // the LastKeepAlive stamped at claim time and nothing refreshing it (renewal starts in
    // RunJobMonitor, i.e. at execution), so StaleJobRecovery reclaims it while this server still
    // intends to run it. The worker then runs its stale copy AND the dispatcher re-claims the now
    // Enqueued row and hands it over again — the same job executes twice with no crash involved.
    [TimedFact]
    public async Task DispatcherMode_WhenPrefetchedJobIsReclaimed_ExecutesItExactlyOnce()
    {
        var barrier = new BarrierSignal();
        var counter = new CounterService();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            config =>
            {
                config.UseDispatcher = true;
                config.WorkerCount = 1;
                config.PrefetchCount = 1;
                config.InvisibilityTimeout = TimeSpan.FromSeconds(1);
                config.StaleJobRecoveryInterval = TimeSpan.FromMilliseconds(250);
            },
            services =>
            {
                services.AddSingleton(barrier);
                services.AddSingleton(counter);
            });

        // Pin the only worker first, so the counter job is unambiguously the prefetched one.
        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new BarrierRequest());
        await publisher.SaveChangesAsync();
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        var prefetchedId = await publisher.Enqueue(new CounterRequest());
        await publisher.SaveChangesAsync();

        // It gets claimed into the channel (Processing), then goes stale and is recovered back to
        // Enqueued while the worker is still holding its copy.
        await WaitForStateAsync(prefetchedId, State.Processing);
        await WaitForStateAsync(prefetchedId, State.Enqueued);

        barrier.CanFinish.Release(10);
        await server.WaitForCompletion();

        // Settle: a duplicate run lands shortly after the first completes.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && counter.Counter < 2)
        {
            await Task.Delay(50);
        }

        counter.Counter.ShouldBe(1);
    }

    // RED until MarkWorkerOwnership renews LastKeepAlive as well as checking it. Checking alone only
    // closes the window BEFORE the guard: a job that waited in the channel past InvisibilityTimeout is
    // still stale the instant the check passes, so recovery can requeue it — and hand it to a second
    // worker — while this worker walks into the handler. That is the same double execution the guard
    // exists to prevent, moved a few milliseconds later.
    //
    // Deterministic by construction: auto-recovery is off and driven by hand, and CancellationCheckInterval
    // is longer than the test, so RunJobMonitor's periodic renewal never fires. The ONLY thing that can
    // refresh the token in this window is the ownership mark itself.
    [TimedFact]
    public async Task DispatcherMode_WhenOwnershipIsTaken_RenewsTheClaimAgainstRecovery()
    {
        var barrier = new BarrierSignal();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            config =>
            {
                config.UseDispatcher = true;
                config.WorkerCount = 1;
                config.PrefetchCount = 1;
                config.InvisibilityTimeout = TimeSpan.FromMilliseconds(500);

                // Drive recovery by hand so the sweep happens at a known point, not on a timer.
                config.StaleJobRecoveryInterval = null;

                // Keep RunJobMonitor's keep-alive renewal out of the window under test.
                config.CancellationCheckInterval = TimeSpan.FromMinutes(5);
            },
            services => services.AddSingleton(barrier));

        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new BarrierRequest());
        await publisher.SaveChangesAsync();
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        // Prefetched while the only worker is pinned: claimed, Processing, sitting in the channel with
        // the claim-time keep-alive and nothing renewing it.
        var prefetchedId = await publisher.Enqueue(new BarrierRequest());
        await publisher.SaveChangesAsync();
        await WaitForStateAsync(prefetchedId, State.Processing);

        // Let it age past InvisibilityTimeout while it waits its turn.
        await Task.Delay(TimeSpan.FromSeconds(1));

        // Release the first job so the worker picks the aged one up and takes ownership of it.
        barrier.CanFinish.Release();
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        // The prefetched job is now inside its handler. Sweep: a worker that renewed its claim on
        // ownership is not a candidate; one that only checked it is still sitting there with a stale
        // token and gets pulled out from under itself.
        await server.RunServerTaskOnceAsync<Warp.Worker.Services.StaleJobRecovery<TestContext>>();

        var state = await Fixture.CreateContext().Set<Job>()
            .Where(x => x.Id == prefetchedId)
            .Select(x => x.CurrentState)
            .FirstOrDefaultAsync();

        // Release before asserting so a failure reports the state instead of hanging teardown (§4.9).
        barrier.CanFinish.Release(10);

        state.ShouldBe(State.Processing);
    }

    private async Task WaitForStateAsync(Guid jobId, State expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var state = await Fixture.CreateContext().Set<Job>()
                .Where(x => x.Id == jobId)
                .Select(x => x.CurrentState)
                .FirstOrDefaultAsync();
            if (state == expected)
            {
                return;
            }
        }

        throw new TimeoutException($"Job {jobId} did not reach {expected}");
    }

    private async Task<int> RunAndCountProcessingAsync(int prefetchCount)
    {
        var barrier = new BarrierSignal();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            config =>
            {
                config.UseDispatcher = true;
                config.WorkerCount = 2;
                config.PrefetchCount = prefetchCount;
            },
            services => services.AddSingleton(barrier));

        var publisher = server.CreatePublisher();
        for (var i = 0; i < 6; i++)
        {
            await publisher.Enqueue(new BarrierRequest());
        }

        await publisher.SaveChangesAsync();

        // Both workers are now pinned inside the handler and cannot take anything else.
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        // With only 2 workers, at most 2 jobs can be executing. Anything beyond that which has left
        // Enqueued was claimed speculatively and is sitting in the channel, unstarted.
        // Sample the peak: give the dispatcher time to top the channel up to whatever it will
        // claim, then take the highest count seen.
        var processing = 0;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await Fixture.CreateContext().Set<Job>()
                .Where(x => x.CurrentState == State.Processing)
                .CountAsync();
            processing = Math.Max(processing, snapshot);
        }

        // Release before returning so a failed assertion reports the count instead of hanging
        // teardown on pinned handlers (§4.9).
        barrier.CanFinish.Release(6);

        return processing;
    }
}
