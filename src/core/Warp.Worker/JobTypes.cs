using System.Collections.Concurrent;

namespace Warp.Worker;

/// <summary>
/// Resolves a job's stored assembly-qualified type name to its <see cref="Type"/>, once per name per
/// process. <see cref="Type.GetType(string)"/> parses the name and goes through the assembly loader on
/// every call; executing a job resolves its message type (and a routed child its handler type), so it ran
/// for every job, about a tenth of the worker's CPU on a drain profile. A name that does not resolve is not
/// cached: the caller fails the job, and the assembly may still be loaded later.
/// </summary>
internal static class JobTypes
{
    private static readonly ConcurrentDictionary<string, Type> Resolved = new(StringComparer.Ordinal);

    public static Type? Resolve(string assemblyQualifiedName)
    {
        if (Resolved.TryGetValue(assemblyQualifiedName, out var cached))
        {
            return cached;
        }

        var type = Type.GetType(assemblyQualifiedName);
        if (type != null)
        {
            Resolved.TryAdd(assemblyQualifiedName, type);
        }

        return type;
    }
}
