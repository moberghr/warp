using System.Reflection;

namespace Warp.Dashboard;

public class WarpDashboardOptions
{
    public string RoutePrefix { get; set; } = "/warp";

    /// <summary>
    /// Optional product name replacing the "Warp" wordmark in the dashboard nav and the browser-tab title,
    /// for hosts that surface the dashboard under their own brand. Ignored for the wordmark when
    /// <see cref="LogoUrl"/> is set (the image wins there), but it still names the tab. Null = "Warp".
    /// </summary>
    public string? BrandName { get; set; }

    /// <summary>
    /// Optional instance label (e.g. "Production", "Staging") shown in the dashboard nav and appended to
    /// the browser-tab title, so tabs for different Warp deployments are distinguishable. Null = none.
    /// </summary>
    public string? InstanceName { get; set; }

    /// <summary>
    /// Optional URL for a "back to app" link rendered in the dashboard nav (e.g. the host portal's home).
    /// Null = no link.
    /// </summary>
    public string? PortalUrl { get; set; }

    /// <summary>Label for the <see cref="PortalUrl"/> link. Defaults to "Back to app" when a URL is set.</summary>
    public string? PortalLabel { get; set; }

    /// <summary>Optional logo image URL shown in the dashboard nav header. Null = the default Warp wordmark.</summary>
    public string? LogoUrl { get; set; }

    public Func<Stream> IndexStream { get; set; } = () => typeof(WarpDashboardOptions).GetTypeInfo().Assembly.GetManifestResourceStream("Warp.Dashboard.dist.index.html")!;
}
