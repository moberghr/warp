using Microsoft.EntityFrameworkCore;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Logging;

namespace Warp.Core.Sagas;

/// <summary>
/// The <see cref="IMessageHandler{TMessage}"/> registered for each <c>ISagaHandler&lt;TSaga, TMessage&gt;</c>
/// pair. The proxy:
/// <list type="number">
/// <item>extracts the correlation key from the message via <see cref="SagaCorrelationCache"/>;</item>
/// <item>acquires <c>warp:saga:{TSaga.FullName}:{key}</c> on <see cref="IWarpSemaphoreProvider"/> with timeout 0;</item>
/// <item>on success, loads the saga (or creates if the message has <see cref="StartsSagaAttribute"/>);</item>
/// <item>invokes the user's <c>ISagaHandler&lt;TSaga, TMessage&gt;.HandleAsync</c>;</item>
/// <item>saves the state (Insert / Update / Remove) and releases the lock.</item>
/// </list>
/// </summary>
public sealed class SagaHandlerProxy<TSaga, TMessage> : IMessageHandler<TMessage>
    where TSaga : Saga, new()
    where TMessage : class, IMessage
{
    // Per-generic-close cached reflection. Both are computed once per (TSaga, TMessage) pair the
    // first time the JIT closes this generic — never per message.
    private static readonly bool IsTimeoutMessage =
        typeof(ITimeoutMessage).IsAssignableFrom(typeof(TMessage));

    private static readonly bool HasStartsSagaAttribute =
        Attribute.IsDefined(typeof(TMessage), typeof(StartsSagaAttribute), inherit: false);

    private readonly ISagaHandler<TSaga, TMessage> _inner;
    private readonly ISagaStore _store;
    private readonly IWarpLockProvider _lockProvider;
    private readonly IJobContext _jobContext;
    private readonly TimeProvider _time;
    private readonly SagaCorrelationCache _correlationCache;

    public SagaHandlerProxy(
        ISagaHandler<TSaga, TMessage> inner,
        ISagaStore store,
        IWarpLockProvider lockProvider,
        IJobContext jobContext,
        TimeProvider time,
        SagaCorrelationCache correlationCache)
    {
        _inner = inner;
        _store = store;
        _lockProvider = lockProvider;
        _jobContext = jobContext;
        _time = time;
        _correlationCache = correlationCache;
    }

    public async Task HandleAsync(TMessage message, CancellationToken cancellationToken)
    {
        var correlationKey = _correlationCache.GetCorrelationKey(message);
        var lockName = $"warp:saga:{typeof(TSaga).FullName}:{correlationKey}";

        // Lock provider (sp_getapplock / pg_try_advisory_lock with timeout 0) rather than
        // semaphore (maxCount=1) — the latter is Medallion's row-based protocol, which under
        // SQL Server contention can BLOCK on transaction row locks even with timeout 0.
        // Reproduced as a 20s TimedFact hang in TwoConcurrentMessagesSameSaga
        // (#211 investigation): first contender fast-failed (~80 ms) but a second concurrent
        // attempt on the same key wedged on the semaphore-state row lock for the test budget.
        // The maxCount-1 semaphore is semantically a mutex; sp_getapplock is the correct
        // primitive.
        var handle = await _lockProvider.TryAcquireAsync(lockName, TimeSpan.Zero, cancellationToken);
        if (handle == null)
        {
            _jobContext.Outcome = BuildBusyOutcome(correlationKey, _time.GetUtcNow().UtcDateTime);
            WarpTelemetry.SagasRequeued.Add(
                1,
                new KeyValuePair<string, object?>("saga_type", typeof(TSaga).Name),
                new KeyValuePair<string, object?>("reason", "busy"));

            return;
        }

        try
        {
            await ProcessAsync(message, correlationKey, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The handler threw uncaught. Anything the handler added to the change tracker via
            // IPublisher.Publish or saga mutation is *not* what we want the worker to commit —
            // discard so the worker's outbox SaveChanges has an empty change set. The exception
            // bubbles up to the worker's retry / fail handling.
            _store.DiscardPendingChanges();
            throw;
        }
        finally
        {
            await handle.DisposeAsync();
        }
    }

    private async Task ProcessAsync(TMessage message, string correlationKey, CancellationToken cancellationToken)
    {
        var saga = await _store.Load<TSaga>(correlationKey, cancellationToken);

        // Three branches, each ~15 lines. The dispatch lives here; the branches live below.
        if (saga == null && IsTimeoutMessage)
        {
            HandleExpiredTimeout(correlationKey);

            return;
        }

        if (saga == null && !HasStartsSagaAttribute)
        {
            await HandleMissingSaga(message, correlationKey, cancellationToken);

            return;
        }

        var started = saga == null;
        var completed = started
            ? await HandleStartsSaga(message, correlationKey, cancellationToken)
            : await HandleExistingSaga(saga!, message, cancellationToken);

        // Stage dashboard Counter rows *before* the save so they commit atomically with the
        // saga's own change set. The save-conflict path clears the change tracker and the
        // counter deltas roll back with it — Counters reflect only logical-success outcomes,
        // mirroring the OTel emit gate below. Hour-bucket keys feed the historical chart.
        if (started || completed)
        {
            var nowUtc = _time.GetUtcNow().UtcDateTime;
            var hour = nowUtc.ToString("yyyy-MM-dd-HH", System.Globalization.CultureInfo.InvariantCulture);
            if (started)
            {
                _store.RecordCounterDelta("stats:saga_started", 1);
                _store.RecordCounterDelta($"stats:saga_started:{hour}", 1);
            }

            if (completed)
            {
                _store.RecordCounterDelta("stats:saga_completed", 1);
                _store.RecordCounterDelta($"stats:saga_completed:{hour}", 1);
            }
        }

        await TrySaveAsync(correlationKey, cancellationToken);

        // OTel lifecycle counters fire only on a clean save. If TrySaveAsync hit a conflict it
        // set the requeue Outcome; firing here would double-count when the retry runs the same
        // handler and reaches the same Started/Completed branches again. The outcome-gate
        // also covers any other path that pre-set an Outcome before we got here.
        if (_jobContext.Outcome == null)
        {
            var sagaType = new KeyValuePair<string, object?>("saga_type", typeof(TSaga).Name);
            if (started)
            {
                WarpTelemetry.SagasStarted.Add(1, sagaType);
                WarpTelemetry.SagasLive.Add(1, sagaType);
            }

            if (completed)
            {
                WarpTelemetry.SagasCompleted.Add(1, sagaType);
                WarpTelemetry.SagasLive.Add(-1, sagaType);
            }
        }
    }

    private void HandleExpiredTimeout(string correlationKey)
    {
        // Wolverine pattern: a timeout that arrives after the saga has already completed is moot.
        // Silently drop rather than failing — the saga did its work, the timeout was a safety net.
        _jobContext.Outcome = BuildExpiredTimeoutOutcome(correlationKey);
    }

    private async Task HandleMissingSaga(TMessage message, string correlationKey, CancellationToken cancellationToken)
    {
        _jobContext.Outcome = BuildNotFoundOutcome(correlationKey);
        await _inner.NotFoundAsync(message, _jobContext, cancellationToken);
    }

    // Returns whether the saga reached the IsCompleted state inside this invocation.
    // Started=true is implied by the saga==null entry condition.
    private async Task<bool> HandleStartsSaga(TMessage message, string correlationKey, CancellationToken cancellationToken)
    {
        var saga = new TSaga { CorrelationKey = correlationKey };
        await _inner.HandleAsync(saga, message, cancellationToken);

        if (saga.IsCompleted)
        {
            // [StartsSaga] message that completes the saga in the same call (rare but legal).
            // Nothing to persist for the saga itself — never inserted. The caller still runs
            // TrySaveAsync after we return: the handler may have invoked the publisher to add
            // child rows, and that commit must fire its notifications via
            // SagaStore.SaveChangesAsync rather than the worker's outbox (which sees the rows
            // Unchanged by then). Both lifecycle counters (started + completed) fire in
            // ProcessAsync once the save lands, so an ephemeral saga still gets observability.
            return true;
        }

        _store.Add(saga);
        _store.RecordJobLink(saga.Id, _jobContext.JobId);

        return false;
    }

    private async Task<bool> HandleExistingSaga(TSaga saga, TMessage message, CancellationToken cancellationToken)
    {
        await _inner.HandleAsync(saga, message, cancellationToken);

        if (saga.IsCompleted)
        {
            _store.Remove(saga);
            await _store.RemoveLinksForSagaAsync(saga.Id, cancellationToken);

            return true;
        }

        _store.Update(saga);
        _store.RecordJobLink(saga.Id, _jobContext.JobId);

        return false;
    }

    private async Task TrySaveAsync(string correlationKey, CancellationToken cancellationToken)
    {
        try
        {
            await _store.SaveChangesAsync(cancellationToken);
        }
        catch (SagaSaveConflictException ex)
        {
            // SagaStore.SaveChangesAsync already cleared the change tracker before
            // rethrowing — the worker's outbox commit will see an empty change set.
            var now = _time.GetUtcNow().UtcDateTime;
            var (reason, message) = ex.Kind switch
            {
                SagaSaveConflictKind.Version => ("version", $"Requeued — saga '{typeof(TSaga).Name}' version conflict for '{correlationKey}'"),
                SagaSaveConflictKind.UniqueConstraint => ("unique", $"Requeued — saga '{typeof(TSaga).Name}' unique-key conflict (concurrent start) for '{correlationKey}'"),
                _ => ("conflict", $"Requeued — saga '{typeof(TSaga).Name}' save conflict for '{correlationKey}'"),
            };
            _jobContext.Outcome = BuildRequeueOutcome(now, message);
            WarpTelemetry.SagasRequeued.Add(
                1,
                new KeyValuePair<string, object?>("saga_type", typeof(TSaga).Name),
                new KeyValuePair<string, object?>("reason", reason));
        }
    }

    // Capped jitter in [50, 250) ms so that N workers contending on the same correlation key
    // don't lock-step into a fetch→busy→requeue→fetch hot loop. Far cheaper than the alternative
    // (a true exponential backoff with attempt-count tracking) and good enough to break sympathy.
    private static DateTime JitteredRequeueTime(DateTime now)
        => now.AddMilliseconds(System.Random.Shared.Next(50, 250));

    private static JobOutcome BuildBusyOutcome(string key, DateTime now)
    {
        var schedule = JitteredRequeueTime(now);

        return new JobOutcome
        {
            // RescheduledState is the documented invariant for any pipeline behavior that
            // reschedules (JobOutcome.cs). With jitter the schedule lies in the near future,
            // so the row goes to Scheduled and ScheduledJobActivation flips it back.
            State = JobOutcome.RescheduledState(schedule, now),
            ScheduleTime = schedule,

            // Keep HandlerType set so the next worker attempt re-enters this saga proxy
            // rather than the IJobHandler<TMessage> discovery path (which would fail because
            // TMessage : IMessage, not IJob).
            ClearHandlerType = false,
            LogMessage = $"Requeued — saga '{typeof(TSaga).Name}' busy for '{key}'",
        };
    }

    private static JobOutcome BuildRequeueOutcome(DateTime now, string logMessage)
    {
        var schedule = JitteredRequeueTime(now);

        return new JobOutcome
        {
            State = JobOutcome.RescheduledState(schedule, now),
            ScheduleTime = schedule,
            ClearHandlerType = false,
            LogMessage = logMessage,
        };
    }

    private static JobOutcome BuildNotFoundOutcome(string key) =>
        new()
        {
            State = State.Failed,
            LogMessage = $"No saga of type '{typeof(TSaga).Name}' for correlation key '{key}'",
        };

    private static JobOutcome BuildExpiredTimeoutOutcome(string key) =>
        new()
        {
            State = State.Deleted,
            LogMessage = $"Timeout '{typeof(TMessage).Name}' fired after saga '{typeof(TSaga).Name}' (key '{key}') completed — moot",
        };
}
