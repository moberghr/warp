namespace Warp.Core.Data.Queries;

/// <summary>
/// Snapshot returned by <see cref="IWarpSqlQueries{TContext}.HeartbeatAsync"/>: the server's
/// <c>paused_at</c> after the UPDATE plus every worker group's pause state for this server.
/// All in one round-trip via CTE+JOIN (PG) or table variable + chained SELECT (SQL Server).
/// </summary>
/// <param name="ServerPausedAt">
/// The server-level pause timestamp post-update. <c>null</c> means not paused.
/// </param>
/// <param name="GroupPaused">
/// Per-worker-group pause state for groups owned by this server. Key is the group's Id,
/// value is <c>true</c> when paused. Empty when the server has no worker groups yet.
/// </param>
/// <param name="RenewedBackgroundServiceLeases">
/// The set of singleton service names whose <c>BackgroundServiceLease</c> row was successfully
/// renewed this beat (i.e. <c>UPDATE background_service_lease ... WHERE holder_server_id = @me</c>
/// returned a row). Empty when the BackgroundServices addon is not registered, when this server
/// holds no leases, or when all held leases have already expired before renewal.
/// <para>
/// The <c>Heartbeat</c> task uses this together with the set of names held on the previous beat
/// to compute which leases were <em>lost</em> (held last beat but not renewed this beat) and
/// publishes a <c>BackgroundServiceLeaseLost</c> signal for each.
/// </para>
/// </param>
public sealed record HeartbeatResult(
    DateTime? ServerPausedAt,
    IReadOnlyDictionary<Guid, bool> GroupPaused,
    IReadOnlyList<string> RenewedBackgroundServiceLeases)
{
    // Backward-compatible constructor: callers that only pass pause data get an empty renewed
    // list. Used when the BackgroundServices addon is not registered on this deployment.
    public HeartbeatResult(DateTime? serverPausedAt, IReadOnlyDictionary<Guid, bool> groupPaused)
        : this(serverPausedAt, groupPaused, [])
    {
    }

    /// <summary>
    /// The database's clock (UTC) when the heartbeat ran: what the heartbeat wrote, and what the server
    /// measures its own clock against (#350). Null from a provider that does not report it.
    /// </summary>
    public DateTime? DatabaseUtcNow { get; init; }
}
