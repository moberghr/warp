using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Policies;

namespace Warp.Core.Timeout;

public class TimeoutPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    // Static on a generic type = one flag per request type per warning, which is the dedupe these warnings want.
    private static int _warnedTotalWithoutDeadline;
    private static int _warnedTotalOnHandler;
    private static int _warnedHandlerUnderTotalStamp;

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
        // Job-backed executions only. Saga proxies (and any other IPolicyExemptHandler) manage their own
        // execution policy — an outer timeout would race the saga's mutex hold + SaveChanges.
        if (PolicyResolver.Bypasses(request, _jobContext))
        {
            return await next(request, cancellationToken);
        }

        var meta = _jobContext.GetMetadata<ITimeoutMetadata>();
        switch (PolicyResolver.StampTimeout(meta, _jobContext.HandlerType, typeof(TRequest)))
        {
            case TimeoutStamp.TotalWithoutDeadline:
                const string totalWithoutDeadlineMessage =
                    "[Timeout(Scope = Total)] on {RequestType} is inert on this execution path: the job was staged "
                    + "directly (e.g. a recurring firing), so no publish-time deadline exists and none can be "
                    + "invented without changing what Total means. Use Scope = PerAttempt for this job type.";
                WarnOnce(ref _warnedTotalWithoutDeadline, totalWithoutDeadlineMessage);

                break;

            case TimeoutStamp.TotalOnHandler:
                const string totalOnHandlerMessage =
                    "[Timeout(Scope = Total)] on the handler of {RequestType} is inert: a Total-scoped timeout is a "
                    + "wall-clock budget measured from enqueue and must be stamped at publish, before any handler "
                    + "is known. Declare Total-scoped timeouts on the request/job type; PerAttempt timeouts may "
                    + "stay on the handler.";
                WarnOnce(ref _warnedTotalOnHandler, totalOnHandlerMessage);

                break;

            // The one shape where a handler declaration loses: a Total timeout is stamped at publish, before
            // any handler is known, and nothing can un-shadow it at execution.
            case TimeoutStamp.AlreadyResolved when meta.TimeoutScope == TimeoutScope.Total
                && PolicyResolver.IsDeclaredOnHandler<TimeoutAttribute>(_jobContext.HandlerType):
                const string handlerUnderTotalStampMessage =
                    "[Timeout] on the handler of {RequestType} is inert: a Total-scoped timeout was already "
                    + "stamped at publish and a wall-clock budget cannot be replaced mid-flight. Move the "
                    + "declaration to the contract, or make the Total-scoped default PerAttempt.";
                WarnOnce(ref _warnedHandlerUnderTotalStamp, handlerUnderTotalStampMessage);

                break;
        }

        var mode = meta.TimeoutMode;
        var scope = meta.TimeoutScope;
        if (meta.TimeoutSeconds is not { } seconds)
        {
            // Last rung: the PerAttempt global default, read live and never stamped. A Total default is
            // publish-stamped and never arrives here.
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

    private void WarnOnce(ref int flag, string message)
    {
        if (Interlocked.Exchange(ref flag, 1) == 0)
        {
#pragma warning disable CA2254 // All three call sites pass a constant template with the same single placeholder.
            _logger.LogWarning(message, typeof(TRequest).Name);
#pragma warning restore CA2254
        }
    }
}
