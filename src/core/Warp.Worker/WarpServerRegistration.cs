using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Enums;

namespace Warp.Worker;

/// <summary>
/// Registers the Server, WorkerGroup, and Worker rows in the database on startup and removes
/// them on graceful shutdown. Populates <see cref="ServerRegistrationState"/> with the generated
/// IDs so the worker host services can find them without re-querying. Also initializes
/// <see cref="PauseStateHolder"/>, then opens the <see cref="WarpStartupGate"/> the server's loops wait on.
/// <para>
/// Runs through the gate: with <see cref="WarpConfiguration.RequireDatabaseAtStartup"/> off and the database
/// unreachable, registration is retried in the background (after any deferred provider check) and the host
/// starts; the loops start when it succeeds.
/// </para>
/// </summary>
public class WarpServerRegistration<TContext> : IHostedService
    where TContext : DbContext
{
    private readonly WarpServerConfiguration _configuration;
    private readonly WarpConfiguration _resolvedConfiguration;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly PauseStateHolder _pauseStateHolder;
    private readonly ServerRegistrationState _state;
    private readonly ILogger<WarpServerRegistration<TContext>> _logger;
    private readonly WarpStartupGate _gate;

    // Set once the Server/WorkerGroup/Worker rows are committed: a retry after a later step failed does not insert
    // them again, and StopAsync deletes only rows that were written.
    private List<ServerRegistrationState.GroupRegistration>? _registrations;

    // Attempts that may have committed rows without this process knowing (a connection lost after COMMIT): the next
    // attempt removes this server's rows before writing them again.
    private bool _rowsMayExist;

    // The 8.0 constructor, kept so code built against it still binds. Without a startup gate the host behaves as
    // if registration had completed in StartAsync (RequireDatabaseAtStartup on).
    public WarpServerRegistration(
        IOptions<WarpServerConfiguration> configuration,
        IOptions<WarpConfiguration> resolvedConfiguration,
        IServiceScopeFactory serviceScopeFactory,
        [FromKeyedServices(DatabaseClock.Key)] TimeProvider timeProvider,
        PauseStateHolder pauseStateHolder,
        ServerRegistrationState state,
        ILogger<WarpServerRegistration<TContext>>? logger = null)
        : this(configuration, resolvedConfiguration, serviceScopeFactory, timeProvider, pauseStateHolder, state, logger, startupGate: null)
    {
    }

    public WarpServerRegistration(
        IOptions<WarpServerConfiguration> configuration,
        IOptions<WarpConfiguration> resolvedConfiguration,
        IServiceScopeFactory serviceScopeFactory,
        [FromKeyedServices(DatabaseClock.Key)] TimeProvider timeProvider,
        PauseStateHolder pauseStateHolder,
        ServerRegistrationState state,
        ILogger<WarpServerRegistration<TContext>>? logger,
        WarpStartupGate? startupGate)
    {
        _configuration = configuration.Value;
        _resolvedConfiguration = resolvedConfiguration.Value;
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
        _pauseStateHolder = pauseStateHolder;
        _state = state;
        _logger = logger ?? NullLogger<WarpServerRegistration<TContext>>.Instance;
        _gate = startupGate ?? new WarpStartupGate(resolvedConfiguration, timeProvider, []);
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _gate.RunAfterChecksAsync("server registration", RegisterAsync, _logger, cancellationToken, ProbeAsync);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // A registration still retrying is stopped first; a server that never reached its write has nothing to remove.
        await _gate.StopAsync();
        if (!_rowsMayExist)
        {
            return;
        }

        if (_registrations is not null)
        {
            await DeregisterAsync();

            return;
        }

        // A write that failed, possibly after it committed (RequireDatabaseAtStartup off): removing what may be there
        // is best effort, and the database may still be down.
        try
        {
            await DeregisterAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Warp could not remove the rows of server {ServerId}, whose registration never completed, on shutdown.", _configuration.ServerId);
        }
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        await WarpStartupGate.ProbeConnectionAsync(scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context, cancellationToken);
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        // A service-only server (RunWorker = false, set by DisableWorker()) creates no worker
        // groups or workers at all, regardless of WorkerCount / AddWorkerGroup. When the worker
        // runs, zero-worker groups are still skipped per-group below.
        // The resolved WarpConfiguration is what the Publisher reads (when AddWarp was called first, ITS
        // builder wins that registration, not this server builder) — so the untouched-Queues substitution
        // must follow the resolved DefaultQueue, the queue untargeted publishes actually land on.
        var workerGroups = _configuration.RunWorker
            ? _configuration.GetEffectiveWorkerGroups(_resolvedConfiguration.DefaultQueue)
            : [];
        var totalWorkerCount = workerGroups.Sum(g => g.WorkerCount);

        using var scope = _serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (_registrations is null)
        {
            if (_rowsMayExist)
            {
                await RemoveRowsAsync(context, cancellationToken);
            }

            _rowsMayExist = true;
            _registrations = await WriteRowsAsync(context, workerGroups, totalWorkerCount, now, cancellationToken);
        }

        await MeasureClockAsync(scope.ServiceProvider, context, cancellationToken);

        await RecordDeployAsync(context, now, cancellationToken);

        _state.Set(_registrations);

        // Initialize PauseStateHolder from DB before workers start, so they never see a stale
        // "not paused" default on startup.
        var groupPauseStates = _registrations.ToDictionary(x => x.GroupEntityId, _ => false);
        _pauseStateHolder.Update(false, groupPauseStates);

        _gate.Open();
    }

    // Only on a retry (RequireDatabaseAtStartup off): a first start has nothing to remove, and pays no query.
    private async Task RemoveRowsAsync(DbContext context, CancellationToken cancellationToken)
    {
        var server = await context.Set<Server>().FindAsync([_configuration.ServerId], cancellationToken);
        if (server is null)
        {
            return;
        }

        context.Set<Warp.Core.Data.Entities.Worker>().RemoveRange(
            await context.Set<Warp.Core.Data.Entities.Worker>()
                .Where(x => x.ServerId == server.Id)
                .ToListAsync(cancellationToken));
        context.Set<Warp.Core.Data.Entities.WorkerGroup>().RemoveRange(
            await context.Set<Warp.Core.Data.Entities.WorkerGroup>()
                .Where(x => x.ServerId == server.Id)
                .ToListAsync(cancellationToken));
        context.Set<Server>().Remove(server);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();
    }

    private string ServerName => _configuration.ServerName ?? $"{Environment.MachineName}.{_configuration.ServerId}";

    private async Task<List<ServerRegistrationState.GroupRegistration>> WriteRowsAsync(
        DbContext context,
        List<WorkerGroupConfiguration> workerGroups,
        int totalWorkerCount,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var server = new Server
        {
            Id = _configuration.ServerId,
            ServerName = ServerName,
            Application = _configuration.ApplicationName,
            Version = _configuration.ApplicationVersion,
            Environment = _configuration.ApplicationEnvironment,
            StartedTime = now,
            LastHeartbeatTime = now,
            ServiceCount = totalWorkerCount,
            CoordinationVersion = ServerCoordination.LeaseVersion,
        };
        await context.Set<Server>().AddAsync(server, cancellationToken);

        // Server processes are their own instance record (the Server row above); when the process opted
        // into multi-application observability, emit a Registered lifecycle event on the unified
        // application/instance log too (InstanceId = the server's Id — a soft ref, no FK). ExpireAt is
        // stamped at write time so ExpirationCleanup's age sweep can drop it (§8.22).
        if (_configuration.ApplicationName is not null)
        {
            await context.Set<ApplicationInstanceLog>().AddAsync(
                new ApplicationInstanceLog
                {
                    InstanceId = server.Id,
                    ApplicationName = _configuration.ApplicationName,
                    Timestamp = now,
                    EventType = ApplicationInstanceEventType.Registered,
                    ExpireAt = now.Add(_configuration.ApplicationInstanceLogRetention),
                },
                cancellationToken);
        }

        var registrations = new List<ServerRegistrationState.GroupRegistration>();
        foreach (var group in workerGroups)
        {
            // Skip zero-worker groups. A worker-mode server can legitimately set its implicit
            // default group to 0 workers and add an explicit AddWorkerGroup with workers (the
            // "no default-queue workers, only my custom group" pattern) — the empty default group
            // should leave no WorkerGroup/Worker row. Service-only servers never reach this loop
            // (the disabled worker produced an empty group list above), and AddWarpServer rejects a
            // worker-enabled server whose total worker count is zero, so a fully-empty config can't
            // get here either.
            if (group.WorkerCount == 0)
            {
                continue;
            }

            var workerGroup = new Warp.Core.Data.Entities.WorkerGroup
            {
                ServerId = _configuration.ServerId,
                WorkerCount = group.WorkerCount,
                Queues = string.Join(",", group.Queues),
                PollingIntervalMs = group.PollingInterval.TotalMilliseconds,
            };
            await context.Set<Warp.Core.Data.Entities.WorkerGroup>().AddAsync(workerGroup, cancellationToken);

            var workerIds = new List<Guid>(group.WorkerCount);
            for (var i = 0; i < group.WorkerCount; i++)
            {
                var workerId = Guid.NewGuid();
                workerIds.Add(workerId);

                await context.Set<Warp.Core.Data.Entities.Worker>().AddAsync(
                    new Warp.Core.Data.Entities.Worker
                    {
                        Id = workerId,
                        ServerId = _configuration.ServerId,
                        StartedTime = now,
                        LastHeartbeatTime = now,
                        WorkerGroupId = workerGroup.Id,
                    },
                    cancellationToken);
            }

            registrations.Add(new ServerRegistrationState.GroupRegistration(group, workerGroup.Id, workerIds));
        }

        await context.SaveChangesAsync(cancellationToken);

        return registrations;
    }

    private async Task DeregisterAsync()
    {
        // Cleanup must happen even if the shutdown token is already cancelled — otherwise stale
        // Server/Worker/WorkerGroup rows are left behind. In production this happens when the
        // host's graceful shutdown budget (default 30s) elapses while pending completions are
        // still draining; ServerCleanup on a surviving server would eventually GC them via
        // heartbeat-staleness, but the leak window is ugly. Use a fresh, time-bounded token so
        // cleanup is decoupled from the upstream cancel but still bounded if the DB is unreachable.
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cleanupCts.Token;

        using var scope = _serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IWarpServerContext>().Context;

        var server = await context.Set<Server>().FindAsync([_configuration.ServerId], ct);
        if (server == null)
        {
            return;
        }

        var workers = await context.Set<Warp.Core.Data.Entities.Worker>()
            .Where(x => x.ServerId == server.Id)
            .ToListAsync(ct);
        context.Set<Warp.Core.Data.Entities.Worker>().RemoveRange(workers);

        // WorkerGroup rows are FK-linked to Server without OnDelete(Cascade) — we must remove
        // them ourselves on graceful shutdown. The crash-recovery path is covered by
        // ServerCleanup, which cleans up WorkerGroup rows for timed-out servers.
        var workerGroups = await context.Set<Warp.Core.Data.Entities.WorkerGroup>()
            .Where(x => x.ServerId == server.Id)
            .ToListAsync(ct);
        context.Set<Warp.Core.Data.Entities.WorkerGroup>().RemoveRange(workerGroups);

        // BackgroundServiceLease and BackgroundServiceInstance rows are FK-restricted
        // (no cascade). Remove them here as a belt-and-suspenders safety net; the primary
        // graceful-shutdown path is BackgroundServiceHost.StopAsync which fires a fire-and-forget
        // delete before waiting on user code. Remove Lease first so its FK to Definition is
        // satisfied before Instance is removed; neither has a FK to Server, so order vs. Server
        // delete is independent — we place them before the Server delete as a safe convention.
        await context.Set<BackgroundServiceLease>()
            .Where(x => x.HolderServerId == server.Id)
            .ExecuteDeleteAsync(ct);

        await context.Set<BackgroundServiceInstance>()
            .Where(x => x.ServerId == server.Id)
            .ExecuteDeleteAsync(ct);

        context.Set<Server>().Remove(server);
        await context.SaveChangesAsync(ct);
    }

    // A server is an application instance (§8.23), so its registration records a deploy when it is the first instance
    // of a new ApplicationVersion — after the Server row committed, in its own SaveChanges, so a duplicate-key race
    // never rolls registration back. Deploy history is a diagnostic: any other failure is logged, never fatal.
    private async Task RecordDeployAsync(DbContext context, DateTime now, CancellationToken ct)
    {
        try
        {
            await ApplicationDeployRecorder.RecordAsync(context, _configuration, _configuration.ServerId, ServerName, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to record the deploy of application version {Version} for server {ServerId}.", _configuration.ApplicationVersion, _configuration.ServerId);
        }
    }

    // Before any task or worker runs, whether a server from before 8.0 is alive (rule 2.20): it writes and compares
    // its own clock, so while one is, this server does too. The question the cluster-task claim asks (rule 6.23),
    // on the database's clock; the claims keep it current from then on. Then one heartbeat as soon as the row
    // exists (#350): it stamps the time on the row and measures this process's clock against the database's, so
    // every server component reads the database's clock from the first task, not only after the first periodic
    // beat. The heartbeat is skipped with the periodic heartbeat disabled, like the correction itself (a test
    // driving a fake clock).
    private async Task MeasureClockAsync(IServiceProvider services, DbContext context, CancellationToken cancellationToken)
    {
        if (_timeProvider is not DatabaseClock clock)
        {
            return;
        }

        clock.LegacyServers.Report(await LegacyServersAliveAsync(context, cancellationToken));
        if (_configuration.HealthCheckInterval == null)
        {
            return;
        }

        var queries = services.GetRequiredService<IWarpSqlQueries<TContext>>();
        var sent = clock.Local.GetUtcNow();
        var result = await queries.HeartbeatAsync(context, _configuration.ServerId, sent.UtcDateTime, null, null, clock.LegacyServers.Alive, cancellationToken);
        if (result?.DatabaseUtcNow is { } databaseNow)
        {
            clock.Record(sent, clock.Local.GetUtcNow(), databaseNow);
        }
    }

    // A row without CoordinationVersion is a server from before 8.0, alive for as long as the row exists: its
    // heartbeat is on its own clock, so it is not compared with anything here (the claim asks the same way).
    // REMOVE in the release after 8.x, with LegacyServerPresence.
    private static async Task<bool> LegacyServersAliveAsync(DbContext context, CancellationToken cancellationToken)
    {
        return await context.Set<Server>()
            .Where(x => x.CoordinationVersion == null)
            .AnyAsync(cancellationToken);
    }
}
