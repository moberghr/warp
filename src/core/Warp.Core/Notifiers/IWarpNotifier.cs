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
/// <b>Events are NOT persisted — there is no notification outbox.</b> The dispatch is in-process from an
/// in-memory buffer, so <b>delivery to the sink is best-effort for every event</b>: a process crash in the
/// window between the triggering commit and the dispatch, or a notifier that is down/throwing when called
/// (the exception is swallowed + logged, never retried), drops that alert with no replay. Do not build
/// guaranteed-delivery accounting on any of these events — they are convenience alerts, not an audit trail,
/// and the operator action / delivery row / instance roster remain the systems of record.
/// </para>
/// <para>
/// <b>The one partial exception is <c>WebhookDeliveryExhausted</c>:</b> the event itself is still not
/// persisted, but the delivery's <c>Exhausted</c> state <em>is</em>, and the executor job's crash-recovery
/// re-run re-emits the event — so Warp will regenerate it after a crash-before-dispatch (notifications may
/// then repeat — key any side effect on the event id). That still does not help if the sink is unreachable
/// at call time. <c>SagaForceCompleted</c> and <c>InstanceDown</c> have no such replay — they report a row
/// <em>deleted</em> in the committing transaction, so a lost dispatch is gone for good.
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
