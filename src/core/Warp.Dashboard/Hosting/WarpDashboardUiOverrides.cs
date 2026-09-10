namespace Warp.Dashboard;

/// <summary>
/// Explicit host declarations of which addon-driven UI surfaces to show, set through
/// <c>MapWarpDashboard(o =&gt; o.ShowAdapters())</c> and friends. Null means "no declaration — use the
/// default", which is why these are nullable rather than plain bools.
/// </summary>
/// <remarks>
/// Why this exists: every flag on <c>GET /api/addons</c> is otherwise resolved as
/// <c>[FromServices] TMarker?</c>, so it reports THIS process's container. That is the wrong question in
/// a dashboard-only deployment (<c>AddWarp</c> + a provider, no <c>AddWarpServer</c>) — the workers hold
/// the addons, the dashboard holds none, and pages get hidden over data that is sitting in the database.
/// The operator knows what the cluster runs; the container does not. These declarations let them say so.
/// </remarks>
internal sealed class WarpDashboardUiOverrides
{
    public bool? Retry { get; set; }

    public bool? Adapters { get; set; }

    public bool? Endpoints { get; set; }

    public bool? Client { get; set; }

    public bool? Slo { get; set; }
}
