using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>
/// Read-only dashboard queries for the Applications view (§8.19 multi-app observability). Unifies the two
/// instance tables — <c>Server</c> rows (server processes) and <c>ApplicationInstance</c> rows (non-server
/// publisher/API/dashboard-only processes) — into one <see cref="InstanceView"/> roster grouped by logical
/// application. Reads on the user's <c>TContext</c> (§2.14 stays-on-<c>TContext</c>), so dashboard-only /
/// publisher-only processes that call <c>AddWarp</c> without <c>AddWarpServer</c> resolve it and serve the
/// <c>/api/applications</c> endpoints. All reads use <c>AsNoTracking()</c> + <c>.Select()</c> projections
/// (§5.3, §6.4); the two tables are read separately and merged in memory (§5.2 — no cross-table SQL union).
/// Liveness (<see cref="InstanceView.IsLive"/>) is computed against <c>TimeProvider</c> "now" (§5.7).
/// </summary>
public interface IApplicationQueryService
{
    /// <summary>
    /// The application roster: every instance (server ∪ non-server) whose <c>Application</c> is set, grouped
    /// by application name into per-app instance counts, live-instance counts, summed live CPU/RAM, and the
    /// distinct version/environment spread. Ordered by application name. Empty when no opted-in process has
    /// registered.
    /// </summary>
    Task<IReadOnlyList<ApplicationSummaryModel>> GetApplications(CancellationToken ct = default);

    /// <summary>
    /// One application's detail: its unified instance list (server ∪ non-server) plus version/environment
    /// spread. Returns null when no instance carries the given application name.
    /// </summary>
    Task<ApplicationDetailModel?> GetApplicationDetail(string application, CancellationToken ct = default);

    /// <summary>
    /// A single instance's detail by (application, instanceId): its unified <see cref="InstanceView"/> plus
    /// its most-recent lifecycle events. Returns null when no instance matches the pair.
    /// </summary>
    Task<ApplicationInstanceDetailModel?> GetInstanceDetail(string application, Guid instanceId, CancellationToken ct = default);
}
