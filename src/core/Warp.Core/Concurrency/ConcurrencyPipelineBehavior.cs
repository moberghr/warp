using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Logging;

namespace Warp.Core.Concurrency;

public class ConcurrencyPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IJobContext _jobContext;
    private readonly IWarpSemaphoreProvider _semaphoreProvider;
    private readonly ConcurrencyLimitResolver _limitResolver;
    private readonly TimeProvider _timeProvider;

    public ConcurrencyPipelineBehavior(
        IJobContext jobContext,
        IWarpSemaphoreProvider semaphoreProvider,
        ConcurrencyLimitResolver limitResolver,
        TimeProvider timeProvider)
    {
        _jobContext = jobContext;
        _semaphoreProvider = semaphoreProvider;
        _limitResolver = limitResolver;
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

        var meta = _jobContext.GetMetadata<IConcurrencyMetadata>();
        if (meta.ConcurrencyKey == null)
        {
            return await next(request, cancellationToken);
        }

        var adminLimit = await _limitResolver.GetLimit(meta.ConcurrencyKey, cancellationToken);
        var effectiveLimit = adminLimit ?? meta.ConcurrencyLimit ?? 1;

        IAsyncDisposable? handle;
        using (var concurrencySpan = WarpTelemetry.StartConcurrencyActivity())
        {
            concurrencySpan?.SetTag(WarpTelemetryAttributes.WarpConcurrencyKey, meta.ConcurrencyKey);
            concurrencySpan?.SetTag(WarpTelemetryAttributes.WarpConcurrencyLimit, effectiveLimit);

            handle = await _semaphoreProvider.TryAcquireAsync(
                $"warp:concurrency:{meta.ConcurrencyKey}",
                effectiveLimit,
                TimeSpan.Zero,
                cancellationToken);

            concurrencySpan?.SetTag(WarpTelemetryAttributes.WarpConcurrencyAcquired, handle != null);
            if (handle == null)
            {
                concurrencySpan?.AddEvent(new System.Diagnostics.ActivityEvent(WarpTelemetryAttributes.WarpConcurrencyHeldByOtherEvent));
            }
        }

        if (handle == null)
        {
            var mode = meta.ConcurrencyMode ?? ConcurrencyMode.Skip;
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            _jobContext.Outcome = mode == ConcurrencyMode.Wait
                ? BuildRequeueOutcome(meta.ConcurrencyKey, effectiveLimit, now)
                : BuildSkipOutcome(meta.ConcurrencyKey, effectiveLimit);

            return default!;
        }

        try
        {
            return await next(request, cancellationToken);
        }
        finally
        {
            await handle.DisposeAsync();
        }
    }

    private static JobOutcome BuildRequeueOutcome(string key, int effectiveLimit, DateTime now) =>
        new()
        {
            State = State.Enqueued,
            ScheduleTime = now,
            ClearHandlerType = true,
            Reason = OutcomeReason.Concurrency,
            LogMessage = $"Requeued — '{key}' full ({effectiveLimit} slots)",
        };

    private static JobOutcome BuildSkipOutcome(string key, int effectiveLimit) =>
        new()
        {
            State = State.Deleted,
            Reason = OutcomeReason.Concurrency,
            LogMessage = $"Cancelled — '{key}' full ({effectiveLimit} slots)",
        };
}
