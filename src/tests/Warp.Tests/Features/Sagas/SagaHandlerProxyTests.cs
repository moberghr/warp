using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Sagas;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Features.Sagas;

[Trait("Category", "NoDb")]
public class SagaHandlerProxyTests
{
    [TimedFact]
    public async Task NewCorrelation_StartsSagaMessage_CreatesAndInvokesHandler()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        var handler = new RecordingHandler();
        var proxy = new SagaHandlerProxy<OrderSaga, StartOrder>(handler, store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new StartOrder { OrderId = "O-1" }, CancellationToken.None);

        handler.HandleInvocations.Count.ShouldBe(1);
        handler.HandleInvocations[0].saga.CorrelationKey.ShouldBe("O-1");
        store.AddCount.ShouldBe(1);
        store.SaveCount.ShouldBe(1);
        store.RecordJobLinkCount.ShouldBe(1);
        store.ContainsSaga<OrderSaga>("O-1").ShouldBeTrue();
        jobContext.Outcome.ShouldBeNull();
    }

    [TimedFact]
    public async Task ExistingSaga_LoadsAndInvokesHandler()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        store.Seed("O-2", new OrderSaga { CorrelationKey = "O-2", PaymentCaptured = false });
        var handler = new RecordingHandler();
        var proxy = new SagaHandlerProxy<OrderSaga, ContinueOrder>(handler, store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new ContinueOrder { OrderId = "O-2" }, CancellationToken.None);

        handler.HandleInvocations.Count.ShouldBe(1);
        handler.HandleInvocations[0].saga.CorrelationKey.ShouldBe("O-2");
        store.UpdateCount.ShouldBe(1);
        store.AddCount.ShouldBe(0);
        store.SaveCount.ShouldBe(1);
        store.RecordJobLinkCount.ShouldBe(1);
        store.RemoveLinksForSagaCount.ShouldBe(0);
        jobContext.Outcome.ShouldBeNull();
    }

    [TimedFact]
    public async Task MutexBusy_SetsRequeueOutcome_HandlerNotInvoked()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        await using var holder = locks.HoldLock($"warp:saga:{typeof(OrderSaga).FullName}:O-3");
        var handler = new RecordingHandler();
        var proxy = new SagaHandlerProxy<OrderSaga, ContinueOrder>(handler, store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new ContinueOrder { OrderId = "O-3" }, CancellationToken.None);

        handler.HandleInvocations.Count.ShouldBe(0);
        store.LoadCount.ShouldBe(0);
        jobContext.Outcome.ShouldNotBeNull();
        jobContext.Outcome.State.ShouldBe(State.Scheduled);
        jobContext.Outcome.ClearHandlerType.ShouldBeFalse();
        jobContext.Outcome.LogMessage!.ShouldContain("busy");
    }

    [TimedFact]
    public async Task NoSagaAndNoStartsSaga_DefaultNotFound_SetsFailedOutcome()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        var handler = new RecordingHandler();
        var proxy = new SagaHandlerProxy<OrderSaga, ContinueOrder>(handler, store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new ContinueOrder { OrderId = "missing" }, CancellationToken.None);

        handler.HandleInvocations.Count.ShouldBe(0);
        handler.NotFoundInvocations.Count.ShouldBe(1);
        jobContext.Outcome.ShouldNotBeNull();
        jobContext.Outcome.State.ShouldBe(State.Failed);
        jobContext.Outcome.LogMessage!.ShouldContain("No saga");
        store.AddCount.ShouldBe(0);
        store.UpdateCount.ShouldBe(0);
    }

    [TimedFact]
    public async Task NoSagaAndNoStartsSaga_OverriddenNotFound_PreservesHandlerSetOutcome()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        var handler = new OverridingNotFoundHandler();
        var proxy = new SagaHandlerProxy<OrderSaga, ContinueOrder>(handler, store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new ContinueOrder { OrderId = "ignore-me" }, CancellationToken.None);

        handler.HandleInvocations.Count.ShouldBe(0);
        handler.NotFoundInvocations.Count.ShouldBe(1);
        jobContext.Outcome.ShouldNotBeNull();
        jobContext.Outcome.State.ShouldBe(State.Deleted);
        jobContext.Outcome.LogMessage!.ShouldContain("silent");
    }

    [TimedFact]
    public async Task MarkCompleted_RemovesRow()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        store.Seed("O-4", new OrderSaga { CorrelationKey = "O-4" });
        var handler = new CompletingHandler();
        var proxy = new SagaHandlerProxy<OrderSaga, ContinueOrder>(handler, store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new ContinueOrder { OrderId = "O-4" }, CancellationToken.None);

        store.RemoveCount.ShouldBe(1);
        store.UpdateCount.ShouldBe(0);
        store.RemoveLinksForSagaCount.ShouldBe(1);
        store.RecordJobLinkCount.ShouldBe(0);
        store.ContainsSaga<OrderSaga>("O-4").ShouldBeFalse();
    }

    [TimedFact]
    public async Task VersionConflictOnSave_SetsRequeueOutcome()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        store.Seed("O-5", new OrderSaga { CorrelationKey = "O-5" });
        store.ThrowOnNextSave = true;
        var handler = new RecordingHandler();
        var proxy = new SagaHandlerProxy<OrderSaga, ContinueOrder>(handler, store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new ContinueOrder { OrderId = "O-5" }, CancellationToken.None);

        jobContext.Outcome.ShouldNotBeNull();
        jobContext.Outcome.State.ShouldBe(State.Scheduled);
        jobContext.Outcome.ClearHandlerType.ShouldBeFalse();
        jobContext.Outcome.LogMessage!.ShouldContain("version conflict");

        // Jitter: ScheduleTime must be in the future, but capped at <500ms from now.
        var delta = jobContext.Outcome.ScheduleTime!.Value - DateTime.UtcNow;
        delta.TotalMilliseconds.ShouldBeLessThan(500);
    }

    [TimedFact]
    public async Task UniqueConstraintConflictOnSave_SetsRequeueOutcome_WithUniqueReason()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        store.ThrowConflictKindOnNextSave = SagaSaveConflictKind.UniqueConstraint;
        var handler = new RecordingHandler();
        var proxy = new SagaHandlerProxy<OrderSaga, StartOrder>(handler, store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new StartOrder { OrderId = "O-race" }, CancellationToken.None);

        jobContext.Outcome.ShouldNotBeNull();
        jobContext.Outcome.State.ShouldBe(State.Scheduled);
        jobContext.Outcome.ClearHandlerType.ShouldBeFalse();
        jobContext.Outcome.LogMessage!.ShouldContain("unique-key conflict");
    }

    [TimedFact]
    public async Task MutexBusy_RequeueOutcome_HasJitteredScheduleTime()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        await using var holder = locks.HoldLock($"warp:saga:{typeof(OrderSaga).FullName}:O-jitter");
        var proxy = new SagaHandlerProxy<OrderSaga, ContinueOrder>(new RecordingHandler(), store, locks, jobContext, time, cache);

        var before = DateTime.UtcNow;
        await proxy.HandleAsync(new ContinueOrder { OrderId = "O-jitter" }, CancellationToken.None);

        // The busy outcome must schedule strictly in the future (>= 50ms per the jitter range)
        // so concurrent same-key requeues don't lock-step into a hot loop.
        jobContext.Outcome.ShouldNotBeNull();
        jobContext.Outcome.ScheduleTime!.Value.ShouldBeGreaterThan(before);
        (jobContext.Outcome.ScheduleTime!.Value - before).TotalMilliseconds.ShouldBeLessThan(500);
    }

    [TimedFact]
    public async Task SuccessfulStart_WritesSagaStartedCounters_BothCumulativeAndHourBucket()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        var proxy = new SagaHandlerProxy<OrderSaga, StartOrder>(new RecordingHandler(), store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new StartOrder { OrderId = "O-counter" }, CancellationToken.None);

        // Cumulative key for the dashboard's headline counter.
        store.CounterDeltas.ShouldContainKey("stats:saga_started");
        store.CounterDeltas["stats:saga_started"].ShouldBe(1);

        // Hour-bucket key for the historical chart. Exact suffix is time-dependent; assert
        // exactly one matches the prefix.
        var hourKeys = store.CounterDeltas.Keys.Where(k => k.StartsWith("stats:saga_started:", StringComparison.Ordinal)).ToList();
        hourKeys.Count.ShouldBe(1);
        store.CounterDeltas[hourKeys[0]].ShouldBe(1);

        // No completion counter — only the start fired.
        store.CounterDeltas.ContainsKey("stats:saga_completed").ShouldBeFalse();
    }

    [TimedFact]
    public async Task InstantCompleteStartsSaga_WritesBothStartedAndCompletedCounters()
    {
        // [StartsSaga] handler calls MarkCompleted in the same invocation — both lifecycle
        // counters must fire so observability records the ephemeral saga.
        var (store, locks, jobContext, cache, time) = SetUp();
        var proxy = new SagaHandlerProxy<OrderSaga, StartOrder>(new InstantCompleteStartHandler(), store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new StartOrder { OrderId = "O-ephemeral" }, CancellationToken.None);

        store.CounterDeltas["stats:saga_started"].ShouldBe(1);
        store.CounterDeltas["stats:saga_completed"].ShouldBe(1);
    }

    [TimedFact]
    public async Task SaveConflict_RollsBackCounterDeltas()
    {
        // The DB counters are staged in the change tracker so they commit atomically with the
        // saga's own state. On conflict the tracker clears; the counter rows must vanish too
        // so a retry's increment lands exactly once. Mirrors the SagasCompleted OTel gate.
        var (store, locks, jobContext, cache, time) = SetUp();
        store.Seed("O-rollback", new OrderSaga { CorrelationKey = "O-rollback" });
        store.ThrowConflictKindOnNextSave = SagaSaveConflictKind.Version;

        var proxy = new SagaHandlerProxy<OrderSaga, ContinueOrder>(new CompletingHandler(), store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new ContinueOrder { OrderId = "O-rollback" }, CancellationToken.None);

        jobContext.Outcome.ShouldNotBeNull();
        jobContext.Outcome.LogMessage!.ShouldContain("version conflict");

        // CounterDeltas reflects committed values only — staged increments were discarded by
        // ThrowConflictKindOnNextSave alongside the saga's own pending change.
        store.CounterDeltas.ContainsKey("stats:saga_completed").ShouldBeFalse();
    }

    [TimedFact]
    public async Task MarkCompleted_CalledTwice_IsIdempotent()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        store.Seed("O-double", new OrderSaga { CorrelationKey = "O-double" });
        var handler = new DoubleCompletingHandler();
        var proxy = new SagaHandlerProxy<OrderSaga, ContinueOrder>(handler, store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new ContinueOrder { OrderId = "O-double" }, CancellationToken.None);

        store.RemoveCount.ShouldBe(1);
        store.UpdateCount.ShouldBe(0);
        store.RemoveLinksForSagaCount.ShouldBe(1);
        store.ContainsSaga<OrderSaga>("O-double").ShouldBeFalse();
    }

    [TimedFact]
    public async Task StartsSaga_InstantMarkCompleted_FallsThroughToSave_AndEmitsBothCounters()
    {
        // The proxy's "[StartsSaga] handler completes the saga in the same call" branch:
        // no row is Added/Removed (never inserted), but SaveChangesAsync MUST still fire so
        // any IPublisher.Publish(child) calls inside the handler commit with notifications.
        // Both Started and Completed counters fire so observability records the ephemeral saga.
        var (store, locks, jobContext, cache, time) = SetUp();
        var handler = new InstantCompleteStartHandler();
        var proxy = new SagaHandlerProxy<OrderSaga, StartOrder>(handler, store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new StartOrder { OrderId = "instant" }, CancellationToken.None);

        handler.HandleInvocations.Count.ShouldBe(1);
        store.AddCount.ShouldBe(0);
        store.UpdateCount.ShouldBe(0);
        store.RemoveCount.ShouldBe(0);
        store.SaveCount.ShouldBe(1);
        jobContext.Outcome.ShouldBeNull();
    }

    [TimedFact]
    public async Task TimeoutMessage_NoSaga_SetsDeletedOutcome_DoesNotCallNotFound()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        var handler = new TimeoutAwareHandler();
        var proxy = new SagaHandlerProxy<OrderSaga, OrderDeadline>(handler, store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new OrderDeadline { OrderId = "expired" }, CancellationToken.None);

        handler.HandleInvocations.Count.ShouldBe(0);
        handler.NotFoundInvocations.Count.ShouldBe(0); // NotFoundAsync is bypassed for timeouts
        jobContext.Outcome.ShouldNotBeNull();
        jobContext.Outcome.State.ShouldBe(State.Deleted);
        jobContext.Outcome.LogMessage!.ShouldContain("moot");
    }

    [TimedFact]
    public async Task TimeoutMessage_ExistingSaga_InvokesHandler()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        store.Seed("live", new OrderSaga { CorrelationKey = "live" });
        var handler = new TimeoutAwareHandler();
        var proxy = new SagaHandlerProxy<OrderSaga, OrderDeadline>(handler, store, locks, jobContext, time, cache);

        await proxy.HandleAsync(new OrderDeadline { OrderId = "live" }, CancellationToken.None);

        handler.HandleInvocations.Count.ShouldBe(1);
        jobContext.Outcome.ShouldBeNull();
    }

    [TimedFact]
    public async Task HandlerThrows_PropagatesException_AndReleasesMutex()
    {
        var (store, locks, jobContext, cache, time) = SetUp();
        store.Seed("O-6", new OrderSaga { CorrelationKey = "O-6" });
        var handler = new ThrowingHandler();
        var proxy = new SagaHandlerProxy<OrderSaga, ContinueOrder>(handler, store, locks, jobContext, time, cache);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            proxy.HandleAsync(new ContinueOrder { OrderId = "O-6" }, CancellationToken.None));

        // Mutex released — a second invocation should acquire successfully (no busy outcome).
        var probeContext = new JobContext();
        var probe = new SagaHandlerProxy<OrderSaga, ContinueOrder>(new RecordingHandler(), store, locks, probeContext, time, cache);
        await probe.HandleAsync(new ContinueOrder { OrderId = "O-6" }, CancellationToken.None);

        // If the lock had not been released, the probe would have short-circuited with a busy
        // Outcome (State.Enqueued, ClearHandlerType=false) before reaching the store.
        probeContext.Outcome.ShouldBeNull();
    }

    private static (FakeSagaStore store, FakeLockProvider locks, JobContext jobContext, SagaCorrelationCache cache, TimeProvider time) SetUp()
    {
        return (new FakeSagaStore(), new FakeLockProvider(), new JobContext(), new SagaCorrelationCache(), TimeProvider.System);
    }

    public sealed class OrderSaga : Saga
    {
        public bool PaymentCaptured { get; set; }
    }

    [StartsSaga]
    public sealed class StartOrder : IMessage
    {
        [Correlate]
        public string OrderId { get; set; } = string.Empty;
    }

    public sealed class ContinueOrder : IMessage
    {
        [Correlate]
        public string OrderId { get; set; } = string.Empty;
    }

    public sealed class OrderDeadline : ITimeoutMessage
    {
        [Correlate]
        public string OrderId { get; set; } = string.Empty;

        public TimeSpan Delay => TimeSpan.FromMilliseconds(50);
    }

    private sealed class TimeoutAwareHandler : ISagaHandler<OrderSaga, OrderDeadline>
    {
        public List<object> HandleInvocations { get; } = [];

        public List<object> NotFoundInvocations { get; } = [];

        public Task HandleAsync(OrderSaga saga, OrderDeadline message, CancellationToken cancellationToken)
        {
            HandleInvocations.Add(message);
            return Task.CompletedTask;
        }

        public Task NotFoundAsync(OrderDeadline message, IJobContext context, CancellationToken cancellationToken)
        {
            NotFoundInvocations.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler :
        ISagaHandler<OrderSaga, StartOrder>,
        ISagaHandler<OrderSaga, ContinueOrder>
    {
        public List<(OrderSaga saga, object message)> HandleInvocations { get; } = [];

        public List<object> NotFoundInvocations { get; } = [];

        public Task HandleAsync(OrderSaga saga, StartOrder message, CancellationToken ct)
        {
            HandleInvocations.Add((saga, message));
            return Task.CompletedTask;
        }

        public Task HandleAsync(OrderSaga saga, ContinueOrder message, CancellationToken ct)
        {
            HandleInvocations.Add((saga, message));
            return Task.CompletedTask;
        }

        public Task NotFoundAsync(StartOrder message, IJobContext context, CancellationToken ct)
        {
            NotFoundInvocations.Add(message);
            return Task.CompletedTask;
        }

        public Task NotFoundAsync(ContinueOrder message, IJobContext context, CancellationToken ct)
        {
            NotFoundInvocations.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class OverridingNotFoundHandler : ISagaHandler<OrderSaga, ContinueOrder>
    {
        public List<object> HandleInvocations { get; } = [];

        public List<object> NotFoundInvocations { get; } = [];

        public Task HandleAsync(OrderSaga saga, ContinueOrder message, CancellationToken ct)
        {
            HandleInvocations.Add(message);
            return Task.CompletedTask;
        }

        public Task NotFoundAsync(ContinueOrder message, IJobContext context, CancellationToken ct)
        {
            NotFoundInvocations.Add(message);
            context.Outcome = new JobOutcome
            {
                State = State.Deleted,
                LogMessage = "silent skip",
            };
            return Task.CompletedTask;
        }
    }

    private sealed class CompletingHandler : ISagaHandler<OrderSaga, ContinueOrder>
    {
        public Task HandleAsync(OrderSaga saga, ContinueOrder message, CancellationToken ct)
        {
            saga.MarkCompleted();
            return Task.CompletedTask;
        }
    }

    private sealed class DoubleCompletingHandler : ISagaHandler<OrderSaga, ContinueOrder>
    {
        public Task HandleAsync(OrderSaga saga, ContinueOrder message, CancellationToken ct)
        {
            saga.MarkCompleted();
            saga.MarkCompleted();
            saga.IsCompleted.ShouldBeTrue();
            return Task.CompletedTask;
        }
    }

    private sealed class InstantCompleteStartHandler : ISagaHandler<OrderSaga, StartOrder>
    {
        public List<object> HandleInvocations { get; } = [];

        public Task HandleAsync(OrderSaga saga, StartOrder message, CancellationToken ct)
        {
            HandleInvocations.Add(message);
            saga.MarkCompleted();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : ISagaHandler<OrderSaga, ContinueOrder>
    {
        public Task HandleAsync(OrderSaga saga, ContinueOrder message, CancellationToken ct)
        {
            throw new InvalidOperationException("boom");
        }
    }
}
