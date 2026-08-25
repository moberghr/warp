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

    /// <summary>
    /// The shared prologue of every policy behaviour. Policy applies only to a job-backed execution: the
    /// request must be a job/message shape, the scope must carry a job row (an in-memory
    /// <c>IMediator.Send</c> of an <c>IJob</c>-shaped type resolves the scoped context with no
    /// <c>JobId</c> — gating it would turn the caller's result into a silent <c>default!</c>), and the
    /// handler must not manage its own policy (<see cref="IPolicyExemptHandler"/>).
    /// </summary>
    public static bool Bypasses(object request, IJobContext jobContext)
    {
        if (request is not IJob && request is not IMessage)
        {
            return true;
        }

        if (jobContext.JobId == Guid.Empty)
        {
            return true;
        }

        return IsPolicyExempt(jobContext.HandlerType);
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
        // MaxRetries and Delays are independent rungs: WithRetry(5) at publish sets the count only, and
        // the attribute's schedule must still apply beneath it — while an explicit publish value for
        // either field outranks the attribute for that field alone.
        if (meta.MaxRetries != null && meta.RetryDelays != null)
        {
            return;
        }

        if (Resolve<RetryAttribute>(handlerType, requestType) is not { } attr)
        {
            return;
        }

        meta.MaxRetries ??= attr.MaxRetries;

        // Left null when the attribute declares none, so the global schedule stays in play.
        if (meta.RetryDelays == null && attr.Delays is { Length: > 0 })
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
            // WARP002 catches this at build time; both arms below are inert backstops, never throws — a
            // throw here lands in Retry's catch and burns the whole budget on a static misconfiguration.
            if (fromHandler)
            {
                return TimeoutStamp.TotalOnHandler;
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
