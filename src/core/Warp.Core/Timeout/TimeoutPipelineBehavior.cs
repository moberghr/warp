using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Enums;
using Warp.Core.Handlers;

namespace Warp.Core.Timeout;

public class TimeoutPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    // Static on a generic type = one flag per closed generic, i.e. per request type — exactly the
    // once-per-type dedupe the warning needs. 0 = not yet warned.
    private static int _warnedTotalWithoutDeadline;

    private readonly IJobContext _jobContext;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<TimeoutOptions> _options;
    private readonly ILogger<TimeoutPipelineBehavior<TRequest, TResponse>> _logger;

    public TimeoutPipelineBehavior(
        IJobContext jobContext,
        TimeProvider timeProvider,
        IOptions<TimeoutOptions> options,
        ILogger<TimeoutPipelineBehavior<TRequest, TResponse>> logger)
    {
        _jobContext = jobContext;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IJob && request is not IMessage)
        {
            return await next(request, cancellationToken);
        }

        // Saga proxies (and any other IPolicyExemptHandler) manage their own execution policy — an outer
        // timeout would race the saga's mutex hold + SaveChanges (see sagas docs, Limitations).
        if (AddonAttributeResolver.IsPolicyExempt(_jobContext.HandlerType))
        {
            return await next(request, cancellationToken);
        }

        var meta = _jobContext.GetMetadata<ITimeoutMetadata>();
        if (meta.TimeoutSeconds == null)
        {
            StampResolvedAttribute(meta);
        }

        var mode = meta.TimeoutMode;
        var scope = meta.TimeoutScope;
        if (meta.TimeoutSeconds is not { } seconds)
        {
            // Last resolver rung: the PerAttempt global default, applied per attempt from live options and
            // never stamped (the Retry precedent — stamping a default would shadow later declarations). A
            // Total-scoped default never reaches here unstamped: it is publish-stamped, and applying it at
            // execution would measure the deadline from first pickup instead of enqueue.
            if (_options.Value.Default is not { } def || _options.Value.DefaultScope != TimeoutScope.PerAttempt)
            {
                return await next(request, cancellationToken);
            }

            seconds = (int)Math.Ceiling(def.TotalSeconds);
            mode ??= _options.Value.DefaultMode;
            scope ??= TimeoutScope.PerAttempt;
        }

        var effectiveScope = scope ?? TimeoutScope.PerAttempt;
        var effectiveMode = mode ?? TimeoutMode.Delete;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        TimeSpan delay;
        if (effectiveScope == TimeoutScope.Total && meta.TimeoutDeadlineUtc is { } deadline)
        {
            var remaining = deadline - now;
            delay = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        else
        {
            delay = TimeSpan.FromSeconds(seconds);
        }

        using var cts = new CancellationTokenSource(delay, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

        try
        {
            return await next(request, linked.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var message = effectiveScope == TimeoutScope.Total
                ? $"Timed out (deadline exceeded, {seconds}s total budget)"
                : $"Timed out after {seconds}s";

            if (effectiveMode == TimeoutMode.Fail)
            {
                throw new TimeoutException(message);
            }

            _jobContext.Outcome = new JobOutcome
            {
                State = State.Deleted,
                Reason = OutcomeReason.Timeout,
                LogMessage = message,
            };

            return default!;
        }
    }

    private void StampResolvedAttribute(ITimeoutMetadata meta)
    {
        var attr = AddonAttributeResolver.Resolve<TimeoutAttribute>(_jobContext.HandlerType, typeof(TRequest));
        if (attr == null)
        {
            return;
        }

        if (attr.Scope == TimeoutScope.Total && meta.TimeoutDeadlineUtc == null)
        {
            // Only reachable for a directly-staged job (a recurring firing) whose CONTRACT declares
            // Scope = Total: a published job had its deadline stamped at publish, and a handler-declared
            // Total is rejected at AddWarp. There is no honest deadline to invent here — computing one now
            // would measure from first pickup instead of enqueue, a different semantic under the same
            // attribute — so the policy is refused loudly-once rather than silently redefined.
            if (Interlocked.Exchange(ref _warnedTotalWithoutDeadline, 1) == 0)
            {
                _logger.LogWarning(
                    "[Timeout(Scope = Total)] on {RequestType} is inert on this execution path: the job was staged "
                    + "directly (e.g. a recurring firing), so no publish-time deadline exists and none can be "
                    + "invented without changing what Total means. Use Scope = PerAttempt for this job type.",
                    typeof(TRequest).Name);
            }

            return;
        }

        meta.TimeoutSeconds = attr.Seconds;
        meta.TimeoutMode ??= attr.Mode;
        meta.TimeoutScope ??= attr.Scope;
    }
}
