using System.Reflection;

namespace Warp.UI;

public class WarpUIOptions
{
    public string RoutePrefix { get; set; } = "/warp";

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

    public Func<Stream> IndexStream { get; set; } = () => typeof(WarpUIOptions).GetTypeInfo().Assembly.GetManifestResourceStream("Warp.UI.dist.index.html")!;
}
