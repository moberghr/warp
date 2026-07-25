using Microsoft.Extensions.Logging;

namespace Warp.Core.Notifiers;

/// <summary>
/// Fans one <see cref="WarpOperationalEvent"/> out to every registered <see cref="IWarpNotifier"/>, guarding
/// each call so a throwing notifier is logged at Warning and never propagates to the calling site (§8.20
/// pattern). Registered once by <c>AddWarp</c> so every dispatch site resolves it; with no notifiers
/// registered <see cref="DispatchAsync"/> is a no-op. Singleton — the injected notifier set is singleton too
/// (see the captive-dependency note on <see cref="IWarpNotifier"/>).
/// </summary>
public sealed class WarpNotifierDispatcher
{
    private readonly IReadOnlyList<IWarpNotifier> _notifiers;
    private readonly ILogger<WarpNotifierDispatcher> _logger;

    public WarpNotifierDispatcher(IEnumerable<IWarpNotifier> notifiers, ILogger<WarpNotifierDispatcher> logger)
    {
        _notifiers = [.. notifiers];
        _logger = logger;
    }

    /// <summary>
    /// Dispatch <paramref name="evt"/> to every registered notifier, in registration order. Each call is
    /// guarded independently — one throwing notifier does not stop the others, and nothing propagates to the
    /// caller. Returns immediately when no notifiers are registered.
    /// </summary>
    public async Task DispatchAsync(WarpOperationalEvent evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (_notifiers.Count == 0)
        {
            return;
        }

        foreach (var notifier in _notifiers)
        {
            try
            {
                await notifier.NotifyAsync(evt, ct);
            }
#pragma warning disable CA1031 // host callback: a throwing notifier is logged and never propagated to the source site.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(
                    ex,
                    "IWarpNotifier {Notifier} threw handling {EventType}; continuing.",
                    notifier.GetType().Name,
                    evt.Type);
            }
        }
    }
}
