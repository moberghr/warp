using System.Reflection;

namespace Warp.Core;

/// <summary>
/// The version of the Warp libraries loaded in this process.
/// </summary>
public static class WarpVersion
{
    /// <summary>
    /// The Warp release this process is running (e.g. <c>7.1.0</c>, or <c>7.2.0-rc.1</c> for a prerelease) — the
    /// assembly's informational version with the SourceLink <c>+{commit}</c> suffix removed.
    /// </summary>
    public static string Current { get; } = Resolve(
        typeof(WarpVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(WarpVersion).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);

    /// <summary>
    /// Picks the release number from the assembly's version attributes.
    /// </summary>
    /// <param name="informational">The informational version, which carries the full semver including prerelease tags.</param>
    /// <param name="file">The file version, used when there is no informational version.</param>
    /// <returns>The release number without build metadata, or <c>unknown</c> when neither attribute is present.</returns>
    internal static string Resolve(string? informational, string? file)
    {
        // Never the AssemblyVersion: Directory.Build.targets pins it to major.0.0.0, a number no release ever had.
        var version = informational ?? file ?? "unknown";
        var metadataStart = version.IndexOf('+', StringComparison.Ordinal);

        return metadataStart < 0 ? version : version[..metadataStart];
    }
}
