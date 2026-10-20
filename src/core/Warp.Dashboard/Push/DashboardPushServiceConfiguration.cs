using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Warp.Core;

namespace Warp.Dashboard.Push;

/// <summary>
/// Opt-in registration for the realtime dashboard push addon. Same shape as
/// <c>AddRetry()</c> / <c>AddConcurrency()</c> / <c>AddCircuitBreaker()</c>.
/// </summary>
/// <remarks>
/// Multi-server fanout requires <c>opt.UseDatabasePush()</c>. Without it, each server's
/// broadcaster only emits events sourced from its own workers; dashboard clients connected
/// to server A will not see events originating on server B until their 30 s safety-net poll.
/// </remarks>
public static class DashboardPushServiceConfiguration
{
    public static IWarpBuilder<TContext> AddDashboardPush<TContext>(
        this IWarpBuilder<TContext> builder,
        Action<WarpDashboardPushConfiguration>? configure = null)
        where TContext : DbContext
    {
        var configuration = new WarpDashboardPushConfiguration();
        configure?.Invoke(configuration);

        // Fail at startup rather than in the broadcast loop, where a bad value only logs a Warning on
        // every iteration and push silently stops.
        if (configuration.MinBroadcastInterval < TimeSpan.Zero || configuration.MinBroadcastInterval > WarpDashboardPushConfiguration.MaxMinBroadcastInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configure),
                configuration.MinBroadcastInterval,
                $"{nameof(WarpDashboardPushConfiguration.MinBroadcastInterval)} must be between zero and {WarpDashboardPushConfiguration.MaxMinBroadcastInterval}.");
        }

        builder.Services.TryAddSingleton(configuration);
        builder.Services.TryAddSingleton<IDashboardPushMarker, DashboardPushMarker>();

        builder.Services.AddSignalR();
        builder.Services.TryAddSingleton<DashboardConnectionCounter>();
        builder.Services.TryAddSingleton<DashboardBroadcaster<TContext>>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, DashboardBroadcaster<TContext>>(
                sp => sp.GetRequiredService<DashboardBroadcaster<TContext>>()));

        return builder;
    }
}
