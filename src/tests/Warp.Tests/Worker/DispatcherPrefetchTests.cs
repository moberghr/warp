using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Worker;

// Pins how many jobs dispatcher mode holds in the Processing state at once. The dispatcher claims
// into a bounded channel sized at WorkerCount + PrefetchCount, and workers pull jobs OUT of that
// channel to run them — so buffer occupancy alone cannot tell busy from free, and the claim is sized
// by the reserve/release capacity counter instead. A buffered job is already Processing but nothing
// has started it, and its LastKeepAlive is only stamped at claim time (renewal begins at ownership,
// then in RunJobMonitor). A job that waits in the channel longer than InvisibilityTimeout is
// therefore visible to StaleJobRecovery as stale while a healthy server still intends to run it.
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class DispatcherPrefetchTestsBase : IntegrationTestBase
{
    protected DispatcherPrefetchTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task DispatcherMode_WithZeroPrefetch_ClaimsOnlyWhatWorkersCanStart()
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

    // The back-compat contract: an UNSET PrefetchCount buffers WorkerCount — the depth dispatcher mode
    // has always claimed ahead. Every other test here sets the knob explicitly, so without this one the
    // null-coalescing default could regress to 0 (or anything else) and the suite would stay green.
    [TimedFact]
    public async Task DispatcherMode_WithPrefetchUnset_BuffersWorkerCountJobs()
    {
        var processing = await RunAndCountProcessingAsync(prefetchCount: null);

        // 2 executing + WorkerCount (2) buffered — the historical depth.
        processing.ShouldBe(4);
    }

    // Regression pin for the double-execution bug the ownership guard closed. A prefetched job sits in
    // the channel with the LastKeepAlive stamped at claim time and nothing refreshing it, so
    // StaleJobRecovery reclaims it while this server still intends to run it. Unguarded, the worker then
    // ran its stale copy AND the dispatcher re-claimed the now-Enqueued row and handed it over again —
    // the same job executed twice with no crash involved.
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
        await server.WaitForJobState(prefetchedId, State.Processing);
        await server.WaitForJobState(prefetchedId, State.Enqueued);

        barrier.CanFinish.Release(10);
        await server.WaitForCompletion();

        // Absence check: the bug is a SECOND execution landing shortly after the first completes, so
        // there is no positive signal to wait on — the settle window is the §4.5 carve-out for proving
        // something does NOT happen. Exits early the moment the duplicate appears.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && counter.Counter < 2)
        {
            await Task.Delay(50);
        }

        counter.Counter.ShouldBe(1);
    }

    // Pins that MarkWorkerOwnership RENEWS LastKeepAlive as well as checking it. Checking alone only
    // closes the window BEFORE the guard: a job that waited in the channel past InvisibilityTimeout is
    // still stale the instant the check passes, so recovery can requeue it — and hand it to a second
    // worker — while this worker walks into the handler. That is the same double execution the guard
    // exists to prevent, moved a few milliseconds later.
    //
    // Deterministic by construction, no wall-clock sleeps (§4.5): the claim ages by ADVANCING the fake
    // clock while the job waits in the channel, recovery is driven by hand (StartWithFakeTime disables
    // the auto sweep), and CancellationCheckInterval is longer than the test so RunJobMonitor's periodic
    // renewal never fires. The ONLY thing that can refresh the token before the sweep is the ownership
    // mark itself.
    [TimedFact]
    public async Task DispatcherMode_WhenOwnershipIsTaken_RenewsTheClaimAgainstRecovery()
    {
        var barrier = new BarrierSignal();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await using var server = await WarpTestServer.StartWithFakeTime(
            Fixture,
            time,
            configure: config =>
            {
                config.UseDispatcher = true;
                config.WorkerCount = 1;
                config.PrefetchCount = 1;
                config.InvisibilityTimeout = TimeSpan.FromMilliseconds(500);

                // Keep RunJobMonitor's keep-alive renewal out of the window under test.
                config.CancellationCheckInterval = TimeSpan.FromMinutes(5);
            },
            configureServices: services => services.AddSingleton(barrier));

        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new BarrierRequest());
        await publisher.SaveChangesAsync();
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        // Prefetched while the only worker is pinned: claimed, Processing, sitting in the channel with
        // the claim-time keep-alive and nothing renewing it.
        var prefetchedId = await publisher.Enqueue(new BarrierRequest());
        await publisher.SaveChangesAsync();
        await server.WaitForJobState(prefetchedId, State.Processing);

        // Age the buffered claim a full fake second past InvisibilityTimeout — one clock advance, no
        // sleeping. (The pinned first job goes equally stale; it is not what the sweep assertion reads.)
        time.Advance(TimeSpan.FromSeconds(1));

        // Release the first job so the worker picks the aged one up and takes ownership of it — the
        // ownership mark stamps the ADVANCED now, which is what makes it survive the sweep below.
        barrier.CanFinish.Release();
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        // The prefetched job is now inside its handler. Sweep: a worker that renewed its claim on
        // ownership is not a candidate; one that only checked it is still sitting there with the
        // claim-time token and gets pulled out from under itself.
        await server.RunServerTaskOnceAsync<Warp.Worker.Services.StaleJobRecovery<TestContext>>();

        var state = await Fixture.CreateContext().Set<Job>()
            .Where(x => x.Id == prefetchedId)
            .Select(x => x.CurrentState)
            .FirstOrDefaultAsync();

        // Release before asserting so a failure reports the state instead of hanging teardown (§4.9).
        barrier.CanFinish.Release(10);

        state.ShouldBe(State.Processing);
    }

    private async Task<int> RunAndCountProcessingAsync(int? prefetchCount)
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

        // Enough jobs that the dispatcher can always claim to whatever depth it believes it has —
        // workers + the largest prefetch under test + slack. This is a capacity bound being measured,
        // not a concurrency race being provoked, so the count is a supply of fuel, not a spray (§0.4):
        // the assertion is on the PEAK Processing count, which an over-claiming dispatcher exceeds with
        // two jobs or twenty.
        var publisher = server.CreatePublisher();
        for (var i = 0; i < 6; i++)
        {
            await publisher.Enqueue(new BarrierRequest());
        }

        await publisher.SaveChangesAsync();

        // Both workers are now pinned inside the handler and cannot take anything else.
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        // Sample the peak: give the dispatcher time to top the channel up to whatever it will claim,
        // then take the highest count seen. An absence-style window (§4.5 carve-out — over-claiming has
        // no positive signal to await), sampled on a cadence rather than a hot loop so it doesn't hammer
        // the shared container's pool (the SqlServer-under-load flake family).
        var processing = 0;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await Fixture.CreateContext().Set<Job>()
                .Where(x => x.CurrentState == State.Processing)
                .CountAsync();
            processing = Math.Max(processing, snapshot);

            await Task.Delay(50);
        }

        // Release before returning so a failed assertion reports the count instead of hanging
        // teardown on pinned handlers (§4.9).
        barrier.CanFinish.Release(6);

        return processing;
    }
}
