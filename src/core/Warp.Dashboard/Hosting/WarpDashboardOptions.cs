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

    /// <summary>
    /// Lays the dashboard nav out — which pages sit on the bar and which groups hold the rest, in the
    /// order you declare them. Call it and the layout is yours; leave it alone and the dashboard renders
    /// its own default nav.
    /// </summary>
    /// <remarks>
    /// Pages a layout leaves out are not hidden: they collect in one trailing overflow group, so
    /// everything stays reachable. Calling this more than once keeps adding to the same layout.
    /// </remarks>
    public WarpDashboardOptions ConfigureMenu(Action<WarpDashboardMenu> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        configure(Menu);

        return this;
    }

    internal WarpDashboardMenu Menu { get; } = new();

    /// <summary>
    /// Shows the Jobs section's <b>Retrying</b> tab — jobs whose last attempt threw and which are waiting
    /// to run again. Shown by default; pass <c>false</c> on a deployment that never retries.
    /// </summary>
    /// <remarks>
    /// Unlike the surfaces below, this one has no addon registration to detect: retrying jobs are found by
    /// reading <c>Job.Metadata</c>, which any dashboard can do whether or not <c>AddRetry()</c> ran in this
    /// process — or in any process, since a spent-attempt count can also arrive through the public metadata
    /// surface. So the default is "show", and this is how a host turns it off.
    /// </remarks>
    public WarpDashboardOptions ShowRetries(bool show = true) => Declare(x => x.Retry = show);

    /// <summary>Shows the <b>Adapters</b> nav item regardless of whether <c>AddAdapters()</c> ran in this process.</summary>
    public WarpDashboardOptions ShowAdapters(bool show = true) => Declare(x => x.Adapters = show);

    /// <summary>Shows the <b>Endpoints</b> nav item regardless of whether <c>AddEndpointObservability()</c> ran here.</summary>
    public WarpDashboardOptions ShowEndpoints(bool show = true) => Declare(x => x.Endpoints = show);

    /// <summary>Shows the <b>Client</b> nav item regardless of whether <c>AddClientObservability()</c> ran here.</summary>
    public WarpDashboardOptions ShowClient(bool show = true) => Declare(x => x.Client = show);

    /// <summary>Shows the <b>SLOs</b> nav item regardless of whether <c>AddSlo()</c> ran in this process.</summary>
    public WarpDashboardOptions ShowSlo(bool show = true) => Declare(x => x.Slo = show);

    /// <summary>
    /// The host's <c>Show*</c> declarations, read by the <c>GET /api/addons</c> endpoint.
    /// </summary>
    /// <remarks>
    /// There is deliberately no <c>ShowSagas</c> / <c>ShowConcurrency</c> / <c>ShowRateLimits</c>. For
    /// those three the service the addons endpoint probes IS the page's query service
    /// (<c>ISagaQueryService</c>, <c>IConcurrencyLimitManager</c>, <c>IRateLimitManager</c>), and only
    /// <c>AddSagas()</c> / <c>AddConcurrency()</c> / <c>AddRateLimit()</c> register it — <c>AddWarp</c>
    /// never does. Forcing the flag on would produce a nav item whose every route answers 404, a worse
    /// outcome than the hidden page. The surfaces above are safe because <c>AddWarp</c> itself registers
    /// their query services, so the page has data to serve wherever the flag is turned on.
    /// </remarks>
    internal WarpDashboardUiOverrides Ui { get; } = new();

    /// <summary>
    /// Applies one <c>Show*</c> declaration and returns this, so the calls chain like
    /// <see cref="ConfigureMenu"/>.
    /// </summary>
    private WarpDashboardOptions Declare(Action<WarpDashboardUiOverrides> configure)
    {
        configure(Ui);

        return this;
    }

    public Func<Stream> IndexStream { get; set; } = () => typeof(WarpDashboardOptions).GetTypeInfo().Assembly.GetManifestResourceStream("Warp.Dashboard.dist.index.html")!;
}
