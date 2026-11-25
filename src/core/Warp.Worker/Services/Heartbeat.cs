using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Diagnostics;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Logging;
using Warp.Core.Notifiers;

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
/// <para>
/// Every time it writes is the database's (#350), and it measures this process's clock against the database's
/// on each beat: the offset corrects the <see cref="DatabaseClock"/> every server component reads, and past
/// <c>ClockSkewWarningThreshold</c> it is reported once per excursion (Warning log + notification). While a
/// server from before 8.0 is alive it writes this server's own time instead and the offset is measured but not
/// applied, as 7.7 behaved (rule 2.20).
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
    private readonly ClockSkewState _skew;
    private readonly WarpNotifierDispatcher _notifier;
    private readonly ILogger<Heartbeat<TContext>> _logger;

    public Heartbeat(
        IWarpServerContext serverContext,
        [FromKeyedServices(DatabaseClock.Key)] TimeProvider time,
        PauseStateHolder pauseStateHolder,
        ProcessCpuTracker cpuTracker,
        IOptions<WarpServerConfiguration> configuration,
        IWarpSqlQueries<TContext> sqlQueries,
        ServerTaskSignals<TContext> signals,
        HeartbeatLeaseTracker leaseTracker,
        ClockSkewState skew,
        WarpNotifierDispatcher notifier,
        ILogger<Heartbeat<TContext>> logger)
    {
        _skew = skew;
        _notifier = notifier;
        _logger = logger;
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
        // This process's own clock (not the corrected one): the statement writes the database's time and
        // stores how far this reading is from it.
        var clock = _time as DatabaseClock;
        var local = clock?.Local ?? _time;
        var sent = local.GetUtcNow();
        var now = sent.UtcDateTime;
        var snapshot = _cpuTracker.Sample(now);
        var memoryBytes = snapshot?.WorkingSet;
        var cpuPercent = snapshot?.CpuPercent;

        // While a server from before 8.0 is alive, write this server's own time as 7.7 did (rule 2.20).
        var result = await _sqlQueries.HeartbeatAsync(
            _context,
            _configuration.ServerId,
            now,
            memoryBytes,
            cpuPercent,
            writeLocalTime: clock?.LegacyServers.Alive ?? false,
            ct) ?? throw new InvalidOperationException("Server not found in the database.");

        // Only on the periodic heartbeat: with it disabled (a test driving a fake clock and running the
        // heartbeat by hand), correcting the clock to the database's would undo the test's time.
        if (clock != null && result.DatabaseUtcNow is { } databaseNow && _configuration.HealthCheckInterval != null)
        {
            // The database read its clock somewhere inside the round trip, so a slow one (a wait for a
            // pooled connection, a stalled statement) says little about the offset: keep the last reading.
            var received = local.GetUtcNow();
            if (received - sent <= DatabaseClock.MaxSampleRoundTrip)
            {
                var offset = clock.Record(sent, received, databaseNow);
                await ReportSkewAsync(offset);
            }
        }

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

    private async Task ReportSkewAsync(TimeSpan offset)
    {
        var threshold = _configuration.ClockSkewWarningThreshold;
        var skewed = threshold != null && offset.Duration() > threshold.Value;
        if (!_skew.Enter(skewed))
        {
            return;
        }

        var offsetMs = Math.Round(offset.TotalMilliseconds);
        _logger.LogWarning(
            "This server's clock is {OffsetMs} ms from the database's (threshold {ThresholdMs} ms). Warp corrects for it (not while a server from before 8.0 is alive), but fix the clock (NTP): the correction is only as fresh as the last heartbeat.",
            offsetMs,
            threshold!.Value.TotalMilliseconds);

        // The heartbeat runs outside any transaction, so this is already post-commit (§8.25).
        await _notifier.DispatchAsync(
            new ClockSkewDetectedEvent
            {
                Type = WarpEventType.ClockSkewDetected,
                Severity = WarpEventSeverity.Warning,
                TimestampUtc = _time.GetUtcNow().UtcDateTime,
                MachineName = Environment.MachineName,
                Application = _configuration.ApplicationName,
                Message = $"Server clock on {Environment.MachineName} is {offsetMs} ms from the database clock.",
                ServerId = _configuration.ServerId,
                OffsetMs = offsetMs,
            },
            CancellationToken.None);
    }
}
