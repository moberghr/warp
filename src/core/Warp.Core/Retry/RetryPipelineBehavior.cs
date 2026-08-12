using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Options;
using Warp.Core.Enums;
using Warp.Core.Handlers;

namespace Warp.Core.Retry;

public class RetryPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IJob
{
    private static readonly ConcurrentDictionary<Type, RetryAttribute?> AttributeCache = new();

    private readonly IJobContext _jobContext;
    private readonly IOptions<RetryOptions> _options;
    private readonly TimeProvider _timeProvider;

    public RetryPipelineBehavior(IJobContext jobContext, IOptions<RetryOptions> options, TimeProvider timeProvider)
    {
        _jobContext = jobContext;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next(request, cancellationToken);
        }
        catch (Exception)
        {
            var meta = _jobContext.GetMetadata<IRetryMetadata>();
            var attr = GetRetryAttribute();
            var maxRetries = meta.MaxRetries ?? attr?.MaxRetries ?? _options.Value.MaxRetries;
            var retriedTimes = meta.RetriedTimes;

            if (retriedTimes < maxRetries)
            {
                var delays = meta.RetryDelays ?? attr?.Delays ?? _options.Value.Delays;
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                DateTime? scheduleTime = null;

                if (delays.Length > 0)
                {
                    var idx = Math.Min(retriedTimes, delays.Length - 1);
                    var baseDelaySeconds = (double)delays[idx];
                    var jitterFactor = Math.Clamp(_options.Value.JitterFactor, 0.0, 1.0);

                    if (jitterFactor > 0)
                    {
                        // Random.Shared.NextDouble() returns [0.0, 1.0) so r ∈ [-1.0, +1.0).
                        // With jitterFactor ∈ [0, 1] (clamped above) and non-negative
                        // baseDelaySeconds, the result stays in [0, 2 * baseDelaySeconds).
                        var r = (Random.Shared.NextDouble() * 2.0) - 1.0;
                        baseDelaySeconds *= 1.0 + (jitterFactor * r);
                    }

                    // Defensive floor: a user-supplied negative delay (or a pathological
                    // RetryOptions constructed directly, bypassing Math.Clamp) would put
                    // ScheduleTime in the past and run the retry immediately.
                    baseDelaySeconds = Math.Max(0.0, baseDelaySeconds);
                    scheduleTime = now + TimeSpan.FromSeconds(baseDelaySeconds);
                }

                meta.RetriedTimes = retriedTimes + 1;

                // Plain `=`, unlike the exhausted branch below — deliberate, and the asymmetry is the point.
                // Retry still has budget, so the attempt is NOT settled and the reschedule is the authority
                // on what happens next; letting a handler-set outcome survive here would let any handler
                // that stamps an Outcome and then throws silently disable its own retry policy.
                //
                // ClearHandlerType = true is safe despite §8.14 (routed IMessage jobs must keep HandlerType
                // on requeue): TRequest is constrained to IJob, IMessage does not implement IJob, so DI's
                // open-generic constraint check skips this behaviour entirely for message-routed jobs. Retry
                // never runs on the path that needs HandlerType preserved. If that constraint is ever
                // widened, this line has to become conditional.
                _jobContext.Outcome = new JobOutcome
                {
                    State = JobOutcome.RescheduledState(scheduleTime ?? now, now),
                    ScheduleTime = scheduleTime,
                    ClearHandlerType = true,
                    Reason = OutcomeReason.Retry,
                };
            }
            else if (maxRetries > 0)
            {
                // Exhaustion used to be signalled by ABSENCE: no outcome was set and the worker's fallback
                // marked the job Failed — the same observable event as a job with no retry policy at all,
                // so "burned through every retry" was indistinguishable from "failed once". Setting the
                // state explicitly makes the distinction countable.
                //
                // Gated on maxRetries > 0: this behaviour runs for EVERY job once AddRetry() is called, and
                // RetryOptions.MaxRetries defaults to 0 — so without the gate, a type that simply carries no
                // [Retry] attribute had its first and only failure labelled "retry exhausted". Exhausted
                // means a budget was granted and spent, not that none was ever granted; a zero-budget
                // failure stays reasonless and lands in the unattributed remainder where it belongs.
                //
                // The ??= is load-bearing, not defensive, but NOT for the reason first written here: Timeout's
                // Delete mode does not reach this catch at all. It swallows the OperationCanceledException,
                // sets the Deleted outcome and returns `default!` (TimeoutPipelineBehavior), so nothing
                // unwinds through Retry. Timeout's Fail mode does throw, but sets no outcome.
                //
                // The genuinely reachable case is a handler (or a user-written inner behaviour) that sets
                // IJobContext.Outcome and then throws. The retry budget is spent, so this branch is only
                // labelling a failure that is already terminal — it has no authority to overrule a decision
                // the code closer to the work already made, and overwriting would turn an intentional
                // Deleted into a Failed. Leaving an existing outcome alone reproduces exactly what the old
                // signal-by-absence behaviour did.
                _jobContext.Outcome ??= new JobOutcome
                {
                    State = State.Failed,
                    Reason = OutcomeReason.RetryExhausted,
                };
            }

            throw;
        }
    }

    private RetryAttribute? GetRetryAttribute()
    {
        var handlerType = _jobContext.HandlerType;
        if (handlerType != null)
        {
            var handlerAttr = AttributeCache.GetOrAdd(handlerType, static t => t.GetCustomAttribute<RetryAttribute>());
            if (handlerAttr != null)
            {
                return handlerAttr;
            }
        }

        return AttributeCache.GetOrAdd(typeof(TRequest), static t => t.GetCustomAttribute<RetryAttribute>());
    }
}
