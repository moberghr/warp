namespace Warp.Dashboard.Endpoints;

// Reported by GET /api/addons so the dashboard can discover all opt-in addons in a single
// 200 round-trip instead of probing each per-addon route and treating the 404 as the signal.
public sealed class WarpAddonsInfo
{
    /// <summary>
    /// The Jobs section's Retrying tab. The only flag here with no DI marker behind it: retrying jobs are
    /// read out of <c>Job.Metadata</c>, which any dashboard can do whether or not <c>AddRetry()</c> ran in
    /// this process. Defaults true; turned off with <c>MapWarpDashboard(o =&gt; o.ShowRetries(false))</c>.
    /// </summary>
    public bool Retry { get; init; }

    public bool Concurrency { get; init; }

    public bool Push { get; init; }

    public bool RateLimits { get; init; }

    public bool Sagas { get; init; }

    public bool Adapters { get; init; }

    public bool Endpoints { get; init; }

    public bool Client { get; init; }

    public bool Webhooks { get; init; }

    // SLO / error-budget (§8.31). True when this process opted in via opt.AddSlo(...); the ISloQueryService /
    // ISloCommandService are always registered by AddWarp, so this marker gates the nav, not the API.
    public bool Slo { get; init; }

    // Multi-app observability (§8.19). The dashboard's Applications page IS the renamed Servers page and is
    // always available; this flag only toggles the app-grouping columns / app filter, and is true when this
    // process opted in by setting WarpConfiguration.ApplicationName.
    public bool Applications { get; init; }

    /// <summary>
    /// Seconds of silence after which an instance stops counting as live — this process's
    /// <see cref="Warp.Core.WarpConfiguration.ApplicationInstanceStaleGrace"/>, the same value
    /// <c>ApplicationQueryService</c> uses to compute <c>InstanceView.IsLive</c>.
    /// </summary>
    /// <remarks>
    /// The one non-boolean here, and it is on this payload because the dashboard already reads it once at
    /// boot. Liveness is a fact that DECAYS: a row fetched as live goes stale while the page sits open, and
    /// a client that cannot see the threshold can only re-ask the server or guess. It guessed — 30s, against
    /// a 2 minute grace — so the same silent process rendered red on one page and green on another. With the
    /// number here the browser re-derives exactly what the server would answer at any later instant.
    /// </remarks>
    public int InstanceStaleAfterSeconds { get; init; }
}
