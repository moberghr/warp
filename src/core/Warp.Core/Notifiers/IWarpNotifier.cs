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
/// <b>Contract:</b> a notifier is invoked <b>after</b> the triggering state transition is committed, and a
/// <b>throwing notifier is caught, logged at Warning, and never propagated</b> — an alert sink must never
/// take down the thing it observes.
/// </para>
/// <para>
/// <b>Delivery guarantee is per source.</b> <c>WebhookDeliveryExhausted</c> is <b>at-least-once</b>: the
/// exhaustion is a persisted delivery state recovered on the executor job's re-run, so a crash between commit
/// and notification replays it (notifications may repeat — key any side effect on the event id).
/// <c>SagaForceCompleted</c> and <c>InstanceDown</c> are <b>best-effort</b>: they report a row that was
/// <em>deleted</em> in the committing transaction, so there is nothing to re-detect — a process crash in the
/// narrow window between that commit and the dispatch drops the notification. That is acceptable because the
/// operator action (force-complete) and the instance roster (dashboard) remain the systems of record; the
/// notification is a convenience alert, not an audit trail. Do not build guaranteed-delivery accounting on
/// these two events.
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
