using Microsoft.AspNetCore.SignalR;
using Warp.Core.Logging;

namespace Warp.Dashboard.Push;

/// <summary>
/// Broadcast-only SignalR hub for the Warp dashboard. Clients receive typed events
/// (<c>JobFinalized</c>, <c>MessageEnqueued</c>) carrying empty payloads — events are
/// invalidations, not data. Clients refetch from the REST API to get authoritative state.
/// </summary>
/// <remarks>
/// Auth is enforced by whatever endpoint conventions the host applied to <c>MapWarpDashboard</c> — the hub is
/// one of the endpoints they cover, so a <c>RequireAuthorization</c> there gates the SignalR negotiate
/// request and the WebSocket upgrade alike, before either reaches SignalR. The hub itself only manages
/// connection-count telemetry.
/// </remarks>
public sealed class WarpDashboardHub : Hub
{
    /// <summary>
    /// This process's connected-client count, read by the broadcaster to skip work nobody would see.
    /// </summary>
    private readonly DashboardConnectionCounter _connections;

    /// <summary>
    /// Initializes a new instance of the <see cref="WarpDashboardHub"/> class.
    /// </summary>
    public WarpDashboardHub(DashboardConnectionCounter connections)
    {
        _connections = connections;
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        _connections.Connected();
        WarpTelemetry.DashboardConnectionsActive.Add(1);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
        _connections.Disconnected();
        WarpTelemetry.DashboardConnectionsActive.Add(-1);
    }
}
