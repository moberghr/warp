using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Options;
using Warp.Core.Enums;
using Warp.Core.Handlers;

namespace Warp.Core.CircuitBreaker;

public class CircuitBreakerPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IJob
{
    private static readonly ConcurrentDictionary<Type, CircuitBreakerAttribute?> AttributeCache = new();

    private readonly IJobContext _jobContext;
    private readonly IOptions<CircuitBreakerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ICircuitBreakerStore _store;

    public CircuitBreakerPipelineBehavior(
        IJobContext jobContext,
        IOptions<CircuitBreakerOptions> options,
        TimeProvider timeProvider,
        ICircuitBreakerStore store)
    {
        _jobContext = jobContext;
        _options = options;
        _timeProvider = timeProvider;
        _store = store;
    }

    public async Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
    {
        var attr = GetCircuitBreakerAttribute();
        var groupKey = attr?.Group ?? typeof(TRequest).Name;
        var options = _options.Value;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var state = await _store.GetAsync(groupKey, cancellationToken);

        // HalfOpen: another worker is already probing. Reschedule; do not run the handler.
        if (state?.State == CircuitState.HalfOpen)
        {
            _jobContext.Outcome = BuildReschedule(now, now, attr, options, groupKey, "probe-in-progress");

            return default!;
        }

        // Open, not expired: reschedule until OpenUntil. Gate on OpenUntil rather than
        // State so that rows written before the State column was introduced still behave
        // correctly (State defaults to Closed for those rows).
        if (state?.OpenUntil is { } openUntil && openUntil > now)
        {
            _jobContext.Outcome = BuildReschedule(openUntil, now, attr, options, groupKey, "open");

            return default!;
        }

        // Open but expired: race for the HalfOpen probe slot. Only State == Open rows
        // participate — pre-column rows that happen to have OpenUntil in the past are
        // treated as closed (the implicit healing window has passed).
        if (state?.State == CircuitState.Open && state.OpenUntil is { } expired && expired <= now)
        {
            var probeWon = await _store.TryBeginProbeAsync(groupKey, now, cancellationToken);
            if (!probeWon)
            {
                _jobContext.Outcome = BuildReschedule(now, now, attr, options, groupKey, "probe-lost");

                return default!;
            }

            // We won — fall through and execute the handler as the recovery probe.
        }

        try
        {
            var response = await next(request, cancellationToken);

            // Only reset on a real handler success. If another behavior (e.g. Mutex) set an Outcome,
            // the handler didn't actually complete — treat as a non-event for the circuit counter.
            // state is a pre-execution snapshot. Two acknowledged skews:
            //   1. Concurrent failure during handler execution: if another worker incremented
            //      FailureCount while next() was running, our snapshot may still read 0 → skip
            //      reset. The next clean success corrects it.
            //   2. Probe-winner reset after a concurrent failure: snapshot shows Open with
            //      FailureCount > 0, handler succeeds, ResetAsync fires. A concurrent failure
            //      recorded during the probe's handler is WIPED by that reset. Tolerated: the
            //      probe succeeded, so treating the circuit as healthy is acceptable; a fresh
            //      failure on the next request will re-open the circuit.
            if (_jobContext.Outcome is null && state?.FailureCount > 0)
            {
                await _store.ResetAsync(groupKey, cancellationToken);
            }

            return response;
        }
        catch
        {
            // An inner behaviour's NON-FAILURE outcome means the attempt is not settled (a retry/rate-limit
            // reschedule — it will run again) or was deliberately discarded (a timeout/concurrency delete) —
            // neither is evidence about the downstream's health, so neither counts.
            //
            // A FAILED outcome is the opposite: it is the raw dependency failure itself, labelled by the
            // behaviour that watched the budget die on it (retry exhaustion stamps Failed/RetryExhausted).
            // With this breaker registered outer of retry — the documented order — that terminal throw is
            // the ONLY failure of a retried job that ever reaches this catch un-rescheduled; treating
            // "an outcome exists" as "don't count" made the breaker blind to every failure of every retried
            // job, and the circuit never opened during exactly the outage it exists for.
            if (_jobContext.Outcome is { } outcome && outcome.State != State.Failed)
            {
                throw;
            }

            var threshold = attr?.GetThreshold(options) ?? options.Threshold;
            var duration = attr?.GetDuration(options) ?? options.Duration;
            await _store.RecordFailureAsync(groupKey, threshold, duration, now, cancellationToken);

            throw;
        }
    }

    private static JobOutcome BuildReschedule(DateTime baseTime, DateTime now, CircuitBreakerAttribute? attr, CircuitBreakerOptions options, string groupKey, string reason)
    {
        var jitter = attr?.GetResetJitter(options) ?? options.ResetJitter;
        var jitterMs = (int)jitter.TotalMilliseconds;
        var delayMs = jitterMs > 0 ? Random.Shared.Next(0, jitterMs + 1) : 0;
        var scheduleTime = baseTime.AddMilliseconds(delayMs);

        return new JobOutcome
        {
            State = JobOutcome.RescheduledState(scheduleTime, now),
            ScheduleTime = scheduleTime,

            // All three reschedule causes (open, probe-in-progress, probe-lost) collapse to one reason:
            // they are the same operational fact — the breaker held this job back. The finer detail stays
            // in LogMessage, which is not a metric dimension (§8.33 keeps the reason set bounded).
            Reason = OutcomeReason.CircuitBreaker,
            LogMessage = $"Rescheduled due to circuit breaker '{groupKey}' ({reason})",
        };
    }

    private CircuitBreakerAttribute? GetCircuitBreakerAttribute()
    {
        var handlerType = _jobContext.HandlerType;
        if (handlerType != null)
        {
            var handlerAttr = AttributeCache.GetOrAdd(handlerType, static t => t.GetCustomAttribute<CircuitBreakerAttribute>());
            if (handlerAttr != null)
            {
                return handlerAttr;
            }
        }

        return AttributeCache.GetOrAdd(typeof(TRequest), static t => t.GetCustomAttribute<CircuitBreakerAttribute>());
    }
}
