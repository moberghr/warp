using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Diagnostics;
using Warp.Core.Enums;

namespace Warp.Core;

/// <summary>
/// Lightweight liveness host for a NON-server Warp process (publisher-only / API-only / dashboard-only —
/// an <c>AddWarp</c> process that never calls <c>AddWarpServer</c>). Registered by <c>AddWarp</c> only when
/// <c>WarpConfiguration.ApplicationName</c> is set, and stays inert in server processes (the
/// <see cref="IWarpServerPresence"/> marker is present — those record themselves on their <c>Server</c> row
/// via the <c>Heartbeat</c> server task). On start it inserts one <see cref="ApplicationInstance"/> row +
/// a <see cref="ApplicationInstanceEventType.Registered"/> lifecycle event, refreshes
/// <c>LastHeartbeatAt</c> + CPU/RAM every <c>ApplicationHeartbeatInterval</c>, and deregisters (deletes the
/// row + writes a <see cref="ApplicationInstanceEventType.Stopped"/> event) on graceful shutdown. No
/// provider and no distributed lock — each instance owns its own row by its generated id. Uses
/// <see cref="IServiceScopeFactory"/> (§0.5) and <see cref="TimeProvider"/> (§5.7).
/// </summary>
internal sealed class ApplicationHeartbeatHost<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WarpConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ProcessCpuTracker _cpuTracker;
    private readonly ILogger<ApplicationHeartbeatHost<TContext>> _logger;
    private readonly bool _isServerProcess;

    // Generated once and held for the process lifetime — this instance owns exactly this row.
    private readonly Guid _instanceId = Guid.NewGuid();

    private bool _active;

    // Latches once the row is found swept-while-alive so the warning below is logged at most once.
    private bool _staleSweptWarned;

    public ApplicationHeartbeatHost(
        IServiceScopeFactory scopeFactory,
        IOptions<WarpConfiguration> configuration,
        TimeProvider timeProvider,
        ProcessCpuTracker cpuTracker,
        ILogger<ApplicationHeartbeatHost<TContext>> logger,
        IEnumerable<IWarpServerPresence> serverPresences)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration.Value;
        _timeProvider = timeProvider;
        _cpuTracker = cpuTracker;
        _logger = logger;

        // Any registered IWarpServerPresence ⇒ this is a server process (it writes its own Server row).
        _isServerProcess = serverPresences.Any();
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Inert unless this is an opted-in, non-server process. ApplicationName is re-checked here as
        // defence in depth even though AddWarp only registers the host when it is set.
        if (_configuration.ApplicationName is null || _isServerProcess)
        {
            return;
        }

        _active = true;

        // Register the instance row before returning (mirrors WarpServerRegistration.StartAsync — a
        // failure here surfaces at startup rather than being silently swallowed).
        await RegisterAsync(cancellationToken);

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (!_active)
        {
            return;
        }

        try
        {
            await DeregisterAsync();
        }
        catch (Exception ex)
        {
            // Best-effort graceful deregister — a stale row is swept by ExpirationCleanup once
            // LastHeartbeatAt passes ApplicationInstanceStaleGrace, so a failure here is non-fatal.
            _logger.LogWarning(ex, "Failed to deregister application instance {InstanceId} on shutdown.", _instanceId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_configuration.ApplicationHeartbeatInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await HeartbeatAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The heartbeat must never crash the host — a transient DB error just skips one tick.
                    _logger.LogWarning(ex, "Application heartbeat tick failed for instance {InstanceId}.", _instanceId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down — StopAsync handles deregistration.
        }
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var snapshot = _cpuTracker.Sample(now);

        context.Set<ApplicationInstance>().Add(new ApplicationInstance
        {
            Id = _instanceId,
            ApplicationName = _configuration.ApplicationName!,
            MachineName = System.Environment.MachineName,
            StartedAt = now,
            LastHeartbeatAt = now,
            CpuUsagePercent = snapshot?.CpuPercent,
            MemoryWorkingSetBytes = snapshot?.WorkingSet,
            Version = _configuration.ApplicationVersion,
            Environment = _configuration.ApplicationEnvironment,
        });

        AddLifecycleLog(context, ApplicationInstanceEventType.Registered, now);

        await context.SaveChangesAsync(ct);
    }

    // Internal (not private) so tests can drive one tick of the periodic-refresh body deterministically —
    // the PeriodicTimer loop above calls exactly this. Behaviour is identical whether invoked by the loop
    // or directly.
    internal async Task HeartbeatAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var instance = await context.Set<ApplicationInstance>().FindAsync([_instanceId], ct);
        if (instance is null)
        {
            // Swept while this process was unresponsive. A later tick deliberately does not recreate the
            // row — the process is treated as gone, so there is nothing to refresh. Warn ONCE so operators
            // can see this happened without spamming the log every tick for the rest of the process life.
            if (!_staleSweptWarned)
            {
                _staleSweptWarned = true;
                _logger.LogWarning(
                    "Application instance {InstanceId} was stale-swept (heartbeat lost / process unresponsive past ApplicationInstanceStaleGrace). The row is not recreated, so this process will not reappear on the Applications view until it is restarted.",
                    _instanceId);
            }

            return;
        }

        var snapshot = _cpuTracker.Sample(now);
        instance.LastHeartbeatAt = now;
        instance.CpuUsagePercent = snapshot?.CpuPercent;
        instance.MemoryWorkingSetBytes = snapshot?.WorkingSet;

        await context.SaveChangesAsync(ct);
    }

    private async Task DeregisterAsync()
    {
        // Decouple deregistration from the (already-cancelled) shutdown token but bound it, so an
        // unreachable database cannot hang host shutdown (same pattern as WarpServerRegistration.StopAsync).
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cleanupCts.Token;

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var instance = await context.Set<ApplicationInstance>().FindAsync([_instanceId], ct);
        if (instance is not null)
        {
            context.Set<ApplicationInstance>().Remove(instance);
        }

        AddLifecycleLog(context, ApplicationInstanceEventType.Stopped, now);

        await context.SaveChangesAsync(ct);
    }

    private void AddLifecycleLog(DbContext context, ApplicationInstanceEventType eventType, DateTime now)
    {
        context.Set<ApplicationInstanceLog>().Add(new ApplicationInstanceLog
        {
            InstanceId = _instanceId,
            ApplicationName = _configuration.ApplicationName!,
            Timestamp = now,
            EventType = eventType,
            ExpireAt = now.Add(_configuration.ApplicationInstanceLogRetention),
        });
    }
}
