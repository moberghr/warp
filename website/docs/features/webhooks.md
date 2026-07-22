---
sidebar_position: 13
---

# Outbound Webhooks

There is no maintained embedded .NET library for *sending* webhooks. Microsoft's ASP.NET WebHooks is archived; the serious options (Svix, Hookdeck Outpost, Convoy) are standalone Rust/Go services you deploy and operate. So teams hand-roll delivery on top of their job scheduler and re-accumulate the same defects: per-attempt correlation ids, 200-only success checks, hand-coupled backoff switches, no redelivery, no dead-letter.

Durable outbound webhook delivery is a **built-in Core feature** (`Warp.Core.Webhooks`) — always on, no package to add and no opt-in switch. The split is deliberate and narrow: **the host owns subscriptions and fan-out; Warp owns everything after `SendAsync`** — durability, signing, scheduled retries, tracking, redelivery, and a dedicated dashboard section. Positioning mirrors Warp itself: durable delivery on the Postgres/SQL Server you already run, no extra infrastructure.

It builds directly on [Outbound Adapters](./adapters.md): every attempt is an ordinary `warp-webhooks` adapter call, so the attempt timeline is just `AdapterCallLog` rows keyed by the delivery id — no separate attempt table.

## The integration boundary

This is the whole design in one table. The host keeps everything that is *its* model (who subscribes, what the payload is, when to disable an endpoint); Warp takes over the mechanical delivery problem the moment you call `SendAsync`.

| Host owns | Warp owns |
|---|---|
| Subscriptions (who receives what), their storage, portals, permissions | `WebhookDelivery` lifecycle after `SendAsync` |
| Fan-out (which subscriptions match an event) | Executor jobs, retry scheduling, exhaustion |
| Payload building + serialization (Payload is an opaque string) | Signing at attempt time (per configured mode) |
| Endpoint enable/disable decisions | `OnDeliveryExhausted` signal (host callback) |
| Secrets lifecycle in its own tables | Secret carried on the delivery row, redacted on all read surfaces |
| Legacy body-embedded signing (via `Signing = None`) | Standard Webhooks header signing |

Warp does **not** model subscriptions or fan-out — that would duplicate every host's tenancy model badly. You resolve which endpoints match an event from your own tables and call `SendAsync` once per destination.

## Design pillars

