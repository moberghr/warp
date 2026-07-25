using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Enums;
using Warp.Core.Logging;
using Warp.Core.Notifiers;

namespace Warp.Worker.Services;

/// <summary>
/// Removes Server rows (and their Worker / WorkerGroup children) whose last heartbeat is
/// older than <see cref="WarpServerConfiguration.HealthCheckTimeout"/>. This is the
/// ungraceful-shutdown cleanup path — <see cref="WarpServerRegistration{TContext}.StopAsync"/>
/// handles the graceful case.
/// </summary>
public sealed class ServerCleanup<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly TimeProvider _time;
    private readonly IWarpSqlQueries<TContext> _sqlQueries;
    private readonly WarpServerConfiguration _configuration;
    private readonly WarpNotifierDispatcher _notifier;

    public ServerCleanup(
        IWarpServerContext serverContext,
        TimeProvider time,
        IWarpSqlQueries<TContext> sqlQueries,
        IOptions<WarpServerConfiguration> configuration,
        WarpNotifierDispatcher notifier)
    {
        _context = serverContext.Context;
        _time = time;
        _sqlQueries = sqlQueries;
        _configuration = configuration.Value;
        _notifier = notifier;
    }

    public string Name => "ServerCleanup";

    public string? LockKey => "warp:server-cleanup";

    public TimeSpan? DefaultInterval => _configuration.ServerCleanupInterval;

    public bool RerunImmediately => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var count = await CleanUpServersAsync(ct);

        return count > 0 ? $"Removed {count} stale servers" : null;
    }

    internal async Task<int> CleanUpServersAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var removedCount = 0;

        // FOR NO KEY UPDATE requires a wrapping transaction to keep the row lock alive
        // past the SELECT. ServerTaskLoop's xact-lock provides it on the production hot
        // path, but direct callers (tests, admin) don't get the wrap — open one then.
        var hasOuterTx = _context.Database.CurrentTransaction != null;
        await using var ownedTx = hasOuterTx
            ? null
            : await _context.Database.BeginTransactionAsync(ct);

        // Collected during the loop, dispatched post-commit (§8.25) so the InstanceDown event never fires
        // ahead of a persisted removal a rollback could undo.
        var downEvents = new List<InstanceDownEvent>();

        var servers = await _sqlQueries.LockAllServersAsync(_context, ct);
        foreach (var server in servers)
        {
            if (now - server.LastHeartbeatTime <= _configuration.HealthCheckTimeout)
            {
                continue;
            }

            downEvents.Add(new InstanceDownEvent
            {
                Type = WarpEventType.InstanceDown,
                Severity = WarpEventSeverity.Warning,
                TimestampUtc = now,
                MachineName = Environment.MachineName,
                Application = WarpTelemetry.ApplicationName,
                Message = $"Server {server.ServerName} ({server.Application ?? "unassigned"}) went down (heartbeat lapsed, last seen {server.LastHeartbeatTime:o}).",
                InstanceId = server.Id,
                ApplicationName = server.Application ?? server.ServerName,
                LastSeenAt = server.LastHeartbeatTime,
                IsServer = true,
            });

            var workers = await _context.Set<Warp.Core.Data.Entities.Worker>()
                .Where(x => x.ServerId == server.Id)
                .ToListAsync(ct);
            _context.Set<Warp.Core.Data.Entities.Worker>().RemoveRange(workers);

            // WorkerGroup rows are FK-linked to Server without OnDelete(Cascade) — crash
            // recovery has to clean them up explicitly. WarpServerRegistration.StopAsync
            // handles the graceful-shutdown case; this handles the ungraceful one.
            var workerGroups = await _context.Set<WorkerGroup>()
                .Where(x => x.ServerId == server.Id)
                .ToListAsync(ct);
            _context.Set<WorkerGroup>().RemoveRange(workerGroups);

            // BackgroundServiceInstance and BackgroundServiceLease rows are FK-restricted
            // (no cascade). Remove them here for the ungraceful-shutdown path, following the same
            // explicit-deletion pattern as Worker/WorkerGroup above.
            var instances = await _context.Set<BackgroundServiceInstance>()
                .Where(x => x.ServerId == server.Id)
                .ToListAsync(ct);
            _context.Set<BackgroundServiceInstance>().RemoveRange(instances);

            var leases = await _context.Set<BackgroundServiceLease>()
                .Where(x => x.HolderServerId == server.Id)
                .ToListAsync(ct);
            _context.Set<BackgroundServiceLease>().RemoveRange(leases);

            _context.Set<Server>().Remove(server);

            removedCount++;
        }

        await _context.SaveChangesAsync(ct);
        if (ownedTx != null)
        {
            await ownedTx.CommitAsync(ct);
        }

        // Post-commit: the removals are durable, so surface each as an operational event.
        foreach (var downEvent in downEvents)
        {
            await _notifier.DispatchAsync(downEvent, ct);
        }

        return removedCount;
    }
}
