using Warp.Core.Enums;
using Warp.Core.Handlers;

namespace Warp.Core.Timeout;

public class TimeoutPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IJobContext _jobContext;
    private readonly TimeProvider _timeProvider;

    public TimeoutPipelineBehavior(IJobContext jobContext, TimeProvider timeProvider)
    {
        _jobContext = jobContext;
        _timeProvider = timeProvider;
    }

    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not IJob)
        {
            return await next(request, cancellationToken);
        }

        var meta = _jobContext.GetMetadata<ITimeoutMetadata>();
        if (meta.TimeoutSeconds is not { } seconds)
        {
            return await next(request, cancellationToken);
        }

        var scope = meta.TimeoutScope ?? TimeoutScope.PerAttempt;
        var mode = meta.TimeoutMode ?? TimeoutMode.Delete;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        TimeSpan delay;
        if (scope == TimeoutScope.Total && meta.TimeoutDeadlineUtc is { } deadline)
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
            var message = scope == TimeoutScope.Total
                ? $"Timed out (deadline exceeded, {seconds}s total budget)"
                : $"Timed out after {seconds}s";

            if (mode == TimeoutMode.Fail)
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
}
