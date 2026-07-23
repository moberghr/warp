using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Diagnostics;
using Warp.Core.Events;
using Warp.Core.Logging;

namespace Warp.Worker.Services;

/// <summary>
/// Server heartbeat: refreshes <c>LastHeartbeatTime</c>, CPU/memory metrics, and the
/// pause-state snapshot workers consult before each poll. No distributed lock — every
/// server must heartbeat independently.
/// <para>
/// When the BackgroundServices addon is registered, this task also renews
/// <c>BackgroundServiceLease</c> rows held by this server (and refreshes
/// <c>BackgroundServiceInstance.LastHeartbeatAt</c>) in the same SQL round-trip. Any singleton
/// lease held last beat but not renewed this beat is published as a
/// <c>BackgroundServiceLeaseLost</c> signal so the corresponding supervisor can cancel its CTS
/// without waiting for the next acquisition poll.
/// </para>
/// </summary>
public sealed class Heartbeat<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly TimeProvider _time;
    private readonly PauseStateHolder _pauseStateHolder;
    private readonly ProcessCpuTracker _cpuTracker;
    private readonly WarpServerConfiguration _configuration;
    private readonly IWarpSqlQueries<TContext> _sqlQueries;
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly HeartbeatLeaseTracker _leaseTracker;

    public Heartbeat(
        IWarpServerContext serverContext,
        TimeProvider time,
        PauseStateHolder pauseStateHolder,
        ProcessCpuTracker cpuTracker,
        IOptions<WarpServerConfiguration> configuration,
        IWarpSqlQueries<TContext> sqlQueries,
        ServerTaskSignals<TContext> signals,
        HeartbeatLeaseTracker leaseTracker)
    {
        _context = serverContext.Context;
        _time = time;
        _pauseStateHolder = pauseStateHolder;
        _cpuTracker = cpuTracker;
        _configuration = configuration.Value;
        _sqlQueries = sqlQueries;
        _signals = signals;
        _leaseTracker = leaseTracker;
    }

    public string Name => "Heartbeat";

    public string? LockKey => null;

    public TimeSpan? DefaultInterval => _configuration.HealthCheckInterval;

    public bool RerunImmediately => false;

    public bool LogOnSuccess => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        // One round-trip via CTE+JOIN (PG) / table-variable+chained-SELECT (MSSQL) — folds
        // the heartbeat UPDATE, the server paused_at read, AND the worker_group pause-state
        // read into a single query. When the BackgroundServices addon is registered, the same
        // round-trip also renews BackgroundServiceLease rows and bumps instance last_heartbeat_at.
        var now = _time.GetUtcNow().UtcDateTime;
        var snapshot = _cpuTracker.Sample(now);
        var memoryBytes = snapshot?.WorkingSet;
        var cpuPercent = snapshot?.CpuPercent;

        var result = await _sqlQueries.HeartbeatAsync(
            _context,
            _configuration.ServerId,
            now,
            memoryBytes,
            cpuPercent,
            ct) ?? throw new InvalidOperationException("Server not found in the database.");
        _pauseStateHolder.Update(result.ServerPausedAt != null, new Dictionary<Guid, bool>(result.GroupPaused));

        // Detect and publish lost leases: any service renewed last beat but missing this beat.
        // HeartbeatLeaseTracker is a Singleton and survives across Scoped Heartbeat instances,
        // so _previousHeld persists correctly across ticks even though Heartbeat is Scoped.
        var lostLeases = _leaseTracker.SwapAndComputeLost(result.RenewedBackgroundServiceLeases);
        foreach (var lost in lostLeases)
        {
            WarpTelemetry.BackgroundServicesLeaseLost.Add(1, new KeyValuePair<string, object?>("service_name", lost));
            _signals.PublishBackgroundServiceLeaseLost(lost);
        }

        return null;
    }
}
