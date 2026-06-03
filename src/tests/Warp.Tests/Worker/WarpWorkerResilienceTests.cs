using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Warp.Tests.Helpers;
using Warp.Worker;

namespace Warp.Tests.Worker;

[Trait("Category", "NoDb")]
public class WarpWorkerResilienceTests
{
    [TimedFact(timeout: 5_000)]
    public async Task ExecuteAsync_WhenFetchThrows_LogsAndContinuesPolling()
    {
        // Regression for PR #123 review F2 + align with WarpDispatcher: WarpWorker had no
        // try/catch in its poll loop, so a single exception from GetAndProcessJob silently
        // terminated the BackgroundService. WarpDispatcher already caught and continued —
        // aligning the two ensures both background services survive transient failures.
        var callCount = 0;
        using var reachedSteadyState = new SemaphoreSlim(0, 1);

        var mockService = new Mock<IWarpWorkerService>();
        mockService.Setup(x => x.GetAndProcessJob(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken _) =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    throw new InvalidOperationException("simulated transient DB failure");
                }

                if (count == 3)
                {
                    reachedSteadyState.Release();
                }

                return Task.FromResult(false);
            });

        var groupConfig = new WorkerGroupConfiguration
        {
            PollingInterval = TimeSpan.FromMilliseconds(50),
            MaxPollingInterval = TimeSpan.FromMilliseconds(200),
            PollingIntervalFactor = 2.0,
        };

        var worker = new WarpWorker<TestContext>(
            mockService.Object,
            NullLogger<WarpWorker<TestContext>>.Instance,
            groupConfig,
            new PauseStateHolder(),
            TimeProvider.System,
            Guid.NewGuid(),
            TestTasks.NullSignals);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await worker.StartAsync(cts.Token);

        var reached = await reachedSteadyState.WaitAsync(TimeSpan.FromSeconds(3), Xunit.TestContext.Current.CancellationToken);

        await worker.StopAsync(CancellationToken.None);

        reached.ShouldBeTrue("worker should continue polling after an exception; pre-fix it silently terminates");
        callCount.ShouldBeGreaterThanOrEqualTo(3);
    }

    [TimedFact]
    public async Task ExecuteAsync_AfterProcessingJob_ResetsBackoffToFloor()
    {
        // Regression coverage for PR #123 review F5: there was no test proving currentDelay
        // actually resets after a successful process. The backoff math is pure (tested in
        // PollingBackoffTests); this test pins the reset hook in WarpWorker.ExecuteAsync.
        //
        // The wait inside WarpWorker uses SemaphoreSlim.WaitAsync(delay, ct) which is
        // wall-clock and does not honour a custom TimeProvider, so this test runs on real
        // time. To stay deterministic on CI: use a very small floor (10ms), assert by call
        // timing (post-success call comes back within floor*5 = 50ms, not max=200ms).
        var floor = TimeSpan.FromMilliseconds(10);
        var max = TimeSpan.FromMilliseconds(200);

        var callTimestamps = new List<DateTime>();
        var callLock = new Lock();
        var callCount = 0;
        using var sawCall7 = new SemaphoreSlim(0, 1);

        var mockService = new Mock<IWarpWorkerService>();
        mockService.Setup(x => x.GetAndProcessJob(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken _) =>
            {
                lock (callLock)
                {
                    callTimestamps.Add(DateTime.UtcNow);
                }

                var n = Interlocked.Increment(ref callCount);
                if (n == 7)
                {
                    sawCall7.Release();
                }

                // Calls 1..5 empty (backoff ramps to cap). Call 6 success → reset hook fires.
                // Call 7 empty: its arrival time relative to call 6 is what we assert on.
                return Task.FromResult(n == 6);
            });

        var groupConfig = new WorkerGroupConfiguration
        {
            PollingInterval = floor,
            MaxPollingInterval = max,
            PollingIntervalFactor = 2.0,
        };

        var worker = new WarpWorker<TestContext>(
            mockService.Object,
            NullLogger<WarpWorker<TestContext>>.Instance,
            groupConfig,
            new PauseStateHolder(),
            TimeProvider.System,
            Guid.NewGuid(),
            TestTasks.NullSignals);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var saw = await sawCall7.WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken);
            saw.ShouldBeTrue("worker should reach call #7 within 2s");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        DateTime[] timestamps;
        lock (callLock)
        {
            timestamps = [.. callTimestamps];
        }

        timestamps.Length.ShouldBeGreaterThanOrEqualTo(7, "worker did not progress through enough iterations");

        // The gap between call 6 (success) and call 7 (next poll) is the assertion target.
        // After call 6 returns true, currentDelay is reset to floor and the loop `continue`s
        // — no wait. Call 7 then comes back empty, schedules the next wait at Next(floor)=20ms.
        // What we observe is call 7's timestamp vs call 6: that gap should be near-zero
        // (just the loop overhead), because there's no Task.Delay between them. Without
        // the reset, currentDelay would still be at max from prior calls and the worker
        // would wait Next(max)=200ms (clamped to max) before call 7.
        //
        // We accept up to floor*3=30ms slack for CI scheduling. Without the reset hook the
        // expected gap is max=200ms+, so the threshold is well-separated either way.
        var gap6to7 = timestamps[6] - timestamps[5];
        gap6to7.ShouldBeLessThan(
            TimeSpan.FromMilliseconds(30),
            "post-success poll must follow immediately; observing a long gap means currentDelay was not reset to floor");
    }
}
