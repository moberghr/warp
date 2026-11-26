using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
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
/// <para>
/// <c>LastHeartbeatAt</c> is compared by the servers' <c>ExpirationCleanup</c> on the database's clock (rule
/// 2.20), so with a database provider registered the beat writes the DATABASE's time, in one statement: a
/// process whose own clock lags the database's by more than <c>ApplicationInstanceStaleGrace</c> was swept while
/// alive. While a server from before 8.0 is alive (a <c>Server</c> row without <c>CoordinationVersion</c>) it
/// writes this process's own time instead, as 7.7 did, because such a server sweeps on its own clock; the same
/// statement decides. Without a provider (EF only) it keeps the local time.
/// </para>
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
    private readonly WarpStartupGate _gate;

    // Completed once the instance row is registered: the heartbeat loop waits for it, and StopAsync deregisters
    // only a row that was written. With RequireDatabaseAtStartup off and the database unreachable at startup,
    // registration completes it later, from the background retry.
    private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The instance row is committed: a retry after a later registration step failed does not insert it again.
    private bool _rowWritten;

    // Generated once and held for the process lifetime — this instance owns exactly this row.
    private readonly Guid _instanceId = Guid.NewGuid();

    private bool _active;

    // Latches once the row is found swept-while-alive so the warning below is logged at most once.
    private bool _staleSweptWarned;

    // Whether a database provider is registered (UsePostgreSql / UseSqlServer), read once at registration: the beat
    // then writes the database's time. Without one (EF only) it writes the local time, as before.
    private bool _databaseTime;

    public ApplicationHeartbeatHost(
        IServiceScopeFactory scopeFactory,
        IOptions<WarpConfiguration> configuration,
        TimeProvider timeProvider,
        ProcessCpuTracker cpuTracker,
        ILogger<ApplicationHeartbeatHost<TContext>> logger,
        IEnumerable<IWarpServerPresence> serverPresences,
        WarpStartupGate? startupGate = null)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration.Value;
        _timeProvider = timeProvider;
        _cpuTracker = cpuTracker;
        _logger = logger;

        // Any registered IWarpServerPresence ⇒ this is a server process (it writes its own Server row).
        _isServerProcess = serverPresences.Any();
        _gate = startupGate ?? new WarpStartupGate(configuration, timeProvider, []);
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
        // failure here surfaces at startup rather than being silently swallowed), unless RequireDatabaseAtStartup
        // is off and the database is unreachable: then it is retried in the background.
        await _gate.RunAfterChecksAsync("application instance registration", RegisterAsync, _logger, cancellationToken, ProbeAsync);

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _gate.StopAsync();

        // The row, not the whole registration: a row committed before a later step failed is still removed.
        if (!_active || !_rowWritten)
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
        try
        {
            await _registered.Task.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

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

    private async Task ProbeAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        await WarpStartupGate.ProbeConnectionAsync(scope.ServiceProvider.GetRequiredService<TContext>(), ct);
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var snapshot = _cpuTracker.Sample(now);

        if (!_rowWritten)
        {
            await WriteRowAsync(context, now, snapshot, ct);
            _rowWritten = true;
        }

        // At once, not one interval later: until then the row carries this process's own time, and a sweep in
        // between would remove an instance that lags the database by more than the grace.
        // A relational provider only: the beat is an ExecuteUpdate that reads the database's clock, which a
        // non-relational provider (EF's in-memory one, as tests use) cannot run.
        _databaseTime = context.Database.IsRelational()
            && scope.ServiceProvider.GetService<IWarpSqlQueries<TContext>>() is not null;
        if (_databaseTime)
        {
            await BeatAtDatabaseTimeAsync(context, now, snapshot, ct);
        }

        await RecordDeployAsync(context, now, ct);
        _registered.TrySetResult();
    }

    private async Task WriteRowAsync(DbContext context, DateTime now, ProcessCpuTracker.Snapshot? snapshot, CancellationToken ct)
    {
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
        var snapshot = _cpuTracker.Sample(now);

        if (_databaseTime)
        {
            if (await BeatAtDatabaseTimeAsync(context, now, snapshot, ct) == 0)
            {
                WarnSweptOnce();
            }

            return;
        }

        var instance = await context.Set<ApplicationInstance>().FindAsync([_instanceId], ct);
        if (instance is null)
        {
            WarnSweptOnce();

            return;
        }

        instance.LastHeartbeatAt = now;
        instance.CpuUsagePercent = snapshot?.CpuPercent;
        instance.MemoryWorkingSetBytes = snapshot?.WorkingSet;

        await context.SaveChangesAsync(ct);
    }

    // One statement. DateTime.UtcNow inside the update is the DATABASE's clock (GETUTCDATE() / now()), not this
    // process's: deliberately, see the class summary. While a server from before 8.0 is alive the same statement
    // writes this process's time instead (rule 2.20) — REMOVE that branch in the release after 8.x. Returns the
    // rows updated: zero when the instance was swept.
    private Task<int> BeatAtDatabaseTimeAsync(DbContext context, DateTime now, ProcessCpuTracker.Snapshot? snapshot, CancellationToken ct)
    {
        var cpuPercent = snapshot?.CpuPercent;
        var workingSet = snapshot?.WorkingSet;
        var servers = context.Set<Server>();

        return context.Set<ApplicationInstance>()
            .Where(x => x.Id == _instanceId)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(
                        y => y.LastHeartbeatAt,
                        y => servers.Any(z => z.CoordinationVersion == null) ? now : DateTime.UtcNow)
                    .SetProperty(y => y.CpuUsagePercent, cpuPercent)
                    .SetProperty(y => y.MemoryWorkingSetBytes, workingSet),
                ct);
    }

    // Swept while this process was unresponsive. A later tick deliberately does not recreate the row — the process
    // is treated as gone, so there is nothing to refresh. Warn ONCE so operators can see this happened without
    // spamming the log every tick for the rest of the process life.
    private void WarnSweptOnce()
    {
        if (_staleSweptWarned)
        {
            return;
        }

        _staleSweptWarned = true;
        _logger.LogWarning(
            "Application instance {InstanceId} was stale-swept (heartbeat lost / process unresponsive past ApplicationInstanceStaleGrace). The row is not recreated, so this process will not reappear on the Applications view until it is restarted.",
            _instanceId);
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

    // Deploy history is a diagnostic: a failure to record it (an unmigrated table, a transient error) is logged and
    // must not fail startup after the instance row committed. A later instance of the same version retries it.
    private async Task RecordDeployAsync(DbContext context, DateTime now, CancellationToken ct)
    {
        try
        {
            await ApplicationDeployRecorder.RecordAsync(context, _configuration, _instanceId, System.Environment.MachineName, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to record the deploy of application version {Version} for instance {InstanceId}.", _configuration.ApplicationVersion, _instanceId);
        }
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
