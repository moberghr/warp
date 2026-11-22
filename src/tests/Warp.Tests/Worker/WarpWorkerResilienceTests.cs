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
        // time. To stay deterministic on CI: use a very small floor (10ms) and assert by call
        // timing (the first wait after the success is 20ms, not max=200ms).
        var floor = TimeSpan.FromMilliseconds(10);
        var max = TimeSpan.FromMilliseconds(200);

        var callTimestamps = new List<DateTime>();
        var callLock = new Lock();
        var callCount = 0;
        using var sawCall8 = new SemaphoreSlim(0, 1);

        var mockService = new Mock<IWarpWorkerService>();
        mockService.Setup(x => x.GetAndProcessJob(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken _) =>
            {
                lock (callLock)
                {
                    callTimestamps.Add(DateTime.UtcNow);
                }

                var n = Interlocked.Increment(ref callCount);
                if (n == 8)
                {
                    sawCall8.Release();
                }

                // Calls 1 to 5 are empty and ramp the backoff to the cap. Call 6 succeeds, which is
                // where the reset happens. Call 7 follows at once either way, because a success
                // never waits, and is empty. The wait before call 8 shows whether the delay was reset.
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
            var saw = await sawCall8.WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken);
            saw.ShouldBeTrue("worker should reach call #8 within 2s");
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

        timestamps.Length.ShouldBeGreaterThanOrEqualTo(8, "worker did not progress through enough iterations");

        // The gap between call 7 (the first empty poll after the success) and call 8 is the
        // assertion target. With the reset, call 7 schedules Next(floor) = 20ms. Without it the
        // delay is still at the cap from calls 1..5 and call 7 schedules Next(max) = 200ms.
        // The 6 -> 7 gap cannot tell the two apart: a successful poll never waits, reset or not.
        //
        // 100ms sits between the two outcomes with 80ms of CI scheduling slack on the passing
        // side and 100ms on the failing one.
        var gap7to8 = timestamps[7] - timestamps[6];
        gap7to8.ShouldBeLessThan(
            TimeSpan.FromMilliseconds(100),
            "the first empty poll after a success must wait from the floor; a gap near the cap means currentDelay was not reset");
    }
}
