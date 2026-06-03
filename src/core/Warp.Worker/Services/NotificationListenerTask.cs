using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Events;
using Warp.Core.Notifications;

namespace Warp.Worker.Services;

/// <summary>
/// Hosted service that consumes <see cref="IWarpNotificationTransport.ListenAsync"/> and
/// signals the in-process background tasks (dispatcher, MessageRouter, Orchestrator)
/// on each notification. Only registered when the user opts in via
/// <c>opt.UseDatabasePush() (inside the AddWarp/AddWarpWorker lambda)</c>.
/// </summary>
public class NotificationListenerTask<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly IWarpNotificationTransport _transport;
    private readonly WarpDatabasePushConfiguration _options;
    private readonly WarpWorkerConfiguration _workerConfiguration;
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly DispatcherRegistry _dispatcherRegistry;
    private readonly ILogger<NotificationListenerTask<TContext>> _logger;

    public NotificationListenerTask(
        IWarpNotificationTransport transport,
        WarpDatabasePushConfiguration options,
        IOptions<WarpWorkerConfiguration> workerConfiguration,
        ServerTaskSignals<TContext> signals,
        DispatcherRegistry dispatcherRegistry,
        ILogger<NotificationListenerTask<TContext>> logger)
    {
        _transport = transport;
        _options = options;
        _workerConfiguration = workerConfiguration.Value;
        _signals = signals;
        _dispatcherRegistry = dispatcherRegistry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_transport is NullNotificationTransport)
        {
            _logger.LogWarning("NotificationListenerTask started but no real transport is registered; listener will idle.");
            return;
        }

        if (!_workerConfiguration.UseDispatcher)
        {
            _logger.LogWarning(
                "Warp DB push is enabled but UseDispatcher=false; worker fetch will keep polling. " +
                "Enable UseDispatcher on WarpWorkerConfiguration to get the full benefit.");
        }

        var delay = _options.ReconnectInitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Drain on (re)connect — signal every consumer once to catch up on anything
                // that may have been missed while the listener was offline.
                DrainSignals();

                await foreach (var notification in _transport.ListenAsync(stoppingToken))
                {
                    Dispatch(notification);
                }

                // Listener exited without throwing — normal termination (stoppingToken cancelled).
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (BrokerSetupFailedException ex)
            {
                const string msg = "Service Broker setup failed — Warp DB push disabled. Falling back to polling. " +
                    "Grant the broker setup DDL permission or run the setup SQL manually.";
                _logger.LogError(ex, msg);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Notification listener failed; reconnecting in {DelaySeconds}s",
                    delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.ReconnectMaxDelay.Ticks));
        }
    }

    private void DrainSignals()
    {
        _dispatcherRegistry.SignalAll();
        _signals.SignalJobEnqueued();
        _signals.SignalMessageEnqueued();
        _signals.SignalJobFinalized();
    }

    private void Dispatch(Notification notification)
    {
        switch (notification.Kind)
        {
            case NotificationKind.JobEnqueued:
                // Two consumers: dispatcher-mode WarpDispatcher (via DispatcherRegistry) and
                // bare-worker WarpWorker instances (via ServerTaskSignals.JobEnqueued). Firing
                // both is harmless — each consumer's semaphore caps at 1.
                _dispatcherRegistry.SignalAll();
                _signals.SignalJobEnqueued();
                break;
            case NotificationKind.MessageEnqueued:
                _signals.SignalMessageEnqueued();
                break;
            case NotificationKind.JobFinalized:
                _signals.SignalJobFinalized();
                break;
            default:
                break;
        }
    }
}
