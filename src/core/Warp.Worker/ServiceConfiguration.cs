using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Events;
using Warp.Core.Logging;
using Warp.Worker.BackgroundServices;
using Warp.Worker.Services;

namespace Warp.Worker;

/// <summary>
/// Provides methods to configure service for Warp worker.
///
/// based on https://learn.microsoft.com/en-us/dotnet/core/extensions/options-library-authors
/// </summary>
public static class ServiceConfiguration
{
    /// <summary>
    /// Add Warp worker service configuration to the service collection. Call the builder's
    /// config fields directly (<c>opt.WorkerCount = 10</c>), opt into provider (
    /// <c>opt.UsePostgreSql()</c> — provider-package extension), and worker-side addons (
    /// <c>opt.UseDatabasePush()</c>) inside the lambda.
    /// </summary>
    public static IServiceCollection AddWarpWorker<TContext>(
        this IServiceCollection services,
        Action<WarpWorkerBuilder<TContext>>? configure = null)
        where TContext : DbContext
    {
        var builder = new WarpWorkerBuilder<TContext>(services);
        configure?.Invoke(builder);

        // Register the builder as both the worker-level and Core-level options so one set of
        // values drives everything. WarpWorkerConfiguration inherits from WarpConfiguration,
        // so this is safe. TryAdd: if AddWarp was called separately first, its builder wins
        // for the Core-level IOptions — user's addons from that lambda (Mutex entity config,
        // etc.) are preserved.
        services.TryAddSingleton<IOptions<WarpWorkerConfiguration>>(Options.Create<WarpWorkerConfiguration>(builder));
        services.TryAddSingleton<IOptions<WarpConfiguration>>(Options.Create<WarpConfiguration>(builder));

        return AddWarpWorkerInner<TContext>(services);
    }

    private static IServiceCollection AddWarpWorkerInner<TContext>(
        this IServiceCollection services)
        where TContext : DbContext
    {
        // Core setup is idempotent (TryAdd-based) so calling it here is safe even if the user
        // also called AddWarp separately for their own addon opt-ins.
        services.AddWarp<TContext>();

        services.AddSingleton<PauseStateHolder>();
        services.AddSingleton<DispatcherRegistry>();

        services.AddLogging(builder =>
        {
            builder.AddProvider(new JobLoggerProvider());
            builder.Configure(options =>
            {
                options.ActivityTrackingOptions |= ActivityTrackingOptions.TraceId
                    | ActivityTrackingOptions.SpanId
                    | ActivityTrackingOptions.ParentId;
            });
        });

        // IWarpLockProvider is registered by the provider package (Warp.Provider.PostgreSql /
        // Warp.Provider.SqlServer) via their UsePostgreSql / UseSqlServer builder extensions.
        // If the user never calls one, IWarpLockProvider resolution fails fast the first time
        // a lock is requested.
        // Background-service coordination services. Registered here because the implementations
        // inject IOptions<WarpWorkerConfiguration> (for ServerId) which lives in Warp.Worker.
        // Interfaces are defined in Warp.Core.BackgroundServices; these TryAddScoped registrations
        // are idempotent — a second AddWarpWorker<TContext> call skips them.
        services.TryAddScoped<IBackgroundServiceStateService, BackgroundServiceStateService<TContext>>();
        services.TryAddScoped<IBackgroundServiceLeaseCoordinator, BackgroundServiceLeaseCoordinator<TContext>>();
        services.TryAddScoped<IBackgroundServiceLogStore, BackgroundServiceLogStore<TContext>>();

        services.AddSingleton<ServerRegistrationState>();

        // ServerTaskSignals<TContext> is registered in Warp.Core.AddWarp (via TryAddSingleton)
        // so publish-only processes can resolve it. AddWarp is called by AddWarpWorker above,
        // so the registration is in place by the time we get here — no duplicate needed.
        services.AddSingleton<ProcessCpuTracker>();
        services.AddSingleton<HeartbeatLeaseTracker>();
        services.AddScoped<IServerTask, Heartbeat<TContext>>();
        services.AddScoped<IServerTask, ServerCleanup<TContext>>();
        services.AddScoped<IServerTask, StaleJobRecovery<TContext>>();
        services.AddScoped<IServerTask, CounterAggregator<TContext>>();
        services.AddScoped<IServerTask, ExpirationCleanup<TContext>>();
        services.AddScoped<IServerTask, RecurringJobScheduler<TContext>>();
        services.AddScoped<IServerTask, ScheduledJobActivation<TContext>>();
        services.AddScoped<IServerTask, Orchestrator<TContext>>();
        services.AddScoped<IServerTask, MessageRouter<TContext>>();
        services.AddHostedService<WarpServerRegistration<TContext>>();
        services.AddHostedService<WarpDispatcherHost<TContext>>();
        services.AddHostedService<WarpSingleWorkerHost<TContext>>();
        services.AddHostedService<ServerTaskHost<TContext>>();

        // BackgroundServiceHost is registered only once per TContext. Multiple
        // AddWarpWorker<TContext> calls (e.g. via AddBackgroundService inside tests) are
        // safe — the guard prevents duplicate hosted-service registrations.
        if (!services.Any(d =>
                d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(BackgroundServiceHost<TContext>)))
        {
            services.AddHostedService<BackgroundServiceHost<TContext>>();
        }

        // The null observer is the production default — no-op singleton so that
        // BackgroundServiceHost can always resolve IBackgroundServiceStatusObserver
        // regardless of whether AddBackgroundService has been called. Tests replace it with
        // a TestStatusObserver registered AFTER AddWarpWorker (the last AddSingleton wins).
        services.TryAddSingleton<IBackgroundServiceStatusObserver, NullBackgroundServiceStatusObserver>();

        return services;
    }
}
