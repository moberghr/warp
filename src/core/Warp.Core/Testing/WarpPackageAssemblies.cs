using System.Collections.Concurrent;
using System.Reflection;

namespace Warp.Core.Testing;

// Tells Warp's own types from the application's. Every assembly built under src/core carries
// [AssemblyMetadata("Warp.Package", "true")] (src/core/Directory.Build.props), so a new package is covered
// without a list to keep. Name prefixes are not used: an application may well be called "Warp.Something".
internal static class WarpPackageAssemblies
{
    public const string MetadataKey = "Warp.Package";

    private static readonly ConcurrentDictionary<Assembly, bool> _cache = new();

    public static bool IsWarpPackage(Assembly assembly) =>
        _cache.GetOrAdd(assembly, static x => x
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(y => string.Equals(y.Key, MetadataKey, StringComparison.Ordinal)));

    public static bool IsWarpPackage(Type? type) => type is not null && IsWarpPackage(type.Assembly);
}
