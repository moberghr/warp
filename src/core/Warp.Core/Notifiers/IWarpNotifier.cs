namespace Warp.Core.Notifiers;

/// <summary>
/// Host seam for operational alerting: Warp detects operational events it already knows about and hands each
/// one — <b>post-commit</b>, as a redaction-safe <see cref="WarpOperationalEvent"/> — to every registered
/// notifier. The host decides what to do with it (send a Teams/Slack message, an email, page, or just log).
/// Warp ships <b>no</b> channel integrations: this is a pure seam, the same shape as
/// <c>IWebhookDeliveryExhaustedHandler</c> / <c>IWebhookSigner</c> / <c>IWarpCredentialValidator</c>.
/// <para>
/// Alerting (Warp telling the operator something is wrong) is distinct from webhooks (the host telling
/// external subscribers about domain events) — a webhook needs a subscriber URL, which is the wrong
/// abstraction for internal self-reporting.
/// </para>
/// <para>
/// <b>Contract</b> (mirrors <c>IWebhookDeliveryExhaustedHandler</c>): invoked <b>after</b> the triggering
/// state transition is committed, <b>at-least-once</b> (a process crash between the commit and the
/// notification re-runs the source, so notifications may repeat — key any side effect on the event's id),
/// and a <b>throwing notifier is caught, logged at Warning, and never propagated</b> — an alert sink must
/// never take down the thing it observes.
/// </para>
/// <para>
/// Register with <c>opt.AddNotifier&lt;T&gt;()</c>. Notifiers are resolved as a set, so several can coexist;
/// with none registered the feature is inert. <b>Captive-dependency footgun (§8.18):</b> notifiers are
/// singletons — inject <see cref="System.IServiceProvider"/>'s <c>IServiceScopeFactory</c> for scoped
/// dependencies, never a <c>DbContext</c> directly.
/// </para>
/// </summary>
public interface IWarpNotifier
{
    /// <summary>
    /// Handle one operational event. Should complete quickly; long-running or unreliable delivery (an HTTP
    /// POST to Teams, an SMTP send) is the host's responsibility to bound. Honour <paramref name="ct"/>.
    /// Throwing is safe — it is logged and swallowed — but returns nothing to Warp.
    /// </summary>
    Task NotifyAsync(WarpOperationalEvent evt, CancellationToken ct);
}
