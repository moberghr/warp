using Microsoft.Extensions.Logging;

namespace Warp.Core.Notifiers;

/// <summary>
/// Fans one <see cref="WarpOperationalEvent"/> out to every registered <see cref="IWarpNotifier"/>, guarding
/// each call so a throwing notifier is logged at Warning and never propagates to the calling site (§8.20
/// pattern). Registered once by <c>AddWarp</c> so every dispatch site resolves it; with no notifiers
/// registered <see cref="DispatchAsync"/> is a no-op. Singleton — the injected notifier set is singleton too
/// (see the captive-dependency note on <see cref="IWarpNotifier"/>).
/// <para>
/// <b>Dispatch only POST-COMMIT.</b> Call <see cref="DispatchAsync"/> after the triggering state change is
/// durably committed. This is easy from an ordinary job handler or a cold admin service that owns its own
/// commit (e.g. the webhook executor, <c>SagaCommandService.ForceComplete</c>). <b>A server task
/// (<c>IServerTask</c>) runs inside the host's lock transaction</b> (<c>LocksWithTransaction</c>), so its
/// <c>ExecuteAsync</c> body is PRE-COMMIT — do NOT dispatch there. Buffer the events and dispatch them from
/// <c>IServerTask.OnCommittedAsync</c>, which the host invokes after the transaction commits (see
/// <c>ExpirationCleanup</c>/<c>ServerCleanup</c>). Dispatching pre-commit could alert on a change a rollback
/// then undoes.
/// </para>
/// <para>
/// <b>Public by necessity, not for host use:</b> the intended host seam is <see cref="IWarpNotifier"/> +
/// <c>opt.AddNotifier&lt;T&gt;()</c>. This type is <c>public</c> only because it is a constructor parameter
/// of the public dispatch-site classes (a public constructor cannot take a less-accessible parameter, CS0051).
/// Hosts should not resolve or call it directly.
/// </para>
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
