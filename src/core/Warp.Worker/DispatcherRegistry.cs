using Warp.Core.Notifications;

namespace Warp.Worker;

/// <summary>
/// Per-host registry of running <see cref="WarpDispatcher{TContext}"/> instances. Registered as a
/// singleton in the worker DI container so each <c>IHost</c> has its own list — integration tests
/// boot multiple hosts in one process, and a process-wide static would cross-signal dispatchers
/// across host boundaries (and keep references to disposed dispatchers' semaphores alive).
/// <para>
/// Exposed to the Core notification listener through the <see cref="IDispatcherWake"/> seam so the
/// listener can wake dispatchers on a cross-process push without <c>Warp.Core</c> depending on
/// <c>Warp.Worker</c> (§0.5). Each dispatcher registers with its group's queues, so a push for one
/// queue wakes only the groups that drain it.
/// </para>
/// </summary>
public sealed class DispatcherRegistry : IDispatcherWake
{
    private readonly List<Registration> _registrations = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Registers a dispatcher's wake semaphore for every queue: it is woken by every push.
    /// </summary>
    public IDisposable Register(SemaphoreSlim signal) => Add(new Registration(this, signal, null));

    /// <summary>
    /// Registers a dispatcher's wake semaphore for the given <paramref name="queues"/> (exact, ordinal
    /// match): <see cref="Signal"/> for another queue does not wake it.
    /// </summary>
    public IDisposable Register(SemaphoreSlim signal, IReadOnlyCollection<string> queues) =>
        Add(new Registration(this, signal, new HashSet<string>(queues, StringComparer.Ordinal)));

    /// <summary>
    /// Wakes every registered dispatcher — used when the caller cannot say which queue has work
    /// (the notification listener's reconnect drain).
    /// </summary>
    public void SignalAll() => Signal(null);

    /// <summary>
    /// Wakes the registered dispatchers that drain <paramref name="queue"/>, so a
    /// <see cref="NotificationKind.JobEnqueued"/> push shortcuts their exponential-backoff sleep. A null
    /// <paramref name="queue"/> wakes every dispatcher.
    /// </summary>
    public void Signal(string? queue)
    {
        lock (_lock)
        {
            foreach (var registration in _registrations)
            {
                if (queue is not null && registration.Queues is { } queues && !queues.Contains(queue))
                {
                    continue;
                }

                if (registration.Semaphore.CurrentCount == 0)
                {
                    registration.Semaphore.Release();
                }
            }
        }
    }

    private Registration Add(Registration registration)
    {
        lock (_lock)
        {
            _registrations.Add(registration);
        }

        return registration;
    }

    private void Unregister(Registration registration)
    {
        lock (_lock)
        {
            _registrations.Remove(registration);
        }
    }

    private sealed class Registration(DispatcherRegistry registry, SemaphoreSlim signal, HashSet<string>? queues) : IDisposable
    {
        private bool _disposed;

        public SemaphoreSlim Semaphore => signal;

        public HashSet<string>? Queues => queues;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registry.Unregister(this);
        }
    }
}
