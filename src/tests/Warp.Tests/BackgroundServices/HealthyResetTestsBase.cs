using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.BackgroundServices;

namespace Warp.Tests.BackgroundServices;

[GenerateDatabaseTests]
public abstract class HealthyResetTestsBase : IntegrationTestBase
{
    protected HealthyResetTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task ServiceRanFor5Min_ThenFaults_RestartCountResetsToZero()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var state = new HealthyResetServiceState();
        var observer = new TestStatusObserver();
        var ct = Xunit.TestContext.Current.CancellationToken;

        // Register the first Running waiter BEFORE starting the server so we cannot miss
        // the transition even if the supervisor sets Running synchronously during startup.
        var firstRunningReached = observer.NextStatus(nameof(HealthyResetService), BackgroundServiceStatus.Running);

        await using var server = await WarpTestServer.StartWithFakeTime(
            Fixture,
            time,
            configure: cfg => cfg.AddBackgroundService<HealthyResetService>(),
            configureServices: services =>
            {
                services.AddSingleton(state);

                // Registered before AddBackgroundService's TryAddSingleton so this non-Try
                // call adds a second registration; .NET DI resolves the last one, giving us
                // the test observer.
                services.AddSingleton<IBackgroundServiceStatusObserver>(observer);
            });

        // Step 1: Supervisor wrote Running — first ExecuteAsync call is in-flight.
        await WaitForObserverAsync(firstRunningReached, "first Running transition", ct);

        // Wait for the service itself to signal that it is inside ExecuteAsync and parked
        // on CanAdvanceTime. This ensures the startedAt timestamp is captured before we advance.
        await state.RunningGate.WaitAsync(ct);

        // Step 2: Register Restarting waiter BEFORE releasing the first attempt so we don't
        // race the supervisor's post-fault path.
        var firstRestartingReached = observer.NextStatus(nameof(HealthyResetService), BackgroundServiceStatus.Restarting);

        // Advance fake time past the 5-minute healthy-reset threshold and release the first
        // attempt. The service returns without throwing → supervisor: RecordFault, HealthyReset
        // (ranFor ≥ 5 min → reset RestartCount to 0, reset backoffIndex), SetStatus(Restarting),
        // then Task.Delay(1s, _time, ct) which is blocked until we advance fake time.
        time.Advance(TimeSpan.FromMinutes(6));
        state.CanAdvanceTime.Release();

        await WaitForObserverAsync(firstRestartingReached, "first Restarting transition", ct);

        // Step 3: register BOTH the second Running waiter AND the second Restarting waiter
        // BEFORE pumping fake time. The second-attempt service body releases FaultedGate and
        // throws immediately (no gate), so under fast DB conditions (warm Postgres pool,
        // sub-millisecond UPDATEs) the supervisor races through SetStatus(Running) →
        // service.ExecuteAsync → RecordFault → SetStatus(Restarting) in well under the pump
        // loop's 10ms Task.Delay tick. If we registered the Restarting waiter *after* the pump
        // exits, the supervisor's SetStatus(Restarting) would fire OnStatusChanged with no
        // matching waiter — TestStatusObserver does not buffer unmatched events, so the
        // transition would be lost and the subsequent await would hang to the bounded-wait
        // timeout. Registering both up-front closes the race regardless of DB latency.
        var secondRunningReached = observer.NextStatus(nameof(HealthyResetService), BackgroundServiceStatus.Running);
        var secondRestartingReached = observer.NextStatus(nameof(HealthyResetService), BackgroundServiceStatus.Restarting);

        await PumpFakeTimeUntilAsync(
            time,
            secondRunningReached,
            step: TimeSpan.FromSeconds(1),
            description: "second Running transition",
            ct: ct);

        // Step 4: Wait for the service to signal that it is about to throw on the second attempt.
        await state.FaultedGate.WaitAsync(ct);

        // Supervisor: RecordFault (RestartCount = 1, because HealthyReset cleared it to 0 after
        // the first fault), HealthyReset check (ranFor < 5 min → no reset), SetStatus(Restarting).
        // The Restarting waiter was registered in step 3 so this completes regardless of how
        // fast the supervisor got there.
        await WaitForObserverAsync(secondRestartingReached, "second Restarting transition", ct);

        // Step 5: Assert. RestartCount should be 1:
        //   - First fault: RecordFault → count=1, HealthyReset fires → count reset to 0
        //   - Second fault: RecordFault → count=1, HealthyReset does NOT fire (ranFor < 5 min)
        // Without the healthy-reset, count would be 2.
        var ctx = Fixture.CreateContext();
        var instance = await ctx.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == server.ServerId)
            .Where(x => x.ServiceName == nameof(HealthyResetService))
            .FirstOrDefaultAsync(ct);

        instance.ShouldNotBeNull();
        instance.RestartCount.ShouldBe(
            1,
            "healthy-reset cleared the counter after the 5-min first run; the second fault then adds 1");
    }

    // Wall-clock budget per observer wait. Sized to fit comfortably inside [TimedFact]'s default
    // 10s budget while still surfacing real hangs fast with a named transition rather than
    // blowing the whole [TimedFact] silently. A healthy run hits each wait in well under a
    // second; if the budget here is being exhausted, the right response is to investigate what
    // the supervisor is stuck on — not to raise the budget.
    private static readonly TimeSpan ObserverWaitTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Awaits an observer task with a bounded wall-clock deadline. If the deadline hits before
    /// the observer fires, throws <see cref="TimeoutException"/> naming the missed transition.
    /// Without this, a dropped observer (e.g. a supervisor-side fault that prevents
    /// <c>SetStatusAsync</c> from completing) wedges the test until xUnit's <c>[TimedFact]</c>
    /// budget hits, with no information about which step actually stalled.
    /// </summary>
    private static async Task WaitForObserverAsync(Task observerTask, string description, CancellationToken ct)
    {
        var completed = await Task.WhenAny(observerTask, Task.Delay(ObserverWaitTimeout, ct));
        if (completed != observerTask)
        {
            throw new TimeoutException(
                $"HealthyResetService did not reach {description} within {ObserverWaitTimeout.TotalSeconds:F0}s.");
        }

        // Surface any exception captured on the observer task.
        await observerTask;
    }

    /// <summary>
    /// Drives <paramref name="time"/> forward in <paramref name="step"/> chunks until
    /// <paramref name="signal"/> completes, with a small real-time delay between advances to
    /// let the supervisor continuation actually run on contended runners. Bounded by
    /// <see cref="ObserverWaitTimeout"/>; throws <see cref="TimeoutException"/> naming
    /// <paramref name="description"/> on miss.
    /// </summary>
    private static async Task PumpFakeTimeUntilAsync(
        FakeTimeProvider time,
        Task signal,
        TimeSpan step,
        string description,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ObserverWaitTimeout;
        while (!signal.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"HealthyResetService did not reach {description} within {ObserverWaitTimeout.TotalSeconds:F0}s of fake-time pumping.");
            }

            time.Advance(step);
            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        }

        await signal;
    }
}
