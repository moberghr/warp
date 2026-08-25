namespace Warp.Dashboard.Extensions;

/// <summary>
/// Describes what a UI extension adds to the Warp dashboard.
/// Returned by GET /api/extensions and consumed by the SPA extension loader.
/// </summary>
public class DashboardExtensionManifest
{
    /// <summary>
    /// Extension name (matches IWarpDashboardExtension.Name).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// URL of the extension's JS module, served from embedded resources.
    /// The SPA dynamically imports this script and calls its install() function.
    /// </summary>
    public required string ScriptUrl { get; init; }

    /// <summary>
    /// Pages this extension adds to the dashboard (new routes + nav items).
    /// </summary>
    public List<DashboardExtensionPage> Pages { get; init; } = [];
}

/// <summary>
/// A page added by an extension. Creates a new route in the SPA and optionally a nav item.
/// </summary>
public class DashboardExtensionPage
{
    /// <summary>
    /// Route path (e.g., "/retry"). Must start with /.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Display label for the nav item.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Lucide icon name for the nav item (e.g., "refresh-cw").
    /// </summary>
    public string? Icon { get; init; }
}
