namespace Warp.Core.Data.Entities;

/// <summary>
/// One row per adapter <c>Name</c> across the cluster — the cluster-wide identity of an outbound
/// dependency (§8.6 disjoint-namespace principle). Created on first registration/use and lazily
/// refreshed. Carries the persisted shared policy (rate limit today, breaker later) so the cluster
/// coordinates on a single config — persisted <b>first-writer-wins</b>: the first registration writes
/// it, and a later mismatching process enforces the persisted value and sets <see cref="HasPolicyConflict"/>
/// rather than overwriting it (change it via a <c>RateLimitOverride</c> admin row or by clearing the
/// persisted policy, not by redeploying). No server reference:
/// adapters run in non-server processes, so a single "last server" value would be misleading.
/// Orphaned rows (renamed/removed adapters with no live use) are removed by <c>ExpirationCleanup</c>
/// once <c>LastSeenAt</c> exceeds <c>WarpConfiguration.AdapterDefinitionOrphanGrace</c>.
/// </summary>
public class AdapterDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    /// <summary>Non-secret display string summarising the adapter's local config for the dashboard.</summary>
    public string? ConfigSummary { get; set; }

    /// <summary>
    /// Effective per-adapter call-log <b>count</b> cap, persisted from the registered
    /// <c>WarpAdapterOptions.CallLogRetentionCount</c> so <c>ExpirationCleanup</c> (which runs on a server
    /// that may not have registered this adapter) can enforce it without the in-memory registry. Null falls
    /// back to <c>WarpConfiguration.AdapterCallLogRetentionCount</c>.
    /// </summary>
    public int? CallLogRetentionCount { get; set; }

    /// <summary>
    /// Dashboard display label for the group dimension (e.g. "Endpoint", "Shop"), persisted from the
    /// registered <c>WarpAdapterOptions.GroupLabel</c> so dashboard-only processes (which never touch the
    /// in-memory registry) can render it. Null falls back to "Group".
    /// </summary>
    public string? GroupLabel { get; set; }

    /// <summary>Persisted shared-policy config (rate limit) as JSON; null when no shared policy is set.</summary>
    public string? SharedPolicyJson { get; set; }

    /// <summary>Stable hash of <see cref="SharedPolicyJson"/> for cheap conflict comparison during lease acquisition.</summary>
    public string? SharedPolicyHash { get; set; }

    /// <summary>
    /// Set when a live process reports a shared policy differing from the persisted one; cleared on a
    /// matching re-registration. Drives the dashboard conflict badge.
    /// </summary>
    public bool HasPolicyConflict { get; set; }
}