1. **The delivery, not the job, is the state machine.** Executor jobs *always complete*; failure lives on the `WebhookDelivery` row (`Pending → Delivered | Exhausted`). No failed jobs, ever — webhooks are visible in their own dashboard section, not the Jobs UI.
2. **Self-contained delivery row.** Everything needed to execute to completion is on the row at `SendAsync` time: URL, headers, payload, retry schedule, success codes, signing mode + secret, group, reference. Nothing is looked up from ambient config mid-flight; a config deploy never reshapes an in-flight delivery.
3. **Per-send cadence.** `RetrySchedule` is a plain `IReadOnlyList<TimeSpan>` on the send request. There is deliberately *no* app-level schedule setting — cadence is a property of what is being delivered, which only the host knows.
4. **Attempts are adapter calls.** The HTTP leg goes through the `warp-webhooks` adapter (group = endpoint, operation = event type, `CorrelationId` = delivery id). One capture/redaction/retention machinery; aggregate health appears on the Adapters page for free.
5. **No timers, no scans.** The executor job *is* the clock: the first attempt is enqueued immediately, each retry is a job in `State.Scheduled` activated by `ScheduledJobActivation`. `NextAttemptAt` on the row is display metadata, not a delivery mechanism — with one deliberate crash-recovery exception ([stuck-delivery recovery](#stuck-delivery-recovery) sweeps it to find deliveries whose executor job was lost to a faulted commit).

## Setup

There is nothing to enable. `AddWarp` wires the dispatcher, executor, redelivery enqueuer, and built-in signer; `AddWarpServer`'s worker polls the dedicated `warp:webhooks` queue automatically. Just inject `IWebhookDispatcher` and call `SendAsync`:

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddAdapters();          // OPTIONAL: record each attempt as an AdapterCallLog row
    opt.AddWebhooks(w =>        // OPTIONAL: only for a custom signer / exhausted-handler
        w.OnDeliveryExhausted<MyExhaustedHandler>());
});
```

`AddWebhooks(...)` is **optional configuration only** — a custom `IWebhookSigner` and the `OnDeliveryExhausted` callback. It is not an enable switch; delivery runs without it. `AddAdapters()` is also optional: with it, every attempt is recorded as a `warp-webhooks` `AdapterCallLog` row (response bodies always captured, request bodies never — the payload already lives on the delivery row) and the per-attempt timeline is populated; without it, the delivery state machine, retries, exhaustion, and dashboard still work fully from the `WebhookDelivery` row — only the granular per-attempt HTTP call log is absent (same telemetry-vs-recording split as adapters).

### Requires a Warp server somewhere

Webhooks **execute jobs**, so a Warp server (worker) must run somewhere in the deployment to drain the `warp:webhooks` queue. Because delivery is a Core feature, **every `AddWarpServer` with a worker drains it** — there is no per-server opt-in to remember. The publisher process that calls `SendAsync` need not be the worker; the delivery row and executor job commit through the outbox and any server in the cluster picks the job up. An `AddWarp`-only publisher/dashboard process stages deliveries and relies on a server elsewhere to run them.

### Sending

Inject `IWebhookDispatcher` and hand it a fully-described `WebhookSend`. `SendAsync` persists a `Pending` delivery row and enqueues the first attempt — both in the caller's transaction (outbox), so the delivery becomes visible atomically with your own writes — and returns the delivery id (the value used as the adapter `CorrelationId`):

```csharp
public sealed class OrderEvents(IWebhookDispatcher webhooks)
{
    public async Task NotifyOrderCreated(Subscription sub, Order order, CancellationToken ct)
    {
        await webhooks.SendAsync(
            new WebhookSend
            {
                Url = sub.CallbackUrl,
                EventType = "order.created",
                EventId = order.Id.ToString(),        // stable idempotency key (webhook-id)
                Payload = JsonSerializer.Serialize(order),
                Group = sub.EndpointName,             // → adapter group (per-endpoint stats)
                Reference = sub.Id.ToString(),         // your opaque link back to the subscription
                Signing = WebhookSigning.StandardWebhooks,
                Secret = sub.SigningSecret,            // whsec_… carried on the row, redacted on read
            },
            ct);
    }
}
```

You call `SendAsync` once per destination — fan-out is yours. Overlap protection for an in-flight delivery comes from worker claim semantics plus a status guard on the delivery write; you never get two concurrent attempts for the same delivery.

## Per-send retry schedule

`RetrySchedule` is the cadence for *this* delivery. N entries means N retries after the first attempt; the delay before retry *N* is `schedule[N-1]`. The schedule column is never mutated during execution — `(RetrySchedule, AttemptCount)` fully determines the remaining plan, and the delivery is exhausted once `AttemptCount` exceeds `schedule.Count`.

```csharp
// Omitted (null) → library built-in [1m, 10m, 1h, 6h]: first attempt + 4 retries.
new WebhookSend { /* no RetrySchedule */ };

// Explicit backoff — first attempt now, then +30s, +5m, +1h:
new WebhookSend
{
    RetrySchedule = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromHours(1)],
};

