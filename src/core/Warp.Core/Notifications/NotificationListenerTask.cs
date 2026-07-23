using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warp.Core.Events;

namespace Warp.Core.Notifications;

/// <summary>
/// Hosted service that consumes <see cref="IWarpNotificationTransport.ListenAsync"/> and republishes
/// each cross-process notification onto the in-process <see cref="ServerTaskSignals{TContext}"/> pipe
/// (waking the dispatcher, bare workers, MessageRouter, Orchestrator, and the dashboard broadcaster).
/// Only registered when the user opts in via <c>opt.UseDatabasePush()</c> (inside the
/// <c>AddWarp</c>/<c>AddWarpServer</c> lambda).
/// <para>
/// Lives in <c>Warp.Core</c> so it runs in <b>any</b> process that opted into a provider + DB push —
/// server or not. In a non-server (<c>AddWarp</c>-only) process the dispatcher-wake portion is inert
/// (<see cref="IDispatcherWake"/> resolves to an empty set — no workers/dispatchers to wake), while the
/// dashboard channels (<see cref="NotificationKind.JobFinalized"/> / <see cref="NotificationKind.MessageEnqueued"/>)
/// still fire, so a non-server dashboard host receives realtime push (§2.9/§2.10). In a server process
/// behavior is identical to before the move.
/// </para>
/// </summary>
public class NotificationListenerTask<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly IWarpNotificationTransport _transport;
    private readonly WarpDatabasePushConfiguration _options;
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly IReadOnlyList<IDispatcherWake> _dispatcherWakes;
    private readonly ILogger<NotificationListenerTask<TContext>> _logger;

    public NotificationListenerTask(
        IWarpNotificationTransport transport,
        WarpDatabasePushConfiguration options,
        ServerTaskSignals<TContext> signals,
        IEnumerable<IDispatcherWake> dispatcherWakes,
        ILogger<NotificationListenerTask<TContext>> logger)
    {
        _transport = transport;
        _options = options;
        _signals = signals;
        _dispatcherWakes = [.. dispatcherWakes];
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_transport is NullNotificationTransport)
        {
            _logger.LogWarning("NotificationListenerTask started but no real transport is registered; listener will idle.");
            return;
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
        WakeDispatchers();
        _signals.SignalJobEnqueued();
        _signals.SignalMessageEnqueued();
        _signals.SignalJobFinalized();
    }

    private void Dispatch(Notification notification)
    {
        switch (notification.Kind)
        {
            case NotificationKind.JobEnqueued:
                // Two consumers: dispatcher-mode WarpDispatcher (via IDispatcherWake) and
                // bare-worker WarpWorker instances (via ServerTaskSignals.JobEnqueued). Firing
                // both is harmless — each consumer's semaphore caps at 1. In an AddWarp-only
                // process both sets are empty/inert (no workers), so this is a no-op there.
                WakeDispatchers();
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

    private void WakeDispatchers()
    {
        for (var i = 0; i < _dispatcherWakes.Count; i++)
        {
            _dispatcherWakes[i].SignalAll();
        }
    }
}
