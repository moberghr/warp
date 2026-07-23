using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Diagnostics;
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
    /// Add a Warp <em>server</em> to the service collection. A server registers itself, runs the
    /// supporting server tasks (heartbeat, stale-server cleanup, expiration cleanup), hosts any
    /// registered <see cref="Core.BackgroundServices.WarpBackgroundService"/> instances, and — unless
    /// you call <see cref="WarpServerConfiguration.DisableWorker"/> — runs the job worker
    /// (fetch/execute loop + job-orchestration tasks). The worker is a component of the server.
    /// <para>
    /// Configure inside the lambda: set fields directly (<c>opt.WorkerCount = 10</c>), opt into a
    /// provider (<c>opt.UsePostgreSql()</c>), register background services
    /// (<c>opt.AddBackgroundService&lt;T&gt;()</c>), and worker-side addons (<c>opt.UseDatabasePush()</c>).
    /// For a service-only server (no job processing) call <c>opt.DisableWorker()</c>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddWarpServer<TContext>(
        this IServiceCollection services,
        Action<WarpServerBuilder<TContext>>? configure = null)
        where TContext : DbContext
    {
        var builder = new WarpServerBuilder<TContext>(services);
        configure?.Invoke(builder);

        // Fail fast on the contradictory "run the worker, but with zero workers" shape. Without
        // this, such a server registers the worker hosts and all job-orchestration tasks (taking
        // distributed locks, routing messages, activating scheduled jobs) yet never fetches a
        // single job — work silently piles up in Enqueued forever. A service-only server must opt
        // out explicitly via DisableWorker(); a zero-worker default group is only valid when an
        // explicit AddWorkerGroup contributes workers (TotalWorkerCount > 0).
        if (builder.RunWorker && builder.TotalWorkerCount == 0)
        {
            throw new InvalidOperationException(
                "AddWarpServer is configured to run the job worker (RunWorker = true) but the total "
                + "worker count across all groups is 0. Set WorkerCount > 0 (or add a worker group "
                + "with workers) to process jobs, or call opt.DisableWorker() for a service-only "
                + "server that runs background services without processing jobs.");
        }

        // The builder IS the configuration. TryAdd: if AddWarp was called separately first, its
        // builder wins for the Core-level IOptions — addons from that lambda are preserved.
        services.TryAddSingleton<IOptions<WarpServerConfiguration>>(Options.Create<WarpServerConfiguration>(builder));
        services.TryAddSingleton<IOptions<WarpConfiguration>>(Options.Create<WarpConfiguration>(builder));

        return AddWarpServerCore<TContext>(services, builder.RunWorker);
    }

    // Registers the server-host infrastructure, and — when runWorker is true — the job worker
    // hosts and job-only server tasks on top.
    private static IServiceCollection AddWarpServerCore<TContext>(
        IServiceCollection services,
        bool runWorker)
        where TContext : DbContext
    {
        // Shared server-host infrastructure (always present): server registration, heartbeat,
        // cleanup, the background-service host, and their dependencies.
        AddServerHostCore<TContext>(services);

        if (!runWorker)
        {
            return services;
        }

        // JobLoggerProvider captures job-handler ILogger output into JobLog — worker-only, so it
        // stays gated here. (Trace-correlation scope tracking is configured server-wide in
        // AddServerHostCore so background-service and server-task logs get it too.)
        services.AddLogging(builder => builder.AddProvider(new JobLoggerProvider()));

        // Job-only server tasks — routing, orchestration, scheduling, recovery, stat aggregation.
        // Deliberately NOT part of AddServerHostCore: a service-only server has no jobs to drive.
        services.AddScoped<IServerTask, StaleJobRecovery<TContext>>();
        services.AddScoped<IServerTask, CounterAggregator<TContext>>();
        services.AddScoped<IServerTask, RecurringJobScheduler<TContext>>();
        services.AddScoped<IServerTask, ScheduledJobActivation<TContext>>();
        services.AddScoped<IServerTask, Orchestrator<TContext>>();
        services.AddScoped<IServerTask, MessageRouter<TContext>>();

        // The job worker hot path. Both no-op when their mode flag is off (UseDispatcher), so both
        // are always registered and self-select at StartAsync. Registered after the shared hosted
        // services; WarpServerRegistration (in AddServerHostCore) still starts first so
        // ServerRegistrationState is populated before either host's StartAsync runs.
        services.AddHostedService<WarpDispatcherHost<TContext>>();
        services.AddHostedService<WarpSingleWorkerHost<TContext>>();

        return services;
    }

    // Server-host infrastructure shared by every Warp server (worker or service-only): everything
    // needed to register the server, run the Heartbeat / ServerCleanup / ExpirationCleanup server
    // tasks, and host WarpBackgroundService instances — but none of the job worker hosts or
    // job-only server tasks.
    private static void AddServerHostCore<TContext>(
        IServiceCollection services)
        where TContext : DbContext
    {
        // Core setup is idempotent (TryAdd-based) so calling it here is safe even if the user
        // also called AddWarp separately for their own addon opt-ins.
        services.AddWarp<TContext>();

        // The Warp server context: a runtime-only mirror of the Warp model used for all autonomous
        // server-internal DB work (worker fetch/complete, server tasks, background-service host), with
        // its own logger so server polling doesn't pollute the user's command logs. The connection is
        // supplied by the provider (UsePostgreSql/UseSqlServer) from TContext's options; the model
        // (names + ExcludeFromMigrations) is built in WarpServerContext.OnModelCreating.
        // Resolved physical names for the server context's model. Default impl reads TContext's model
        // once (the single place TContext is touched); the server context consumes the abstraction.
        services.TryAddSingleton<IWarpServerModelNames>(sp =>
            new WarpServerModelNames<TContext>(sp.GetRequiredService<IServiceScopeFactory>()));

        // Snapshot the non-generic DbContextOptions forwarders present before we register the server
        // context, so we can identify (by reference) the one AddDbContext is about to append and remove
        // exactly that — see the rationale below.
        var forwardersBefore = services
            .Where(x => x.ServiceType == typeof(DbContextOptions))
            .ToList();

        services.AddDbContext<WarpServerContext<TContext>>((sp, options) =>
        {
            var configurator = sp.GetService<IWarpServerContextConfigurator>()
                ?? throw new InvalidOperationException(
                    "AddWarpServer requires a Warp provider — call opt.UsePostgreSql() or "
                    + "opt.UseSqlServer() so the server context can open against the same database "
                    + "as your DbContext.");

            configurator.Configure(options, sp);
            options.AddWarpInterceptors();

            // Keep the autonomous server loops' SQL out of the application's command logs: demote the
            // server context's command-executed event to Debug (app stays at Information). The user's
            // own DbContext is unaffected. Opt back in with opt.EnableServerCommandLogging = true.
            var serverConfig = sp.GetService<IOptions<WarpServerConfiguration>>()?.Value;
            if (serverConfig is null || !serverConfig.EnableServerCommandLogging)
            {
                options.ConfigureWarnings(w => w.Log((RelationalEventId.CommandExecuted, LogLevel.Debug)));
            }
        });

        // AddDbContext also appends a non-generic DbContextOptions forwarder (plain Add, not TryAdd)
        // carrying WarpServerContext<TContext> as its ContextType. That enumeration —
        // GetServices<DbContextOptions>().Select(o => o.ContextType) — is the only vector EF's
        // design-time tooling uses to discover this context (it's internal, open-generic, and outside
        // the user's startup assembly, so the IDesignTimeDbContextFactory and assembly scans miss it).
        // Left in, `dotnet ef` counts the runtime-only server context and demands --context. Warp only
        // ever resolves DbContextOptions<WarpServerContext<TContext>>, so dropping the forwarder we just
        // added leaves runtime untouched; the user's own AddDbContext keeps its separate forwarder
        // (→ TContext), the correct design-time target. No property identifies a forwarder by its target
        // context (the factory closure is opaque), so we remove it by identity: the one descriptor
        // AddDbContext appended that wasn't present before.
        var addedForwarder = services
            .Where(x => x.ServiceType == typeof(DbContextOptions))
            .Except(forwardersBefore)
            .Single();
        services.Remove(addedForwarder);

        // Server-internal components depend on IWarpServerContext (not the concrete generic type),
        // resolving the scoped WarpServerContext<TContext>.
        services.AddScoped<IWarpServerContext>(sp => sp.GetRequiredService<WarpServerContext<TContext>>());

        // Trace-correlation scope tracking applies to every server process (worker or
        // service-only) so background-service and server-task logs carry TraceId/SpanId/ParentId.
        // The job-handler log provider (JobLoggerProvider) is worker-only and added separately.
        services.AddLogging(builder =>
        {
            builder.Configure(options =>
            {
                options.ActivityTrackingOptions |= ActivityTrackingOptions.TraceId
                    | ActivityTrackingOptions.SpanId
                    | ActivityTrackingOptions.ParentId;
            });
        });

        services.AddSingleton<PauseStateHolder>();

        // DispatcherRegistry is registered for every server (not gated on RunWorker): a
        // service-only server with UseDatabasePush() still hosts NotificationListenerTask, which
        // wakes dispatchers through the IDispatcherWake seam. It's a cheap dependency-free
        // singleton; the dispatcher-mode worker is the only other consumer. Exposing the same
        // instance as IDispatcherWake lets the Core listener resolve IEnumerable<IDispatcherWake>
        // (empty in an AddWarp-only process — no dispatchers to wake) without Warp.Core depending
        // on Warp.Worker (§0.5).
        services.AddSingleton<DispatcherRegistry>();
        services.AddSingleton<Warp.Core.Notifications.IDispatcherWake>(sp => sp.GetRequiredService<DispatcherRegistry>());

        // IWarpLockProvider is registered by the provider package (Warp.Provider.PostgreSql /
        // Warp.Provider.SqlServer) via their UsePostgreSql / UseSqlServer builder extensions.
        // If the user never calls one, IWarpLockProvider resolution fails fast the first time
        // a lock is requested.
        // Background-service coordination services. Registered here because the implementations
        // inject IOptions<WarpServerConfiguration> (for ServerId) which lives in Warp.Worker.
        // Interfaces are defined in Warp.Core.BackgroundServices; these TryAddScoped registrations
        // are idempotent — a second registration call skips them.
        services.TryAddScoped<IBackgroundServiceStateService, BackgroundServiceStateService<TContext>>();
        services.TryAddScoped<IBackgroundServiceLeaseCoordinator, BackgroundServiceLeaseCoordinator<TContext>>();
        services.TryAddScoped<IBackgroundServiceLogStore, BackgroundServiceLogStore<TContext>>();

        services.AddSingleton<ServerRegistrationState>();

        // ServerTaskSignals<TContext> is registered in Warp.Core.AddWarp (via TryAddSingleton)
        // so publish-only processes can resolve it. AddWarp is called above, so the registration
        // is in place by the time we get here — no duplicate needed.
        services.AddSingleton<ProcessCpuTracker>();
        services.AddSingleton<HeartbeatLeaseTracker>();

        // Marks this process as a server (§multi-app observability). Its only consumer is the
        // Core-side ApplicationHeartbeatHost, which self-inerts when any IWarpServerPresence is
        // present — a server records itself on its Server row, not a duplicate ApplicationInstance.
        services.AddSingleton<IWarpServerPresence, WarpServerPresence>();

        // Server-infrastructure tasks: heartbeat (renews singleton lease + bumps instance
        // heartbeat), stale-server cleanup (releases dead-server leases/instances), and expiration
        // cleanup (background-service log retention + orphaned-definition GC; its job-cleanup half
        // no-ops with no jobs). Job-only tasks are added by AddWarpServerCore instead.
        services.AddScoped<IServerTask, Heartbeat<TContext>>();
        services.AddScoped<IServerTask, ServerCleanup<TContext>>();
        services.AddScoped<IServerTask, ExpirationCleanup<TContext>>();

        // WarpServerRegistration MUST be registered before ServerTaskHost / BackgroundServiceHost
        // (and, in worker mode, the worker hosts) so its StartAsync populates ServerRegistrationState
        // and creates the Server row before anything else starts.
        services.AddHostedService<WarpServerRegistration<TContext>>();
        services.AddHostedService<ServerTaskHost<TContext>>();

        // BackgroundServiceHost is registered only once per TContext. Multiple registration calls
        // (e.g. via AddBackgroundService inside tests) are safe — the guard prevents duplicate
        // hosted-service registrations.
        if (!services.Any(d =>
                d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(BackgroundServiceHost<TContext>)))
        {
            services.AddHostedService<BackgroundServiceHost<TContext>>();
        }

        // The null observer is the production default — no-op singleton so that
        // BackgroundServiceHost can always resolve IBackgroundServiceStatusObserver
        // regardless of whether AddBackgroundService has been called. Tests replace it with
        // a TestStatusObserver registered AFTER the AddWarp* call (the last AddSingleton wins).
        services.TryAddSingleton<IBackgroundServiceStatusObserver, NullBackgroundServiceStatusObserver>();
    }
}
