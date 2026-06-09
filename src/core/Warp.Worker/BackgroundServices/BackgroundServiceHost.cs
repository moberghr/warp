using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Events;

namespace Warp.Worker.BackgroundServices;

/// <summary>
/// <see cref="BackgroundService"/> that drives every registered <see cref="WarpBackgroundService"/>.
/// Mirrors <c>ServerTaskHost</c> — discovers services from DI at construction time, creates one
/// <see cref="BackgroundServiceSupervisor{TContext}"/> per service, and runs them all via
/// <c>Task.WhenAll</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Worker hot path untouched (§0.2 / §6.1).</strong> This host is a parallel hosted
/// service. It does NOT touch <c>WarpWorkerService</c>, <c>WarpDispatcher</c>, or any
/// <c>IServerTask</c> execution path.
/// </para>
/// </remarks>
public sealed class BackgroundServiceHost<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly List<BackgroundServiceSupervisor<TContext>> _supervisors = [];
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BackgroundServiceHost<TContext>> _logger;

    // Guards against duplicate provider registration when the host is recreated in tests.
    // ILoggerFactory.AddProvider is cumulative — calling it twice for the same category
    // results in duplicate log rows in the BackgroundServiceLog table.
    private readonly HashSet<string> _registeredLogCategories = [];

    public BackgroundServiceHost(
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        IOptions<WarpConfiguration> warpConfig,
        IOptions<WarpServerConfiguration> workerConfig,
        ServerTaskSignals<TContext> signals,
        IBackgroundServiceStatusObserver statusObserver,
        ILogger<BackgroundServiceHost<TContext>> logger)
    {
        _scopes = scopes;
        _logger = logger;

        var leaseTtl = workerConfig.Value.BackgroundServiceLeaseTtl ?? TimeSpan.FromSeconds(30);
        var acquirePollInterval = workerConfig.Value.BackgroundServiceAcquirePollInterval ?? TimeSpan.FromSeconds(15);
        var logFlushInterval = workerConfig.Value.LogFlushInterval;

        using var metadataScope = scopes.CreateScope();
        var services = metadataScope.ServiceProvider.GetServices<WarpBackgroundService>();

        foreach (var service in services)
        {
            var serviceType = service.GetType();
            var categoryName = serviceType.FullName ?? serviceType.Name;

            var collector = new BackgroundServiceLogCollector(
                service.Name,
                workerConfig.Value.ServerId,
                service.MinLogLevel,
                scopes,
                time,
                loggerFactory.CreateLogger($"Warp.Worker.BackgroundServices.Collector[{service.Name}]"));

            // Add the per-service log provider to the global ILoggerFactory so that when the
            // user service's DI-resolved ILogger<T> logs, those entries flow into the collector.
            // The provider's CreateLogger(category) returns NullLogger for everything that doesn't
            // exactly match this service's category name, bounding the pollution.
            // Guard against duplicate registration: ILoggerFactory.AddProvider is cumulative and
            // does not deduplicate, so re-creating the host (e.g. in tests) would accumulate
            // providers for the same category and produce duplicate log rows.
            var loggerProvider = new BackgroundServiceLoggerProvider(categoryName, service.MinLogLevel, collector);
            if (_registeredLogCategories.Add(categoryName))
            {
                loggerFactory.AddProvider(loggerProvider);
            }

            IBackgroundServiceStrategy strategy = service.Scope switch
            {
                ServiceScope.PerServer => new PerServerServiceStrategy(),
                ServiceScope.Singleton => new SingletonServiceStrategy<TContext>(
                    service.Name,
                    scopes,
                    leaseTtl,
                    signals),
                _ => new PerServerServiceStrategy(),
            };

            var supervisorLogger = loggerFactory.CreateLogger(
                $"Warp.Worker.BackgroundServices.Supervisor[{service.Name}]");

            var lifecycleLogger = new BackgroundServiceLifecycleLogger(collector);

            var supervisor = new BackgroundServiceSupervisor<TContext>(
                service,
                strategy,
                scopes,
                time,
                acquirePollInterval,
                logFlushInterval,
                supervisorLogger,
                collector,
                lifecycleLogger,
                statusObserver);

            _supervisors.Add(supervisor);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_supervisors.Count == 0)
        {
            return;
        }

        var tasks = _supervisors
            .Select(s => s.RunAsync(stoppingToken));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — all supervisors observed the cancellation.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_supervisors.Count > 0)
        {
            // Fire-and-forget: DELETE BackgroundServiceLease and BackgroundServiceInstance rows
            // for @me immediately, before waiting on user code. This is the graceful-shutdown
            // failover lever — a hung ExecuteAsync must not strand the lease.
            // Use a short-budget CTS independent of both the host and the passed CT.
            _ = DeleteLeaseAndInstanceRowsAsync();
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task DeleteLeaseAndInstanceRowsAsync()
    {
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var configScope = _scopes.CreateScope();
        var serverId = configScope.ServiceProvider.GetRequiredService<IOptions<WarpServerConfiguration>>().Value.ServerId;

        try
        {
            using var scope = _scopes.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<TContext>();

            await ctx.Set<Warp.Core.Data.Entities.BackgroundServiceLease>()
                .Where(x => x.HolderServerId == serverId)
                .ExecuteDeleteAsync(cleanupCts.Token);

            await ctx.Set<Warp.Core.Data.Entities.BackgroundServiceInstance>()
                .Where(x => x.ServerId == serverId)
                .ExecuteDeleteAsync(cleanupCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort — TTL covers it on hard kill.
            _logger.LogWarning(
                ex,
                "BackgroundServiceHost: best-effort cleanup of lease/instance rows for server {ServerId} failed",
                serverId);
        }
    }
}
