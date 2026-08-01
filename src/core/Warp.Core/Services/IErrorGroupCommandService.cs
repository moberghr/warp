using Warp.Core.Enums;

namespace Warp.Core.Services;

/// <summary>
/// Write side of the error grouping / Issues feature (§8.29) — the operator resolve/ignore action. Registered by
/// <c>AddWarp</c> itself so any dashboard host resolves it. Low-contention admin action: an optimistic
/// load-set-save on <c>TContext</c>, no mutex.
/// </summary>
public interface IErrorGroupCommandService
{
    /// <summary>
    /// Sets a group's operator status (Resolved / Ignored / Unresolved), stamping <c>StatusChangedAt</c> so a
    /// later occurrence counts as a regression. Returns false when no group has the fingerprint.
    /// </summary>
    Task<bool> SetStatus(string fingerprint, ErrorGroupStatus status, CancellationToken ct);
}
