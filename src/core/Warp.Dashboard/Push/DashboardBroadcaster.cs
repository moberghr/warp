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

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _wakeGate = new();

    private IDisposable? _jobFinalizedSubscription;
    private IDisposable? _messageEnqueuedSubscription;
    private int _jobFinalizedPending;
    private int _messageEnqueuedPending;
    private bool _disposed;

    public DashboardBroadcaster(
        ServerTaskSignals<TContext> signals,
        IHubContext<WarpDashboardHub> hub,
        WarpDashboardPushConfiguration configuration,
        TimeProvider time,
        IServiceScopeFactory scopeFactory,
        ILogger<DashboardBroadcaster<TContext>> logger)
    {
        _signals = signals;
        _hub = hub;
        _configuration = configuration;
        _time = time;
        _scopeFactory = scopeFactory;
        _logger = logger;
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

    private async Task DrainAndBroadcastAsync(CancellationToken ct)
    {
        var jobRepeats = RepeatsFor(Interlocked.Exchange(ref _jobFinalizedPending, 0));
        for (var i = 0; i < jobRepeats; i++)
        {
            await BroadcastAsync(JobFinalizedEvent, ct);
        }

        var messageRepeats = RepeatsFor(Interlocked.Exchange(ref _messageEnqueuedPending, 0));
        for (var i = 0; i < messageRepeats; i++)
        {
            await BroadcastAsync(MessageEnqueuedEvent, ct);
        }
    }

    private int RepeatsFor(int pending)
    {
        if (pending <= 0)
        {
            return 0;
        }

        return _configuration.CoalesceWindow > TimeSpan.Zero ? 1 : pending;
    }

    private async Task BroadcastAsync(string method, CancellationToken ct)
    {
        // Fetch the current aggregate snapshot once per broadcast — every connected
        // client receives the same DTO, eliminating the per-client GET /api/status
        // refetch that would otherwise multiply with the connected-client count.
        // Stats fetch is best-effort: on failure we still broadcast the event so
        // clients fall through to their REST refetch path.
        var payload = await TryFetchStatsAsync(ct);

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
