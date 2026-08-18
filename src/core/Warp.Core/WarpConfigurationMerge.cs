using System.Collections;
using System.Reflection;

namespace Warp.Core;

/// <summary>
/// Folds the Core-level settings of one <see cref="WarpConfiguration"/> into another.
/// <para>
/// <c>WarpServerConfiguration</c> inherits <see cref="WarpConfiguration"/>, so a server configuration
/// exposes every Core setting — but the two are registered as separate <c>IOptions</c> singletons, and in
/// the two-builder shape (<c>AddWarp(o =&gt; ...)</c> first, then <c>AddWarpServer(o =&gt; ...)</c>) they
/// resolve to two different objects. The server builder never saw the Core lambda, so server-side readers
/// of an inherited setting (retention caps, ApplicationName, the metrics tiers) silently got defaults while
/// Core readers got the configured value. Merging at registration makes one consistent set.
/// </para>
/// </summary>
internal static class WarpConfigurationMerge
{
    /// <summary>
    /// Copies every Core setting that <paramref name="source"/> configured and <paramref name="target"/>
    /// left at its default. A setting configured on BOTH to different values is a genuine ambiguity — there
    /// is no way to tell which lambda the author meant to win — so it throws rather than picking one.
    /// <para>
    /// "Configured" means "differs from a freshly constructed <see cref="WarpConfiguration"/>". Setting a
    /// value that happens to equal the default is indistinguishable from not setting it, which is harmless:
    /// the copy would be a no-op either way.
    /// </para>
    /// </summary>
    public static void ApplyCoreSettings(WarpConfiguration source, WarpConfiguration target)
    {
        if (ReferenceEquals(source, target))
        {
            return;
        }

        var defaults = new WarpConfiguration();

        foreach (var property in typeof(WarpConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Read-only collections (EntityConfigurators) are contributed through the Core options only —
            // WarpModelCustomizer and WarpServerContext both resolve IOptions<WarpConfiguration> — so there
            // is nothing on the server side that could read a stale copy.
            if (!property.CanRead || !property.CanWrite)
            {
                continue;
            }

            var sourceValue = property.GetValue(source);
            var targetValue = property.GetValue(target);

            if (ValuesMatch(sourceValue, targetValue) || ValuesMatch(sourceValue, property.GetValue(defaults)))
            {
                continue;
            }

            if (!ValuesMatch(targetValue, property.GetValue(defaults)))
            {
                throw new InvalidOperationException(
                    $"Warp configuration conflict: '{property.Name}' is set to '{Describe(sourceValue)}' in the "
                    + $"AddWarp lambda and to '{Describe(targetValue)}' in the AddWarpServer lambda. It is a "
                    + "Core-level setting shared by both, so set it in exactly one of them.");
            }

            property.SetValue(target, sourceValue);
        }
    }

    // Sequence equality for the collection-typed settings (InAppNamespaceDenylist), reference/value
    // equality for the rest. A fresh default list is never reference-equal to another fresh default list,
    // so comparing those by identity would report every host as having "configured" the denylist.
    private static bool ValuesMatch(object? left, object? right)
    {
        if (Equals(left, right))
        {
            return true;
        }

        if (left is IEnumerable leftItems and not string && right is IEnumerable rightItems and not string)
        {
            return leftItems.Cast<object?>().SequenceEqual(rightItems.Cast<object?>());
        }

        return false;
    }

    private static string Describe(object? value)
    {
        if (value is IEnumerable items and not string)
        {
            return string.Join(", ", items.Cast<object?>());
        }

        return value?.ToString() ?? "null";
    }
}
