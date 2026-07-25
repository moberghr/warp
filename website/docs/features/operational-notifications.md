---
sidebar_position: 16
---

# Operational notifications (`IWarpNotifier`)

Warp detects operational events it already knows about — a webhook delivery giving up, a saga force-completed, an application instance going down — and hands each one to a host-implemented notifier. **You** decide what to do with it: post to Teams or Slack, send an email, page on-call, or just log it. Warp ships **no** channel integrations; this is a pure seam.

## Alerting is not webhooks

They look similar but point opposite ways:

- **Webhooks** (`IWebhookDispatcher.SendAsync`) — *your app* telling *external subscribers* about *your* domain events. Outbound, to a URL someone registered.
- **Operational notifications** (`IWarpNotifier`) — *Warp* telling *you, the operator* that something inside your system needs attention. There is no external subscriber and no URL — the destination is your ops channel.

Firing a webhook at yourself to alert yourself is circular; that's why alerting is its own seam.

## The seam

```csharp
public interface IWarpNotifier
{
    Task NotifyAsync(WarpOperationalEvent evt, CancellationToken ct);
}
```

Register one (or several) inside the `AddWarp` / `AddWarpServer` lambda:

```csharp
services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddNotifier<TeamsNotifier>();
    opt.AddNotifier<OpsEmailNotifier>();   // several coexist; each receives every event
});
```

With no notifier registered the feature is inert — nothing is dispatched, zero cost.

A minimal notifier:

```csharp
public sealed class TeamsNotifier : IWarpNotifier
{
    private readonly IHttpClientFactory _httpClientFactory;   // inject what you need

    public TeamsNotifier(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task NotifyAsync(WarpOperationalEvent evt, CancellationToken ct)
    {
        // Route by severity or switch on the concrete type for typed detail.
        if (evt.Severity < WarpEventSeverity.Warning)
        {
            return;
        }

        var card = evt switch
        {
            WebhookDeliveryExhaustedEvent w => $"Webhook {w.DeliveryId} to {w.GroupName ?? w.Url} gave up after {w.AttemptCount} attempts",
            InstanceDownEvent i => $"{(i.IsServer ? "Server" : "Instance")} {i.ApplicationName} went down",
            _ => evt.Message,
        };

        using var client = _httpClientFactory.CreateClient("teams");
        await client.PostAsJsonAsync("", new { text = card }, ct);
    }
}
```

## Contract — the guarantees

- **Post-commit.** A notifier fires *after* the triggering state transition is durable — the saga row is already gone, the delivery is already `Exhausted`, the instance is already reaped. You never see an event a rollback could undo. (For the two server-task-sourced events, the dispatch is deferred until after the server task's lock transaction commits — never inside it.)
- **Never propagates.** A throwing notifier is caught, logged at `Warning`, and swallowed — one bad notifier does not stop the others, and an alert sink can never take down the thing it observes. It also never fails or slows the worker.

## Durability — events are not persisted

There is **no notification outbox.** The dispatch is in-process from an in-memory buffer, so **delivery to your sink is best-effort for every event.** An alert is dropped, with no retry, if:

- the process **crashes** in the window between the triggering commit and the dispatch, or
- your notifier is **down or throws** when called (the exception is swallowed and logged at `Warning`; there is no notification-level retry).

This is by design — these are convenience alerts, not an audit trail. The operator action, the `WebhookDelivery` row, and the instance roster remain the systems of record; don't build guaranteed-delivery accounting on notifications.

**Per-source nuance.** `WebhookDeliveryExhausted` is the one event Warp will **re-emit** after a crash-before-dispatch: the event itself still isn't persisted, but the delivery's `Exhausted` state is, and the executor job's crash-recovery re-run regenerates the event (so it may repeat — key side effects on the event id). That still doesn't help if your sink is unreachable at call time. `SagaForceCompleted` and `InstanceDown` have no replay at all — they report a row *deleted* in the committing transaction, so a lost dispatch is gone for good.

**Keep `NotifyAsync` quick.** It runs inline at the dispatch site (a server-task tick or the webhook executor job — never the worker fetch/execute hot path). Long or unreliable delivery (an HTTP POST, an SMTP send) is yours to bound; honour the `CancellationToken`.

**Captive-dependency footgun.** Notifiers are singletons. Inject `IServiceScopeFactory` if you need scoped services (e.g. a `DbContext`), never the scoped dependency directly — `ValidateScopes=true` catches the mistake at startup.

## The events

Every event is a redaction-safe snapshot: **identity and metadata only, never a payload body** (job message, webhook body, saga state). The base carries a `Type`, a `Severity`, a timestamp, the machine, the originating `Application` (when `ApplicationName` is set), and a human-readable `Message`; the concrete subtype adds typed detail.

| `WarpEventType` | Subtype | Raised when | Severity |
|---|---|---|---|
| `WebhookDeliveryExhausted` | `WebhookDeliveryExhaustedEvent` | a webhook delivery exhausts its retry schedule without success | Warning |
| `SagaForceCompleted` | `SagaForceCompletedEvent` | an operator force-completes (dead-letters) a saga | Info |
| `InstanceDown` | `InstanceDownEvent` | an application instance or worker server is reaped by the stale sweep (`IsServer` distinguishes them) | Warning |

Two enum values are **reserved but not yet emitted** — a host can switch on them safely; they simply never arrive until a later release wires them:

- `JobDeadLettered` — a job permanently failed (exhausted retries). Deferred: the worker's finalization signal is payloadless, so at-least-once dead-letter notification needs a claim mechanism that is a slice of its own.
- `BacklogBreached` — a queue wait-time / depth threshold was crossed. Waiting on queue-wait metrics.

## Where events come from

Warp raises these from detections it already had — no new scanning loop, and the worker fetch/execute path is untouched:

- **Webhook exhaustion** — the webhook executor, post-commit, alongside the existing `IWebhookDeliveryExhaustedHandler`. Per-attempt call logs still require `AddAdapters()`; the notification does not.
- **Saga force-complete** — `ISagaCommandService.ForceComplete`, after the saga row is removed.
- **Instance down** — the `ExpirationCleanup` server task's stale-instance sweep (non-server processes, `IsServer=false`) and the `ServerCleanup` server task's stale-server sweep (worker servers, `IsServer=true`).

A Warp server must be running somewhere for the server-task-sourced events (instance-down) to fire, since those live in server tasks; the webhook and saga events fire wherever that work runs.
