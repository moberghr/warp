namespace Warp.UI.Endpoints;

// Reported by GET /api/addons so the dashboard can discover all opt-in addons in a single
// 200 round-trip instead of probing each per-addon route and treating the 404 as the signal.
public sealed class WarpAddonsInfo
{
    public bool Concurrency { get; init; }

    public bool Push { get; init; }

    public bool RateLimits { get; init; }

    public bool Sagas { get; init; }

    public bool Adapters { get; init; }

    public bool Endpoints { get; init; }

    public bool Client { get; init; }

    public bool Webhooks { get; init; }

    // Multi-app observability (§8.19). The dashboard's Applications page IS the renamed Servers page and is
    // always available; this flag only toggles the app-grouping columns / app filter, and is true when this
    // process opted in by setting WarpConfiguration.ApplicationName.
    public bool Applications { get; init; }
}
