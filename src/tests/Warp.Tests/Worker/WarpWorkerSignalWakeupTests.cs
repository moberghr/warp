using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Warp.Core.Events;
using Warp.Worker;

namespace Warp.Tests.Worker;

[Trait("Category", "NoDb")]
public class WarpWorkerSignalWakeupTests
{
    [TimedFact]
    public async Task ExecuteAsync_SignalJobEnqueued_BypassesBackoffWait()
    {
        // Pins the in-process wake-up contract: when a Publisher/MessageRouter/handler in the
        // same process commits an Enqueued row and SignalJobEnqueued fires, a bare worker
        // currently asleep on its polling backoff must wake on the next signal pulse rather
        // than waiting for the wall-clock interval to elapse. Without this, bursty workloads
        // wait up to MaxPollingInterval (often 30s+ with push enabled) between the job becoming
        // available and the worker noticing.
        //
        // Test shape: configure a long polling interval (10s) so a passing test cannot be
        // explained by the wall-clock catching up. Fire the signal mid-wait, assert the next
        // fetch attempt lands within a small fraction of that interval.
        var fetchCalls = new List<DateTime>();
        var firstWaitStarted = new TaskCompletionSource();
        using var secondFetchSeen = new SemaphoreSlim(0, 1);
        var signals = new ServerTaskSignals<TestContext>();

        var mockService = new Mock<IWarpWorkerService>();
        mockService.Setup(x => x.GetAndProcessJob(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken _) =>
            {
                lock (fetchCalls)
                {
                    fetchCalls.Add(DateTime.UtcNow);
                }

                if (fetchCalls.Count == 1)
                {
                    // Signal that the worker has entered the post-fetch wait phase.
                    firstWaitStarted.TrySetResult();
                }
                else
                {
                    secondFetchSeen.Release();
                }

                return Task.FromResult(false);
            });

        var groupConfig = new WorkerGroupConfiguration
        {
            // Long enough that wall-clock cannot explain a sub-second wake-up.
            PollingInterval = TimeSpan.FromSeconds(10),
            MaxPollingInterval = TimeSpan.FromSeconds(30),
            PollingIntervalFactor = 2.0,
        };

        var worker = new WarpWorker<TestContext>(
            mockService.Object,
            NullLogger<WarpWorker<TestContext>>.Instance,
            groupConfig,
            new PauseStateHolder(),
            TimeProvider.System,
            Guid.NewGuid(),
            signals);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Make sure the worker has actually entered the wait — otherwise SignalJobEnqueued
            // could fire before the subscription is wired and we'd be testing a different path.
            await firstWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken);

            var signalAt = DateTime.UtcNow;
            signals.SignalJobEnqueued();

            var observed = await secondFetchSeen.WaitAsync(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken);
            observed.ShouldBeTrue("worker should wake within 2s of SignalJobEnqueued; configured wait is 10s");

            var elapsed = DateTime.UtcNow - signalAt;
            elapsed.ShouldBeLessThan(
                TimeSpan.FromSeconds(2),
                "wake-up latency must be sub-second; observing a full polling interval would mean the signal subscription is not wired");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [TimedFact]
    public async Task ServerTaskSignals_JobEnqueuedSubscription_FiresOnSignalJobEnqueued()
    {
        // Belt-and-braces unit-level coverage for the new channel: independent of WarpWorker,
        // a JobEnqueued subscriber must be invoked when SignalJobEnqueued fires, and must NOT
        // be invoked by SignalMessageEnqueued or SignalJobFinalized (channels are disjoint).
        var signals = new ServerTaskSignals<TestContext>();
        var jobEnqueuedCount = 0;
        using var subscription = signals.Subscribe(ServerTaskSignal.JobEnqueued, () => Interlocked.Increment(ref jobEnqueuedCount));

        signals.SignalMessageEnqueued();
        signals.SignalJobFinalized();
        jobEnqueuedCount.ShouldBe(0, "JobEnqueued subscriber must not fire on other channels");

        signals.SignalJobEnqueued();
        signals.SignalJobEnqueued();
        jobEnqueuedCount.ShouldBe(2, "each SignalJobEnqueued must invoke every subscriber once");

        subscription.Dispose();
        signals.SignalJobEnqueued();
        jobEnqueuedCount.ShouldBe(2, "disposed subscription must stop receiving signals");
    }
}
