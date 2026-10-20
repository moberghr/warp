using Microsoft.AspNetCore.SignalR;
using Moq;
using Warp.Dashboard.Push;

namespace Warp.Tests.DashboardPush;

/// <summary>
/// Simulates dashboard clients connecting to <see cref="WarpDashboardHub"/>, through the hub's own
/// connect callback, so <see cref="DashboardConnectionCounter"/> moves exactly as a live connection
/// moves it. The broadcaster skips the stats snapshot while no client is connected.
/// </summary>
internal static class FakeDashboardClients
{
    /// <summary>
    /// Connects <paramref name="count"/> clients to a hub backed by <paramref name="connections"/>, and
    /// returns the last one's hub so a test can disconnect it.
    /// </summary>
    public static async Task<WarpDashboardHub> ConnectAsync(DashboardConnectionCounter connections, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var hub = await ConnectOneAsync(connections);
        for (var i = 1; i < count; i++)
        {
            hub = await ConnectOneAsync(connections);
        }

        return hub;
    }

    private static async Task<WarpDashboardHub> ConnectOneAsync(DashboardConnectionCounter connections)
    {
        var hub = new WarpDashboardHub(connections) { Context = new Mock<HubCallerContext>().Object };
        await hub.OnConnectedAsync();

        return hub;
    }
}
