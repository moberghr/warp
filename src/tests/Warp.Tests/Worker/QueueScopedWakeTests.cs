using Shouldly;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Worker;

namespace Warp.Tests.Worker;

// The JobEnqueued wake is queue-scoped (rule 6.3): a worker group is woken only for the queues it drains.
// Before, every enqueue woke every local group, and 8.1's dedicated warp:webhooks group turned that into an
// empty claim per job (+1 statement, command and transaction per job on the fan-out benchmark).
[Trait("Category", "NoDb")]
public class QueueScopedWakeTests
{
    [TimedFact]
    public void SignalJobEnqueued_FilteredSubscriber_WokenForItsQueueAndForNoQueueOnly()
    {
        var signals = new ServerTaskSignals<TestContext>();
        var woken = 0;
        using var subscription = signals.SubscribeJobEnqueued(["a", "b"], () => woken++);

        signals.SignalJobEnqueued("c");
        woken.ShouldBe(0, "an enqueue to a queue the group does not drain must not wake it");

        signals.SignalJobEnqueued("A");
        woken.ShouldBe(0, "queue matching is ordinal and exact");

        signals.SignalJobEnqueued("a");
        signals.SignalJobEnqueued("b");
        woken.ShouldBe(2);

        signals.SignalJobEnqueued(null);
        woken.ShouldBe(3, "a signal that names no queue wakes everyone");

        signals.SignalJobEnqueued();
        woken.ShouldBe(4, "the parameterless form names no queue");
    }

    [TimedFact]
    public void SignalJobEnqueued_PlainSubscriber_WokenForEveryQueue()
    {
        var signals = new ServerTaskSignals<TestContext>();
        var woken = 0;
        using var subscription = signals.Subscribe(ServerTaskSignal.JobEnqueued, () => woken++);

        signals.SignalJobEnqueued("a");
        signals.SignalJobEnqueued("warp:webhooks");
        signals.SignalJobEnqueued(null);

        woken.ShouldBe(3);
    }

    [TimedFact]
    public void SignalJobEnqueued_FilteredSubscriptionDisposed_NoLongerWoken()
    {
        var signals = new ServerTaskSignals<TestContext>();
        var woken = 0;
        var subscription = signals.SubscribeJobEnqueued(["a"], () => woken++);

        subscription.Dispose();
        signals.SignalJobEnqueued("a");

        woken.ShouldBe(0);
    }

    [TimedFact]
    public void Signal_QueueScopedRegistration_WokenForItsQueueAndForNoQueueOnly()
    {
        var registry = new DispatcherRegistry();
        using var semaphore = new SemaphoreSlim(0);
        using var registration = registry.Register(semaphore, ["a", "b"]);

        registry.Signal("c");
        semaphore.CurrentCount.ShouldBe(0, "a push for a queue the group does not drain must not wake it");

        registry.Signal("a");
        semaphore.CurrentCount.ShouldBe(1);
        semaphore.Wait(0).ShouldBeTrue();

        registry.Signal(null);
        semaphore.CurrentCount.ShouldBe(1, "a push that names no queue wakes everyone");
        semaphore.Wait(0).ShouldBeTrue();

        registry.SignalAll();
        semaphore.CurrentCount.ShouldBe(1);
    }

    [TimedFact]
    public void Signal_UnscopedRegistration_WokenForEveryQueue()
    {
        var registry = new DispatcherRegistry();
        using var semaphore = new SemaphoreSlim(0);
        using var registration = registry.Register(semaphore);

        registry.Signal("anything");

        semaphore.CurrentCount.ShouldBe(1);
    }

    [TimedFact]
    public void Signal_BurstOfPushes_LeavesAtMostOnePendingWake()
    {
        var registry = new DispatcherRegistry();
        using var semaphore = new SemaphoreSlim(0);
        using var registration = registry.Register(semaphore, ["a"]);

        registry.Signal("a");
        registry.Signal("a");

        semaphore.CurrentCount.ShouldBe(1);
    }

    [TimedFact]
    public void Signal_ThroughTheCoreSeam_IsQueueScoped()
    {
        // The listener calls IDispatcherWake.Signal(queue); DispatcherRegistry must implement it rather than
        // fall back to the interface's wake-everyone default.
        var registry = new DispatcherRegistry();
        using var semaphore = new SemaphoreSlim(0);
        using var registration = registry.Register(semaphore, ["a"]);
        IDispatcherWake wake = registry;

        wake.Signal("b");

        semaphore.CurrentCount.ShouldBe(0);
    }

    [TimedFact]
    public void Signal_ImplementationWithoutQueues_FallsBackToSignalAll()
    {
        var wake = new CountingWake();

        ((IDispatcherWake)wake).Signal("a");

        wake.SignalAllCalls.ShouldBe(1);
    }

    [TimedFact]
    public void Signal_DisposedRegistration_NotWoken()
    {
        var registry = new DispatcherRegistry();
        using var semaphore = new SemaphoreSlim(0);
        var registration = registry.Register(semaphore, ["a"]);

        registration.Dispose();
        registry.Signal("a");

        semaphore.CurrentCount.ShouldBe(0);
    }

    private sealed class CountingWake : IDispatcherWake
    {
        public int SignalAllCalls { get; private set; }

        public void SignalAll() => SignalAllCalls++;
    }
}
