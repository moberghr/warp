using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Options;
using Warp.Core.Handlers;

namespace Warp.Core.Timeout;

public class TimeoutPublishBehavior<T> : IPublishPipelineBehavior<T>
{
    private static readonly ConcurrentDictionary<Type, TimeoutAttribute?> AttributeCache = new();

    private readonly IOptions<TimeoutOptions> _options;
    private readonly TimeProvider _timeProvider;

    public TimeoutPublishBehavior(IOptions<TimeoutOptions> options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public Task PublishAsync(PublishContext<T> context, PublishDelegate next, CancellationToken ct)
    {
        var meta = context.GetMetadata<ITimeoutMetadata>();

        if (meta.TimeoutSeconds == null)
        {
            var attr = AttributeCache.GetOrAdd(typeof(T), static t => t.GetCustomAttribute<TimeoutAttribute>());
            if (attr != null)
            {
                meta.TimeoutSeconds = attr.Seconds;
                meta.TimeoutMode ??= attr.Mode;
                meta.TimeoutScope ??= attr.Scope;
            }
            else if (_options.Value.Default is { } def && _options.Value.DefaultScope == TimeoutScope.Total)
            {
                // Only a Total-scoped default is publish-stamped: its deadline is a wall-clock budget
                // measured from enqueue and must exist before the first execution (the workers read it
                // pre-execution for deadline attainment). A PerAttempt default is applied at execution by
                // TimeoutPipelineBehavior instead — stamping it here filled the metadata slot for every
                // job and made a handler-declared [Timeout] unreachable, the same shadowing Retry fixed
                // as #236. Handler [Timeout] under a Total default is rejected at AddWarp for the same
                // reason, so the slot-always-full behaviour is safe in this arm.
                meta.TimeoutSeconds = (int)Math.Ceiling(def.TotalSeconds);
                meta.TimeoutMode ??= _options.Value.DefaultMode;
                meta.TimeoutScope ??= TimeoutScope.Total;
            }
        }

        if (meta.TimeoutSeconds is { } secs
            && meta.TimeoutScope == TimeoutScope.Total
            && meta.TimeoutDeadlineUtc == null)
        {
            meta.TimeoutDeadlineUtc = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(secs);
        }

        return next();
    }
}
