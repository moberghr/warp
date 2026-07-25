using Microsoft.Extensions.Logging.Abstractions;
using Warp.Core.Notifiers;

namespace Warp.Tests.Helpers;

/// <summary>
/// Test helpers for the operational-event notifier seam: an empty dispatcher for sites that don't assert
/// notifications, and a spy-backed dispatcher + notifier for the ones that do.
/// </summary>
internal static class TestNotifiers
{
    /// <summary>A real dispatcher with no notifiers — a no-op, for constructor sites that don't test dispatch.</summary>
    public static WarpNotifierDispatcher EmptyDispatcher()
        => new([], NullLogger<WarpNotifierDispatcher>.Instance);

    /// <summary>A dispatcher wired to <paramref name="spy"/> so a test can assert what was dispatched.</summary>
    public static WarpNotifierDispatcher SpyDispatcher(SpyNotifier spy)
        => new([spy], NullLogger<WarpNotifierDispatcher>.Instance);

    /// <summary>An empty scoped-events buffer, for server-task construction sites that don't assert events.</summary>
    public static PendingOperationalEvents EmptyPendingEvents() => new();
}

/// <summary>Captures every event dispatched to it, for assertions.</summary>
internal sealed class SpyNotifier : IWarpNotifier
{
    private readonly List<WarpOperationalEvent> _received = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<WarpOperationalEvent> Received
    {
        get
        {
            lock (_gate)
            {
                return [.. _received];
            }
        }
    }

    public Task NotifyAsync(WarpOperationalEvent evt, CancellationToken ct)
    {
        lock (_gate)
        {
            _received.Add(evt);
        }

        return Task.CompletedTask;
    }
}
