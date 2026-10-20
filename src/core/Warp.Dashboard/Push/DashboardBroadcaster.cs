using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warp.Core.Events;
using Warp.Core.Logging;
using Warp.Core.Models;
using Warp.Core.Services;

namespace Warp.Dashboard.Push;

/// <summary>
/// Subscribes to <see cref="ServerTaskSignals{TContext}"/> and translates in-process signals
/// into SignalR hub broadcasts. Runs out-of-band of the worker fetch/execute loop (§6.1).
/// </summary>
/// <remarks>
/// <para>
/// Signal handling is latched-flag + coalesce-window: each signal sets a flag and releases
/// a semaphore; the loop wakes, waits up to <see cref="WarpDashboardPushConfiguration.CoalesceWindow"/>
/// to allow burst signals to accumulate, then broadcasts at most one event per kind per cycle.
/// </para>
/// <para>
/// At <c>CoalesceWindow == TimeSpan.Zero</c>, the loop skips the wait — every signal becomes
/// its own broadcast. Used by tests that want deterministic counts.
/// </para>
/// </remarks>
public sealed class DashboardBroadcaster<TContext> : BackgroundService
    where TContext : DbContext
{
    private const string JobFinalizedEvent = "JobFinalized";
    private const string MessageEnqueuedEvent = "MessageEnqueued";

    private readonly ServerTaskSignals<TContext> _signals;
    private readonly IHubContext<WarpDashboardHub> _hub;
    private readonly WarpDashboardPushConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DashboardBroadcaster<TContext>> _logger;

    /// <summary>
    /// This process's connected dashboard clients; null when constructed without one (always fetch stats).
    /// </summary>
    private readonly DashboardConnectionCounter? _connections;

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _wakeGate = new();

    private IDisposable? _jobFinalizedSubscription;
    private IDisposable? _messageEnqueuedSubscription;
    private int _jobFinalizedPending;
    private int _messageEnqueuedPending;
    private bool _disposed;

    /// <summary>
    /// When the last broadcast went out, for <see cref="WarpDashboardPushConfiguration.MinBroadcastInterval"/>.
    /// </summary>
    private DateTimeOffset _lastBroadcastAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardBroadcaster{TContext}"/> class that attaches
    /// a stats snapshot to every broadcast, connected clients or not.
    /// </summary>
    public DashboardBroadcaster(
        ServerTaskSignals<TContext> signals,
        IHubContext<WarpDashboardHub> hub,
        WarpDashboardPushConfiguration configuration,
        TimeProvider time,
        IServiceScopeFactory scopeFactory,
        ILogger<DashboardBroadcaster<TContext>> logger)
        : this(signals, hub, configuration, time, scopeFactory, logger, connections: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardBroadcaster{TContext}"/> class that skips the
    /// stats snapshot while <paramref name="connections"/> reports no connected clients.
    /// </summary>
    public DashboardBroadcaster(
        ServerTaskSignals<TContext> signals,
        IHubContext<WarpDashboardHub> hub,
        WarpDashboardPushConfiguration configuration,
        TimeProvider time,
        IServiceScopeFactory scopeFactory,
        ILogger<DashboardBroadcaster<TContext>> logger,
        DashboardConnectionCounter? connections)
    {
        _signals = signals;
        _hub = hub;
        _configuration = configuration;
        _time = time;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _connections = connections;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Wire subscriptions before ExecuteAsync runs so a signal fired immediately after
        // StartAsync returns can't beat the Subscribe call.
        _jobFinalizedSubscription = _signals.Subscribe(ServerTaskSignal.JobFinalized, OnJobFinalizedSignal);
        _messageEnqueuedSubscription = _signals.Subscribe(ServerTaskSignal.MessageEnqueued, OnMessageEnqueuedSignal);

        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _jobFinalizedSubscription?.Dispose();
        _messageEnqueuedSubscription?.Dispose();
        _jobFinalizedSubscription = null;
        _messageEnqueuedSubscription = null;

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(stoppingToken);

                var window = _configuration.CoalesceWindow;
                if (window > TimeSpan.Zero)
                {
                    await Task.Delay(window, _time, stoppingToken);
                }

                // Hold back until the floor has passed since the last broadcast; signals arriving
                // meanwhile are counted and collapse into this one. Measured as elapsed time, so the
                // first broadcast (no previous one) never waits.
                var floor = _configuration.MinBroadcastInterval;
                if (floor > TimeSpan.Zero && _lastBroadcastAt != DateTimeOffset.MinValue)
                {
                    var wait = floor - (_time.GetUtcNow() - _lastBroadcastAt);
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, _time, stoppingToken);
                    }
                }

                await DrainAndBroadcastAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dashboard broadcaster iteration failed");
            }
        }
    }

    private void OnJobFinalizedSignal()
    {
        Interlocked.Increment(ref _jobFinalizedPending);
        Wake();
    }

    private void OnMessageEnqueuedSignal()
    {
        Interlocked.Increment(ref _messageEnqueuedPending);
        Wake();
    }

    /// <summary>
    /// Releases the wake semaphore. Guarded by the gate so two concurrent signal callbacks
    /// cannot both observe <c>CurrentCount == 0</c> and both call <c>Release</c> (which would
    /// exceed the max count of 1 and throw). Same pattern as <c>ServerTaskLoop.Signal</c>.
    /// </summary>
    private void Wake()
    {
        lock (_wakeGate)
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
    }

    /// <summary>
    /// Sends the pending events, all carrying one stats snapshot taken for this drain.
    /// </summary>
    private async Task DrainAndBroadcastAsync(CancellationToken ct)
    {
        var jobRepeats = RepeatsFor(Interlocked.Exchange(ref _jobFinalizedPending, 0));
        var messageRepeats = RepeatsFor(Interlocked.Exchange(ref _messageEnqueuedPending, 0));
        if (jobRepeats + messageRepeats == 0)
        {
            return;
        }

        // One snapshot per drain, and none while nobody here is connected: the stats are about
        // twenty queries, and an unwatched process ran them on every broadcast. The event still
        // goes out without it, so clients reached some other way (a backplane) fall back to
        // their own /status fetch.
        var payload = _connections is { Count: 0 } ? null : await TryFetchStatsAsync(ct);

        for (var i = 0; i < jobRepeats; i++)
        {
            await BroadcastAsync(JobFinalizedEvent, payload, ct);
        }

        for (var i = 0; i < messageRepeats; i++)
        {
            await BroadcastAsync(MessageEnqueuedEvent, payload, ct);
        }

        _lastBroadcastAt = _time.GetUtcNow();
    }

    private int RepeatsFor(int pending)
    {
        if (pending <= 0)
        {
            return 0;
        }

        return _configuration.CoalesceWindow > TimeSpan.Zero ? 1 : pending;
    }

    /// <summary>
    /// Sends one event to every connected client, with the drain's stats snapshot when there is one.
    /// </summary>
    /// <remarks>
    /// Every connected client receives the same snapshot, which saves each of them a GET /api/status.
    /// The snapshot is best-effort: without one the event still goes out and clients fall through to
    /// their REST refetch path.
    /// </remarks>
    private async Task BroadcastAsync(string method, DashboardStatistics? payload, CancellationToken ct)
    {
        try
        {
            if (payload is null)
            {
                await _hub.Clients.All.SendAsync(method, ct);
            }
            else
            {
                await _hub.Clients.All.SendAsync(method, payload, ct);
            }

            WarpTelemetry.DashboardEventsBroadcast.Add(1);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean shutdown — let the outer loop terminate normally.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast dashboard event {Method}", method);
        }
    }

    private async Task<DashboardStatistics?> TryFetchStatsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var statsService = scope.ServiceProvider.GetService<IDashboardStatsService>();
            if (statsService is null)
            {
                return null;
            }

            return await statsService.GetWarpStatus();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch dashboard stats for push; broadcasting without payload");
            return null;
        }
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wake.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
