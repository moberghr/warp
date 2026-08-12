using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;

namespace Warp.Worker;

/// <summary>
/// Registers the Server, WorkerGroup, and Worker rows in the database on startup and removes
/// them on graceful shutdown. Populates <see cref="ServerRegistrationState"/> with the generated
/// IDs so the worker host services can find them without re-querying. Also initializes
/// <see cref="PauseStateHolder"/>.
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

    public WarpServerRegistration(
        IOptions<WarpServerConfiguration> configuration,
        IOptions<WarpConfiguration> resolvedConfiguration,
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        PauseStateHolder pauseStateHolder,
        ServerRegistrationState state)
    {
        _configuration = configuration.Value;
        _resolvedConfiguration = resolvedConfiguration.Value;
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
        _pauseStateHolder = pauseStateHolder;
        _state = state;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
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

        var server = new Server
        {
            Id = _configuration.ServerId,
            ServerName = _configuration.ServerName ?? $"{Environment.MachineName}.{_configuration.ServerId}",
            Application = _configuration.ApplicationName,
            Version = _configuration.ApplicationVersion,
            Environment = _configuration.ApplicationEnvironment,
            StartedTime = now,
            LastHeartbeatTime = now,
            ServiceCount = totalWorkerCount,
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

        _state.Set(registrations);

        // Initialize PauseStateHolder from DB before workers start, so they never see a stale
        // "not paused" default on startup.
        var groupPauseStates = registrations.ToDictionary(x => x.GroupEntityId, _ => false);
        _pauseStateHolder.Update(false, groupPauseStates);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
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
}
