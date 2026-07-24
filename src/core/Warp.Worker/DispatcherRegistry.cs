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
/// <c>Warp.Worker</c> (§0.5).
/// </para>
/// </summary>
public sealed class DispatcherRegistry : IDispatcherWake
{
    private readonly List<SemaphoreSlim> _signals = [];
    private readonly Lock _lock = new();

    public IDisposable Register(SemaphoreSlim signal)
    {
        lock (_lock)
        {
            _signals.Add(signal);
        }

        return new Registration(this, signal);
    }

    /// <summary>
    /// Wakes every registered dispatcher — used by the notification listener so a
    /// <see cref="Warp.Core.Notifications.NotificationKind.JobEnqueued"/> push shortcuts the
    /// current exponential-backoff sleep.
    /// </summary>
    public void SignalAll()
    {
        lock (_lock)
        {
            foreach (var signal in _signals)
            {
                if (signal.CurrentCount == 0)
                {
                    signal.Release();
                }
            }
        }
    }

    private void Unregister(SemaphoreSlim signal)
    {
        lock (_lock)
        {
            _signals.Remove(signal);
        }
    }

    private sealed class Registration(DispatcherRegistry registry, SemaphoreSlim signal) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registry.Unregister(signal);
        }
    }
}