// Empty list → EXACTLY ONE attempt, then Exhausted on failure. No retries.
new WebhookSend { RetrySchedule = [] };
```

`[]` is the explicit single-attempt shape: one POST, and on failure the delivery goes straight to `Exhausted` and the exhausted handler fires. Use it for best-effort notifications where a retry storm is worse than a miss.

The schedule persists as a JSON seconds array (`"[60,600,3600,21600]"`, `"[]"` for empty), so it roundtrips readably on both providers.

### Cadence precision

Retry timing is **not** a precision scheduler, and the docs are explicit about it: a scheduled retry fires at *delay + up to ~5s activation latency + worker pickup*. Each retry is a job in `State.Scheduled`; `ScheduledJobActivation` flips it to `Enqueued` when `ScheduleTime <= now`, and its cadence (`ScheduledActivationInterval`, default 5s) is the worst-case slack before the job becomes eligible, after which a worker fetches it on its normal poll/signal cycle. This is exactly the right precision for exponential backoff and deliberately not marketed as anything tighter — if you need sub-second delivery deadlines, webhooks are the wrong tool.

### Do not wrap the client in an external HTTP retry handler

The `warp-webhooks` adapter **owns delivery retries** — the `RetrySchedule` above, driven by scheduled jobs. It deliberately registers with no `UseResilience()`. Do **not** put a general-purpose Polly retry handler on its `HttpClient`. The common way this happens by accident is a *global* handler applied to every client — most notably the .NET Aspire `ServiceDefaults` template, whose `ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler())` blankets all clients, `warp-webhooks` included.

The failure mode is subtle because delivery still works: an external handler retries the HTTP POST *inside* a single scheduled attempt, so each transient failure (a `503` from a down receiver, a timeout) is retried several times before the attempt returns. Because the per-attempt timeline is assembled from `AdapterCallLog` rows by `CorrelationId`, every one of those inner retries lands its own row — a 3-attempt `RetrySchedule` shows up as a dozen "attempts" in the dashboard, and you now have two uncoordinated backoff layers. Keep resilience **per-adapter** (`a.UseResilience()` on the adapters that want it) rather than global, or scope the global handler to exclude the `warp-webhooks` client.

### Stuck-delivery recovery

The executor claims each attempt atomically *before* the HTTP leg and commits the outcome (delivered / retry scheduled / exhausted) *after* it. If a transient database fault hits that second commit, the delivery is left `Pending` with no live executor job — the retry job was staged inside the failed transaction. Nothing scans `NextAttemptAt` in normal operation, so `StaleJobRecovery` (an existing server task) carries the one deliberate recovery sweep: any `Pending` delivery whose `NextAttemptAt` is more than `WarpConfiguration.WebhookStuckDeliveryGrace` (default 10 minutes) in the past gets a fresh executor job.

Duplicate jobs are guarded twice over. First, the sweep skips any candidate that still has a live executor job on the webhooks queue — a delivery whose workers are merely backlogged past the grace is left alone. Second, the guarded `NextAttemptAt` bump and the new executor job commit in **one transaction** (the same pattern `Redeliver` uses), so a fault can never leave a bumped row without its job or a job without its bump, and consecutive sweeps never double-enqueue. Recovery is still at-least-once end to end: the recovered attempt may repeat a POST whose *outcome* was lost — the same delivery guarantee webhooks already carry.

## Success codes

By default any 2xx response marks the delivery `Delivered`. Pin `SuccessCodes` when the receiver's contract is narrower — e.g. a receiver that must return `200` and where a `202 Accepted` means "queued, not yet durable" and should be retried:

```csharp
new WebhookSend { SuccessCodes = [200] };   // a 202 now counts as a failed attempt
```

Any response outside the success set (or a thrown transport exception) is a failed attempt: retried if the schedule has entries left, otherwise exhausted.

**Any exception from the HTTP leg consumes a schedule slot.** The executor treats *every* throw while attempting the HTTP request as one failed attempt — this includes a rate-limit rejection (`AdapterRateLimitedException`) if the host attaches the adapters shared rate limiter to the `warp-webhooks` client. A throttled attempt is not free: it increments `AttemptCount` and burns the next entry of `RetrySchedule` exactly as a `500` or a connection failure would, and the delivery exhausts once the schedule is consumed regardless of *why* the attempts failed. This is deliberate — the delivery state machine does not distinguish a throttle from any other failure — but it means a rate limiter tuned too tight can exhaust a delivery on throttles alone. If you rate-limit the webhooks client, size `RetrySchedule` with that in mind (a throttle-heavy endpoint wants more, longer-spaced entries), or leave the `warp-webhooks` adapter unthrottled and shape delivery rate through the schedule instead.

## Signing

`WebhookSigning` selects how each attempt is signed. The mode and secret ride the `WebhookSend` and are stamped on the row, so signing is computed fresh per attempt from self-contained data:

- **`None`** (default) — no signing headers added. This is the **migration path** for hosts whose consumers already verify a body-embedded HMAC produced in the host's own payload-building code (see below). Warp adds nothing; your existing signature travels inside `Payload`.
- **`StandardWebhooks`** — HMAC-SHA256 over `{webhook-id}.{webhook-timestamp}.{payload}`, emitting `webhook-id`, `webhook-timestamp`, and `webhook-signature` (`v1,<base64>`). `webhook-id` equals the delivery's `EventId` and is **constant across retries**, so it doubles as the consumer's idempotency key. Matches the published [Standard Webhooks](https://www.standardwebhooks.com/) spec and test vectors; the secret is the `whsec_…` form.
- **`Custom`** — resolves the `IWebhookSigner` you registered via `AddWebhooks(w => w.UseCustomSigner<MySigner>())`. Declaring custom signing without wiring a signer throws at `AddWebhooks` registration time, not mid-delivery.

```csharp
opt.AddWebhooks(w =>
{
    w.UseCustomSigner<AcmeLegacySigner>();
    w.OnDeliveryExhausted<AlertOnDeadLetter>();
});
```

## Secrets at rest

Per-delivery signing secrets are stored on the `WebhookDelivery` row. This is an accepted, deliberate trade for **self-containment**: an attempt must never fail because the host moved or rotated a secret between publish and delivery, so the secret rides the row rather than being fetched via a host callback at attempt time.

The stance and the controls:

- **Redacted on every read surface.** The query service, the REST endpoints, and the dashboard expose the secret only as a `HasSecret` boolean — the value never leaves the service. `Authorization`-class headers are redacted the same way. Redaction is not a caller-toggleable option.
- **Never logged.** Payloads and secrets are user data (§1.2) — stored (that is the feature) but never emitted to logs at Info level or above.
- **Escape hatch for stricter requirements.** Hosts that cannot store signing secrets at rest use **`Signing = None`** and sign in their own payload-building code (a body-embedded HMAC over the payload they already serialize). Warp then persists no secret for that delivery; the security boundary stays entirely in the host.

## Exhaustion and the host callback

When a delivery's schedule is exhausted by a failed attempt, Warp sets `Status = Exhausted` and — **after** that transition commits — invokes the registered `IWebhookDeliveryExhaustedHandler` with a redaction-safe snapshot (delivery id, event type/id, url, group, reference, attempt count — no payload, headers, or secret):

```csharp
public sealed class AlertOnDeadLetter(ISubscriptionStore store) : IWebhookDeliveryExhaustedHandler
{
    public async Task OnDeliveryExhaustedAsync(WebhookDeliveryExhausted d, CancellationToken ct)
    {
        // Your decision: disable the endpoint, alert, flag the subscription — Warp only signals.
        await store.RecordDeadLetter(d.Reference, d.DeliveryId, ct);
    }
}
```

This is the `OnDeliveryExhausted` signal from the boundary table. Warp reports the dead-lettered delivery; **the host decides** whether to disable the endpoint — endpoint lifecycle belongs to your subscription record. If the callback throws, it is logged at Warning and the failure never propagates to the executor job; the delivery stays `Exhausted` and the job still completes.

**Delivery guarantee: at-least-once.** The callback fires once per exhaustion transition on the happy path, and always *after* the `Exhausted` row is durably committed (so a rolled-back transition can never re-fire it). If the process crashes between that commit and the callback, the executor job is retried and re-invokes the callback for the same already-`Exhausted` delivery. The obligation even survives a **Redeliver** racing that recovery window: the pending-callback flag rides through the settled→`Pending` flip, and the redelivered executor run fires the prior exhaustion's callback before attempting. **Implement the handler idempotently** — key any side effect (alert, disable, dead-letter record) on the delivery id.

## Redelivery

A settled (`Delivered` or `Exhausted`) delivery can be requeued from the dashboard or through `IWebhookCommandService.Redeliver(deliveryId)`: it resets to `Pending` with a fresh attempt budget, refreshes `ExpireAt` so it can't be swept mid-flight, and enqueues an immediate executor job — the settled→`Pending` flip and the enqueue commit atomically in one transaction, so two concurrent redelivers on one delivery enqueue exactly one job.

`Redeliver` returns a `WebhookRedeliveryResult` so callers map outcomes precisely: `Enqueued` (requeued), `NotFound` (unknown id), and `Rejected` (already `Pending` — it owns a live job). Because the redelivery enqueuer is part of Core and registered by `AddWarp`, `Redeliver` works from **any** process (dashboard-only / publisher-only included) — it stages the executor job through the outbox and a server elsewhere runs it. (The `Unavailable` result remains defined for defensiveness but is no longer reachable in a normal `AddWarp` process now that the enqueuer is always present.) The REST endpoint maps `Enqueued`→`200`, `NotFound`→`404`, and `Rejected`→`409`.

## Migrating from a hand-rolled implementation

If you already deliver webhooks on top of Hangfire / Quartz / a bespoke job + delivery table, the migration is incremental and does not require a big-bang cutover.

1. **Keep your subscription tables.** Warp models none of them. Your fan-out query — "which endpoints subscribe to `order.created`?" — stays exactly as it is.
2. **Replace the enqueue call.** Everywhere you previously scheduled a delivery job (`BackgroundJob.Enqueue(() => DeliverWebhook(...))` or equivalent), call `IWebhookDispatcher.SendAsync(new WebhookSend { ... })` instead. Map your endpoint's URL, headers, and the serialized payload onto the send. Your delivery id becomes Warp's `WebhookDelivery.Id`; put your subscription id in `Reference` so you can still join back.
3. **Preserve your existing signature first.** Start with `Signing = None` so consumers keep verifying the body-embedded HMAC your current code produces — zero consumer changes. Adopt `Signing = StandardWebhooks` additively later, once receivers are ready for the `webhook-*` headers.
4. **Map your backoff to `RetrySchedule`.** Your hand-coupled backoff switch (`attempt switch { 1 => 1min, 2 => 10min, ... }`) becomes an ordered `IReadOnlyList<TimeSpan>` on the send. A max-attempts-then-dead-letter policy maps to a finite schedule plus an `IWebhookDeliveryExhaustedHandler` that does what your dead-letter branch did.
5. **Retire your attempt table.** The per-attempt log becomes `AdapterCallLog` rows keyed by `CorrelationId = deliveryId` (redacted request/response captured by the adapter layer), surfaced as the attempt timeline on the delivery detail page. Delete the bespoke attempt table once the dashboard covers your operational needs.

Migration tooling for legacy delivery tables is intentionally out of scope — the path above is the supported shape, done per event type as you touch each one.

## Coexisting with another job system

Webhooks require a Warp worker, but you do **not** have to move your whole job workload to Warp to adopt them. A common shape is a Warp server that runs *only* the webhooks executor alongside an existing Hangfire/Quartz deployment that keeps running your business jobs. Warp's queue (`warp:webhooks`), its retry scheduling, and its dashboard are self-contained; the two systems share only the database you already run. This lets you adopt durable webhook delivery as a bounded, low-risk slice without a scheduler migration.

## Retention and cleanup

`WebhookDelivery` rows carry an `ExpireAt` from `WarpConfiguration.WebhookDeliveryRetention` (default 30 days). `ExpirationCleanup` — an existing server task, so the worker hot path is untouched — deletes expired **settled** deliveries (`Delivered` / `Exhausted`) on each tick. `Pending` deliveries are excluded from the sweep: a long retry backoff can outrun retention, and an in-flight delivery must never vanish out from under its own scheduled executor job. Redelivery also refreshes `ExpireAt`, so a requeued delivery gets a full retention window from the moment it is requeued. The `warp-webhooks` adapter's call-log retention is aligned to the same value by default, so attempt rows and delivery rows age together (they expire independently — there is no FK cascade — but the aligned default keeps them in step).

The `WebhookDelivery` table is added to the model **unconditionally** by `WarpModelCustomizer`, whether or not any host calls `AddWebhooks()` — same principle as the other addon entities, so the migration story is independent of which processes opt in.

## Telemetry

Three counters, in addition to the HTTP leg's spans/duration/error counters that come from the adapter layer (not duplicated here):

- `warp.webhooks.deliveries` (tags: outcome `delivered|exhausted`)
- `warp.webhooks.attempts` (tags: outcome)
- `warp.webhooks.redeliveries`

## Dashboard

A **Webhooks** nav item (always shown — webhooks is a Core feature; the `webhooks` flag from `GET {prefix}/api/addons` gates on the always-registered redelivery enqueuer) opens two screens:

**Deliveries list** — summary tiles (deliveries, delivered %, pending, exhausted) over a table of status, event, endpoint (the group value), reference, attempts, next-attempt time, and created time — with filters for status, event type, reference, group, and date.

**Delivery detail** — the self-contained contract exactly as stamped at `SendAsync` time: URL, group, reference, signing mode, retry schedule, success codes, attempt count, next-attempt/created/expires timestamps — the secret rendered only as `***` via the `HasSecret` flag and `Authorization`-class headers redacted (not caller-toggleable). Below it, the **per-attempt timeline** assembled from `AdapterCallLog` rows by `CorrelationId` — each attempt's outcome, status code, duration, and captured response body — and a **Redeliver** action for settled (`Delivered`/`Exhausted`) deliveries.

The same data is served over Core query/command services (`IWebhookQueryService` / `IWebhookCommandService`, resolvable in any `AddWarp` process), so a host can equally expose it through its own permission-gated portal:

- `GET {prefix}/api/webhooks` — filtered, paged delivery list.
- `GET {prefix}/api/webhooks/{id}` — one delivery's contract + attempt timeline (secret reduced to `HasSecret`).
- `GET {prefix}/api/webhooks/summary` — tile counts.
- `POST {prefix}/api/webhooks/{id}/redeliver` — requeue a settled delivery (`200` requeued; `404` unknown; `409` already pending or no webhooks worker in this process).

## Not in scope

By design (host-owned or deferred):

- **Subscription / registration modeling and fan-out** — host-owned; Warp starts at `SendAsync`.
- **Automatic endpoint disabling** — the host decides via `IWebhookDeliveryExhaustedHandler`.
- **Per-endpoint keyed rate limiting of deliveries** — a deferred extension of the adapters shared limiter.
- **FIFO / ordering guarantees across deliveries** — independent deliveries execute independently.
- **Payload transformations, receiving-side webhooks, migration tooling for legacy delivery tables.**
