namespace Warp.Dashboard;

/// <summary>
/// The dashboard's built-in pages, as the host names them when laying out the nav
/// (<see cref="WarpDashboardMenu"/>). Every page the SPA ships with has a member here, so a layout can
/// place any of them and the ones a layout leaves out are still reachable through the overflow group.
/// </summary>
/// <remarks>
/// Members are not routes. The SPA owns its own URLs (and several pages open on a filtered sub-route,
/// e.g. Jobs at <c>/jobs/enqueued</c>); the join between the two sides is the stable id in
/// <see cref="WarpDashboardPageIds"/>.
/// </remarks>
public enum WarpDashboardPage
{
    Dashboard = 1,
    Jobs = 2,
    Messages = 3,
    Batches = 4,
    Recurring = 5,
    Services = 6,
    Adapters = 7,
    Endpoints = 8,
    Client = 9,
    Webhooks = 10,
    Concurrency = 11,
    RateLimits = 12,
    Sagas = 13,
    Issues = 14,
    Slo = 15,
    Counters = 16,
    Applications = 17,
}

/// <summary>
/// Maps a <see cref="WarpDashboardPage"/> to the id the SPA joins on.
/// </summary>
/// <remarks>
/// A switch expression with literal ids rather than <c>ToString().ToLowerInvariant()</c>, for the same
/// reason as <c>OutcomeReasonTokens</c>: the id is a wire format shared with the SPA, so renaming a member
/// must be a compile-time edit here rather than a silent change that stops matching a nav item.
/// </remarks>
internal static class WarpDashboardPageIds
{
    internal static string For(WarpDashboardPage page) => page switch
    {
        WarpDashboardPage.Dashboard => "dashboard",
        WarpDashboardPage.Jobs => "jobs",
        WarpDashboardPage.Messages => "messages",
        WarpDashboardPage.Batches => "batches",
        WarpDashboardPage.Recurring => "recurring",
        WarpDashboardPage.Services => "services",
        WarpDashboardPage.Adapters => "adapters",
        WarpDashboardPage.Endpoints => "endpoints",
        WarpDashboardPage.Client => "client",
        WarpDashboardPage.Webhooks => "webhooks",
        WarpDashboardPage.Concurrency => "concurrency",
        WarpDashboardPage.RateLimits => "ratelimits",
        WarpDashboardPage.Sagas => "sagas",
        WarpDashboardPage.Issues => "issues",
        WarpDashboardPage.Slo => "slo",
        WarpDashboardPage.Counters => "counters",
        WarpDashboardPage.Applications => "applications",
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Unknown dashboard page."),
    };
}
