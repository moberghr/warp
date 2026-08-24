using System.Collections.Concurrent;
using System.Reflection;

namespace Warp.Core.Handlers;

/// <summary>
/// Resolves a policy attribute for an executing job: handler type first (from
/// <see cref="IJobContext.HandlerType"/>), request/contract type second. Startup validation rejects the
/// same policy family on both axes, so the rung order here is unobservable — either axis wins because
/// the other is guaranteed empty. Cached per (attribute, declaring type); after the first execution per
/// process there is no reflection on this path.
/// </summary>
internal static class AddonAttributeResolver
{
    private static readonly ConcurrentDictionary<(Type AttributeType, Type DeclaringType), Attribute?> Cache = new();
    private static readonly ConcurrentDictionary<Type, bool> PolicyExemptCache = new();

    /// <summary>
    /// True when the executing handler manages its own execution policy (<see cref="IPolicyExemptHandler"/>)
    /// — the policy behaviours skip such executions entirely, before reading metadata or attributes.
    /// </summary>
    public static bool IsPolicyExempt(Type? handlerType)
    {
        if (handlerType == null)
        {
            return false;
        }

        return PolicyExemptCache.GetOrAdd(handlerType, static t => typeof(IPolicyExemptHandler).IsAssignableFrom(t));
    }

    public static TAttr? Resolve<TAttr>(Type? handlerType, Type requestType)
        where TAttr : Attribute
    {
        if (handlerType != null)
        {
            var handlerAttribute = Get<TAttr>(handlerType);
            if (handlerAttribute != null)
            {
                return handlerAttribute;
            }
        }

        return Get<TAttr>(requestType);
    }

    private static TAttr? Get<TAttr>(Type type)
        where TAttr : Attribute
    {
        return (TAttr?)Cache.GetOrAdd(
            (typeof(TAttr), type),
            static key => key.DeclaringType.GetCustomAttribute(key.AttributeType, inherit: false));
    }
}
