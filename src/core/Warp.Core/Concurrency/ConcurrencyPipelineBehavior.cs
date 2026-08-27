using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Logging;
using Warp.Core.Policies;

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
        // Job-backed executions only. Saga proxies (and any other IPolicyExemptHandler) manage their own
        // serialization — the per-correlation saga mutex — and must not contend on an outer key too.
        if (PolicyResolver.Bypasses(request, _jobContext))
        {
            return await next(request, cancellationToken);
        }

        var meta = _jobContext.GetMetadata<IConcurrencyMetadata>();
        PolicyResolver.StampConcurrency(meta, _jobContext.HandlerType, typeof(TRequest));

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
                ? BuildRequeueOutcome(meta.ConcurrencyKey, effectiveLimit, now, clearHandlerType: request is not IMessage)
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

    private static JobOutcome BuildRequeueOutcome(string key, int effectiveLimit, DateTime now, bool clearHandlerType) =>
        new()
        {
            State = State.Enqueued,
            ScheduleTime = now,

            // Routed message children must keep HandlerType (§8.14): it IS the routing decision, and
            // re-discovery would look up IJobHandler<T> for a type that only has IMessageHandler<T>
            // registrations. Direct jobs clear it so the next attempt re-discovers.
            ClearHandlerType = clearHandlerType,
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

/// <summary>
/// DI shim: carries the <c>IJob</c> constraint so an in-memory <c>Send</c> never resolves
/// <see cref="ConcurrencyLimitResolver"/> and its scoped DbContext. See the retry shims.
/// </summary>
internal sealed class ConcurrencyJobPipelineBehavior<TRequest, TResponse> : ConcurrencyPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IJob
{
    public ConcurrencyJobPipelineBehavior(
        IJobContext jobContext,
        IWarpSemaphoreProvider semaphoreProvider,
        ConcurrencyLimitResolver limitResolver,
        TimeProvider timeProvider)
        : base(jobContext, semaphoreProvider, limitResolver, timeProvider)
    {
    }
}

/// <summary>DI shim: the <c>IMessage</c> half of the constraint split — see <see cref="ConcurrencyJobPipelineBehavior{TRequest, TResponse}"/>.</summary>
internal sealed class ConcurrencyMessagePipelineBehavior<TRequest, TResponse> : ConcurrencyPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IMessage
{
    public ConcurrencyMessagePipelineBehavior(
        IJobContext jobContext,
        IWarpSemaphoreProvider semaphoreProvider,
        ConcurrencyLimitResolver limitResolver,
        TimeProvider timeProvider)
        : base(jobContext, semaphoreProvider, limitResolver, timeProvider)
    {
    }
}
