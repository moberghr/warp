using Microsoft.EntityFrameworkCore;

namespace Warp.Core.Events;

/// <summary>
/// In-process push → wake plumbing. Producers (workers finalising a job, the notification
/// listener receiving a remote push, scheduled-job activation) call the named
/// <c>SignalXxx</c> methods. Consumers — server-task loops in <c>Warp.Worker</c> and the
/// dashboard broadcaster in <c>Warp.UI</c> — subscribe at host construction and tear down
/// on shutdown via the returned <see cref="IDisposable"/>.
/// </summary>
/// <remarks>
/// Lives in <c>Warp.Core</c> so that any downstream package (Worker, UI, future addons) can
/// subscribe without taking a dependency on Worker. A dedicated method per signal (instead
/// of a keyed registry) matches how Warp's signal surface is actually used: a small,
/// domain-fixed set that changes rarely. Adding a signal is a one-line addition here plus
/// whatever subscriber code needs it — no keys, no stringly typed channels.
/// </remarks>
public sealed class ServerTaskSignals<TContext>
    where TContext : DbContext
{
    private readonly List<Action> _jobFinalizedWakers = [];
    private readonly List<Action> _messageEnqueuedWakers = [];
    private readonly List<Action> _jobEnqueuedWakers = [];
    private readonly List<Action<string>> _backgroundServiceLeaseLostWakers = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Called when a job reaches a terminal state — wakes all subscribers to
    /// <see cref="ServerTaskSignal.JobFinalized"/>.
    /// </summary>
    public void SignalJobFinalized() => Fire(_jobFinalizedWakers);

    /// <summary>
    /// Called when a <c>Kind=Message</c> job is enqueued — wakes all subscribers to
    /// <see cref="ServerTaskSignal.MessageEnqueued"/>.
    /// </summary>
    public void SignalMessageEnqueued() => Fire(_messageEnqueuedWakers);

    /// <summary>
    /// Called when a <c>Kind=Job</c> row is enqueued or activated — wakes all subscribers to
    /// <see cref="ServerTaskSignal.JobEnqueued"/>. Bare workers and dispatchers subscribe so they
    /// bypass exponential-backoff sleep when work appears, both for local enqueues and (when DB
    /// push is enabled) for cross-server pushes drained by <c>NotificationListenerTask</c>.
    /// </summary>
    public void SignalJobEnqueued() => Fire(_jobEnqueuedWakers);

    /// <summary>
    /// Subscribe <paramref name="wake"/> to the given channel. Dispose the returned handle to
    /// unregister — leaking it would leak a closure across the host's lifetime. Disposal is
    /// idempotent and thread-safe.
    /// </summary>
    public IDisposable Subscribe(ServerTaskSignal channel, Action wake) => channel switch
    {
        ServerTaskSignal.JobFinalized => Subscribe(_jobFinalizedWakers, wake),
        ServerTaskSignal.MessageEnqueued => Subscribe(_messageEnqueuedWakers, wake),
        ServerTaskSignal.JobEnqueued => Subscribe(_jobEnqueuedWakers, wake),
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown server-task signal channel."),
    };

    /// <summary>
    /// Called by <c>Heartbeat</c> when a singleton lease held by this server was not renewed
    /// (row deleted, holder cleared, or expired between beats). Wakes all subscribers with the
    /// affected <paramref name="serviceName"/> so per-service CTSs can be cancelled immediately.
    /// </summary>
    public void PublishBackgroundServiceLeaseLost(string serviceName) =>
        FireWithPayload(_backgroundServiceLeaseLostWakers, serviceName);

    /// <summary>
    /// Subscribe <paramref name="onLost"/> to the <c>BackgroundServiceLeaseLost</c> channel.
    /// The subscriber receives the service name whose lease was lost. Dispose the returned
    /// handle to unregister.
    /// </summary>
    public IDisposable SubscribeBackgroundServiceLeaseLost(Action<string> onLost) =>
        SubscribeWithPayload(_backgroundServiceLeaseLostWakers, onLost);

    private Subscription Subscribe(List<Action> list, Action wake)
    {
        lock (_gate)
        {
            list.Add(wake);
        }

        return new Subscription(list, wake, _gate);
    }

    private PayloadSubscription<string> SubscribeWithPayload(List<Action<string>> list, Action<string> wake)
    {
        lock (_gate)
        {
            list.Add(wake);
        }

        return new PayloadSubscription<string>(list, wake, _gate);
    }

    private void Fire(List<Action> list)
    {
        Action[] snapshot;
        lock (_gate)
        {
            snapshot = [.. list];
        }

        foreach (var waker in snapshot)
        {
            waker();
        }
    }

    private void FireWithPayload(List<Action<string>> list, string payload)
    {
        Action<string>[] snapshot;
        lock (_gate)
        {
            snapshot = [.. list];
        }

        foreach (var waker in snapshot)
        {
            waker(payload);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly List<Action> _list;
        private readonly Lock _gate;
        private Action? _wake;

        public Subscription(List<Action> list, Action wake, Lock gate)
        {
            _list = list;
            _gate = gate;
            _wake = wake;
        }

        public void Dispose()
        {
            if (_wake is null)
            {
                return;
            }

            lock (_gate)
            {
                _list.Remove(_wake);
            }

            _wake = null;
        }
    }

    private sealed class PayloadSubscription<T> : IDisposable
    {
        private readonly List<Action<T>> _list;
        private readonly Lock _gate;
        private Action<T>? _wake;

        public PayloadSubscription(List<Action<T>> list, Action<T> wake, Lock gate)
        {
            _list = list;
            _gate = gate;
            _wake = wake;
        }

        public void Dispose()
        {
            if (_wake is null)
            {
                return;
            }

            lock (_gate)
            {
                _list.Remove(_wake);
            }

            _wake = null;
        }
    }
}
