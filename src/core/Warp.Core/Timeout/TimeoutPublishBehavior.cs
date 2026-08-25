using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Options;
using Warp.Core.Handlers;

namespace Warp.Core.Timeout;

/// <summary>
/// The one publish-side policy behaviour left (§8.8). Everything else resolves at execution so a handler
/// declaration can win; a <see cref="TimeoutScope.Total"/> budget cannot, because its deadline is
/// wall-clock from enqueue and is read pre-execution by the workers and the SLO attainment counter.
/// </summary>
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

            if (attr is { Scope: TimeoutScope.Total })
            {
                meta.TimeoutSeconds = attr.Seconds;
                meta.TimeoutMode ??= attr.Mode;
                meta.TimeoutScope ??= TimeoutScope.Total;
            }
            else if (attr == null && _options.Value is { Default: { } def, DefaultScope: TimeoutScope.Total })
            {
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
