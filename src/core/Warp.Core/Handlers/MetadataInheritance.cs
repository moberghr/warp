using Warp.Core.Concurrency;
using Warp.Core.NoRestart;
using Warp.Core.RateLimit;
using Warp.Core.Retry;
using Warp.Core.Timeout;

namespace Warp.Core.Handlers;

internal static class MetadataInheritance
{
    // Addon metadata — retry / timeout / concurrency / rate-limit / no-restart — is operational
    // policy resolved per handler (attribute) or per call (JobParameters), together with live
    // execution state such as RetriedTimes and TimeoutDeadlineUtc. None of it may flow from a
    // parent job to a child it spawns; only trace/correlation and user keys are inheritable.
    // Derived from the IJobMetadata interfaces (property names are the dictionary keys, §8.12) so
    // a newly added addon property is excluded from inheritance automatically.
    public static readonly HashSet<string> NonInheritableKeys = new[]
    {
        typeof(IRetryMetadata),
        typeof(ITimeoutMetadata),
        typeof(IConcurrencyMetadata),
        typeof(IRateLimitMetadata),
        typeof(ICanBeRestartedMetadata),
    }
        .SelectMany(x => x.GetProperties())
        .Select(x => x.Name)
        .ToHashSet(StringComparer.Ordinal);
}
