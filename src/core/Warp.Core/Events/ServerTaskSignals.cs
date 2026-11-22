using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Warp.Core.Events;

/// <summary>
/// In-process push → wake plumbing. Producers (workers finalising a job, the notification
/// listener receiving a remote push, scheduled-job activation) call the named
/// <c>SignalXxx</c> methods. Consumers — server-task loops in <c>Warp.Worker</c> and the
/// dashboard broadcaster in <c>Warp.Dashboard</c> — subscribe at host construction and tear down
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
    private const int MaxFinalizedParents = 10_000;

    private readonly Lock _gate = new();

    // What the Orchestrator has to look at on its next pass (rule 6.11): the parents whose child just
    // finished, or everything. Kept here, beside the wake, because the wake alone made every pass check
    // every in-flight parent: measured 10,924 pages per pass on PostgreSQL with 1,000 messages in
    // flight, once per job completion.
    private readonly HashSet<Guid> _finalizedParents = [];
    private bool _parentSweepRequested;
    private long _lastParentSweep;

    /// <summary>
    /// Called when a job reaches a terminal state and the caller cannot say which parent it belonged to
    /// (a reconnect drain, a notification from a server that sent no ids). Wakes all subscribers to
    /// <see cref="ServerTaskSignal.JobFinalized"/>, and the Orchestrator's next pass checks every parent.
    /// </summary>
    public void SignalJobFinalized()
    {
        lock (_gate)
        {
            _parentSweepRequested = true;
        }

        Fire(_jobFinalizedWakers);
    }

    // The worker's form: it holds the finished job's row, so it names the one parent worth checking
    // (none for a job with no parent, which still wakes the Orchestrator for its continuations).
    internal void SignalJobFinalized(Guid? parentJobId)
    {
        if (parentJobId is { } parent)
        {
            lock (_gate)
            {
                AddFinalizedParent(parent);
            }
        }

        Fire(_jobFinalizedWakers);
    }

    internal void SignalJobsFinalized(IReadOnlyCollection<Guid> parentJobIds)
    {
        if (parentJobIds.Count > 0)
        {
            lock (_gate)
            {
                foreach (var parent in parentJobIds)
                {
                    AddFinalizedParent(parent);
                }
            }
        }

        Fire(_jobFinalizedWakers);
    }

    // Asks for a check of every parent on the next pass, without waking anything: for the Orchestrator
    // itself, when one pass could not take everything that was ready.
    internal void RequestParentSweep()
    {
        lock (_gate)
        {
            _parentSweepRequested = true;
        }
    }

    // Hands the Orchestrator its work and forgets it. A sweep of every parent is due when one was asked
    // for, on the first pass of the process, and once every sweepEvery: that last one is what finds a
    // parent whose child was finished by something that names no parent (a dashboard delete, crash
    // recovery, a worker of a server that could not take the lock). Measured on the stopwatch, not the
    // TimeProvider: this is a cadence, and a test's frozen clock must not switch it off.
    internal (Guid[] Parents, bool Sweep) TakeFinalizedParents(TimeSpan? sweepEvery)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var sweep = _parentSweepRequested
                || _lastParentSweep == 0
                || sweepEvery is not { } every
                || Stopwatch.GetElapsedTime(_lastParentSweep, now) >= every;

            if (sweep)
            {
                _parentSweepRequested = false;
                _lastParentSweep = now;
            }

            if (_finalizedParents.Count == 0)
            {
                return ([], sweep);
            }

            Guid[] parents = [.. _finalizedParents];
            _finalizedParents.Clear();

            return (parents, sweep);
        }
    }

    // A pass that failed gives its work back, so the next one repeats it.
    internal void ReturnFinalizedParents(Guid[] parents, bool sweep)
    {
        lock (_gate)
        {
            _parentSweepRequested |= sweep;
            foreach (var parent in parents)
            {
                AddFinalizedParent(parent);
            }
        }
    }

    // Caller holds _gate. Nothing drains the set in a process whose Orchestrator never gets the lock,
    // so it is bounded: past the cap the ids are dropped for one sweep, which finds the same parents.
    private void AddFinalizedParent(Guid parent)
    {
        if (_finalizedParents.Count >= MaxFinalizedParents)
        {
            _finalizedParents.Clear();
            _parentSweepRequested = true;

            return;
        }

        _finalizedParents.Add(parent);
    }

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
