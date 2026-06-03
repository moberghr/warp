namespace Warp.UI.Endpoints;

// Reported by GET /api/addons so the dashboard can discover all opt-in addons in a single
// 200 round-trip instead of probing each per-addon route and treating the 404 as the signal.
public sealed class WarpAddonsInfo
{
    public bool Concurrency { get; init; }

    public bool Push { get; init; }

    public bool RateLimits { get; init; }

    public bool Sagas { get; init; }
}
