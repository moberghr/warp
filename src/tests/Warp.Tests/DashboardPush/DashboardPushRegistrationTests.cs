using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Events;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Dashboard.Push;
using Warp.Tests.TestData;

namespace Warp.Tests.DashboardPush;

/// <summary>
/// What <c>AddDashboardPush</c> wires: the hub and the broadcaster must share one connection counter,
/// or the broadcaster would skip (or always fetch) the stats snapshot regardless of who is watching.
/// </summary>
[Trait("Category", "NoDb")]
public class DashboardPushRegistrationTests
{
    [TimedFact]
    public async Task AddDashboardPush_HubAndBroadcaster_ShareOneConnectionCounter()
    {
        var stats = new Mock<IDashboardStatsService>();
        stats.Setup(x => x.GetWarpStatus()).ReturnsAsync(new DashboardStatistics());
        var hubContext = new FakeHubContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"push-{Guid.NewGuid():N}"));
        services.AddWarp<TestContext>(o => o.AddDashboardPush(p =>
        {
            p.CoalesceWindow = TimeSpan.Zero;
            p.MinBroadcastInterval = TimeSpan.Zero;
        }));
        services.RemoveAll<IHubContext<WarpDashboardHub>>();
        services.AddSingleton<IHubContext<WarpDashboardHub>>(hubContext);
        services.AddScoped(_ => stats.Object);
        await using var provider = services.BuildServiceProvider();

        // A client connects through a hub the container builds, the way SignalR builds one.
        var hub = ActivatorUtilities.CreateInstance<WarpDashboardHub>(provider);
        hub.Context = new Mock<HubCallerContext>().Object;
        await hub.OnConnectedAsync();

        var broadcaster = provider.GetRequiredService<DashboardBroadcaster<TestContext>>();
        await broadcaster.StartAsync(CancellationToken.None);
        try
        {
            provider.GetRequiredService<ServerTaskSignals<TestContext>>().SignalJobFinalized();
            await hubContext.WaitForBroadcastsAsync(1);

            stats.Verify(x => x.GetWarpStatus(), Times.Once);
        }
        finally
        {
            await broadcaster.StopAsync(CancellationToken.None);
        }
    }

    [TimedTheory]
    [InlineData(-1)]
    [InlineData(61)]
    public void AddDashboardPush_MinBroadcastIntervalOutOfRange_RejectedAtRegistration(int seconds)
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"push-{Guid.NewGuid():N}"));

        Should.Throw<ArgumentOutOfRangeException>(() =>
            services.AddWarp<TestContext>(o => o.AddDashboardPush(p => p.MinBroadcastInterval = TimeSpan.FromSeconds(seconds))));
    }
}
