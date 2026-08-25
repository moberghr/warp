using System.Collections.Concurrent;
using System.Reflection;
using Warp.Core.CircuitBreaker;
using Warp.Core.Concurrency;
using Warp.Core.Handlers;
using Warp.Core.RateLimit;
using Warp.Core.Retry;
using Warp.Core.Timeout;

namespace Warp.Core.Policies;

/// <summary>
/// The single place addon policy is resolved (§8.8): metadata already on the row → handler class →
/// contract → global options. The winner is stamped into metadata and never re-resolved; global options
/// are deliberately never stamped. Cached per (attribute, declaring type).
/// </summary>
internal static class PolicyResolver
{
    // Rung-major: the whole family is searched on the handler before the contract, so a handler
    // [Semaphore] beats a contract [Mutex] — they share one metadata slot.
    private static readonly Type[] ConcurrencyFamily = [typeof(MutexAttribute), typeof(SemaphoreAttribute)];

    private static readonly ConcurrentDictionary<(Type AttributeType, Type DeclaringType), Attribute?> Cache = new();
    private static readonly ConcurrentDictionary<Type, bool> PolicyExemptCache = new();

    /// <summary>
    /// True when the executing handler manages its own execution policy (<see cref="IPolicyExemptHandler"/>)
    /// — the policy behaviours skip such executions entirely.
    /// </summary>
    public static bool IsPolicyExempt(Type? handlerType)
    {
        if (handlerType == null)
        {
            return false;
        }

        return PolicyExemptCache.GetOrAdd(handlerType, static t => typeof(IPolicyExemptHandler).IsAssignableFrom(t));
    }

    public static bool IsDeclaredOnHandler<TAttr>(Type? handlerType)
        where TAttr : Attribute =>
        handlerType != null && Get(typeof(TAttr), handlerType) != null;

    public static void StampConcurrency(IConcurrencyMetadata meta, Type? handlerType, Type requestType)
    {
        if (meta.ConcurrencyKey != null)
        {
            return;
        }

        // All three fields or none — the execution gate keys on ConcurrencyKey alone.
        switch (Find(ConcurrencyFamily, handlerType, requestType).Attribute)
        {
            case MutexAttribute mutex:
                meta.ConcurrencyKey = mutex.Key;
                meta.ConcurrencyLimit = 1;
                meta.ConcurrencyMode = mutex.Mode;

                break;

            case SemaphoreAttribute semaphore:
                meta.ConcurrencyKey = semaphore.Key;
                meta.ConcurrencyLimit = semaphore.Limit;
                meta.ConcurrencyMode = semaphore.Mode;

                break;
        }
    }

    public static void StampRateLimit(IRateLimitMetadata meta, Type? handlerType, Type requestType)
    {
        if (meta.RateLimitKey != null)
        {
            return;
        }

        if (Resolve<RateLimitAttribute>(handlerType, requestType) is not { } attr)
        {
            return;
        }

        meta.RateLimitKey = attr.Key;
        meta.RateLimitCount = attr.Count;
        meta.RateLimitWindowSeconds = attr.PerSeconds;
        meta.RateLimitMode = attr.Mode;
        meta.RateLimitStyle = attr.Style;
    }

    public static void StampRetry(IRetryMetadata meta, Type? handlerType, Type requestType)
    {
        if (meta.MaxRetries != null)
        {
            return;
        }

        if (Resolve<RetryAttribute>(handlerType, requestType) is not { } attr)
        {
            return;
        }

        meta.MaxRetries = attr.MaxRetries;

        // Left null when the attribute declares none, so the global schedule stays in play.
        if (attr.Delays is { Length: > 0 })
        {
            meta.RetryDelays = attr.Delays;
        }
    }

    public static TimeoutStamp StampTimeout(ITimeoutMetadata meta, Type? handlerType, Type requestType)
    {
        if (meta.TimeoutSeconds != null)
        {
            return TimeoutStamp.AlreadyResolved;
        }

        var (attribute, fromHandler) = Find([typeof(TimeoutAttribute)], handlerType, requestType);
        if (attribute is not TimeoutAttribute attr)
        {
            return TimeoutStamp.NothingDeclared;
        }

        if (attr.Scope == TimeoutScope.Total)
        {
            // A Total budget is measured from enqueue, so it cannot be resolved once the job is running.
            // WARP002 catches this at build time; this is the backstop for handlers it cannot see.
            if (fromHandler)
            {
                throw new WarpException(
                    $"[Timeout(Scope = Total)] is declared on handler '{handlerType!.Name}', but a Total-scoped "
                    + "timeout is a wall-clock budget measured from enqueue — its deadline must be stamped at "
                    + "publish, before any handler is known. Declare Total-scoped timeouts on the request/job "
                    + "type; PerAttempt timeouts may stay on the handler.");
            }

            if (meta.TimeoutDeadlineUtc == null)
            {
                return TimeoutStamp.TotalWithoutDeadline;
            }
        }

        meta.TimeoutSeconds = attr.Seconds;
        meta.TimeoutMode ??= attr.Mode;
        meta.TimeoutScope ??= attr.Scope;

        return TimeoutStamp.Stamped;
    }

    /// <summary>
    /// Resolved but never stamped: the breaker's threshold and duration describe a shared dependency GROUP
    /// whose live state is a DB row, and two jobs in one group must not disagree about when it opens.
    /// </summary>
    public static CircuitBreakerAttribute? ResolveCircuitBreaker(Type? handlerType, Type requestType) =>
        Resolve<CircuitBreakerAttribute>(handlerType, requestType);

    internal static TAttr? Resolve<TAttr>(Type? handlerType, Type requestType)
        where TAttr : Attribute =>
        (TAttr?)Find([typeof(TAttr)], handlerType, requestType).Attribute;

    private static (Attribute? Attribute, bool FromHandler) Find(Type[] family, Type? handlerType, Type requestType)
    {
        if (handlerType != null && FindOn(family, handlerType) is { } onHandler)
        {
            return (onHandler, true);
        }

        return (FindOn(family, requestType), false);
    }

    private static Attribute? FindOn(Type[] family, Type declaringType)
    {
        foreach (var attributeType in family)
        {
            if (Get(attributeType, declaringType) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static Attribute? Get(Type attributeType, Type declaringType) =>
        Cache.GetOrAdd(
            (attributeType, declaringType),
            static key => key.DeclaringType.GetCustomAttribute(key.AttributeType, inherit: false));
}
