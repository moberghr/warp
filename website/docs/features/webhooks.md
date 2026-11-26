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

1. **The delivery, not the job, is the state machine.** Executor jobs *always complete*; failure lives on the `WebhookDelivery` row (`Pending → Delivered | Exhausted`, plus `Parked` while its [endpoint is failing](#endpoint-health) and `Cancelled` by an [operator](#cancelling-deliveries)). No failed jobs, ever — webhooks are visible in their own dashboard section, not the Jobs UI.
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
    opt.AddWebhooks(w =>        // OPTIONAL: custom signer / exhausted handler / address guard
    {
        w.OnDeliveryExhausted<MyExhaustedHandler>();
        w.PublicAddressesOnly = true;   // production: never connect to private/loopback/metadata addresses
    });
});
```

`AddWebhooks(...)` is **optional configuration only** — a custom `IWebhookSigner`, the `OnDeliveryExhausted` callback, the [public-addresses-only guard](#public-addresses-only), [endpoint health](#endpoint-health) and [deduplication](#deduplication). It is not an enable switch; delivery runs without it. When a delivery exhausts, Warp also emits a `WebhookDeliveryExhausted` [operational notification](./operational-notifications.md) to any registered `IWarpNotifier` — the generic operator-alerting channel, alongside the webhook-specific `OnDeliveryExhausted` callback. `AddAdapters()` is also optional: with it, every attempt is recorded as a `warp-webhooks` `AdapterCallLog` row (response bodies always captured, request bodies never — the payload already lives on the delivery row) and the per-attempt timeline is populated; without it, the delivery state machine, retries, exhaustion, and dashboard still work fully from the `WebhookDelivery` row — only the granular per-attempt HTTP call log is absent (same telemetry-vs-recording split as adapters).

### Requires a Warp server somewhere

Webhooks **execute jobs**, so a Warp server (worker) must run somewhere in the deployment to drain the `warp:webhooks` queue. Because delivery is a Core feature, **every `AddWarpServer` with a worker drains it** — there is no per-server opt-in to remember. The publisher process that calls `SendAsync` need not be the worker; the delivery row and executor job commit through the outbox and any server in the cluster picks the job up. An `AddWarp`-only publisher/dashboard process stages deliveries and relies on a server elsewhere to run them.

### Webhook workers

Attempts wait on other people's servers, up to `WebhookAttemptTimeout` (30 s) each, so since 8.1 **every server runs them on a dedicated worker group** that polls only `warp:webhooks`, with `WebhookWorkerCount` workers (default **4**), and the default worker group no longer polls `warp:webhooks`. A receiver that hangs can hold at most those workers; your jobs keep their own. It also caps concurrent attempts per server: with 4 workers and receivers that answer in 200 ms, a server sends about 20 deliveries a second, so raise it for high webhook volume (or add servers).

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.WebhookWorkerCount = 8; // 0 = the pre-8.1 shape: the default group drains warp:webhooks
});
```

The group takes the top-level polling settings and no prefetch. It shows on the dashboard like any worker group, its workers count in the server's worker total, and it is paused on its own: **pausing the default group no longer stops webhook delivery** (pause the webhook group as well, or the whole server). A group listing `warp:webhooks` with no workers does not count as routing it. If you route `warp:webhooks` yourself (listed in `Queues`, or in an `AddWorkerGroup(...)` group), that wins: no dedicated group is added, and with an explicit group listing it the default group does not poll it either.

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

Retry timing is **not** a precision scheduler, and the docs are explicit about it: a scheduled retry fires at *delay + up to ~10s activation latency + worker pickup*. Each retry is a job in `State.Scheduled`; `ScheduledJobActivation` flips it to `Enqueued` when `ScheduleTime <= now`, and its cadence (`ScheduledActivationInterval`, default 10s) is the worst-case slack before the job becomes eligible, after which a worker fetches it on its normal poll/signal cycle. This is exactly the right precision for exponential backoff and deliberately not marketed as anything tighter — if you need sub-second delivery deadlines, webhooks are the wrong tool.

### Do not wrap the client in an external HTTP retry handler

The `warp-webhooks` adapter **owns delivery retries** — the `RetrySchedule` above, driven by scheduled jobs. It deliberately registers with no retry handler of its own. Do **not** put a general-purpose retry handler on its `HttpClient`. The common way this happens by accident is a *global* handler applied to every client — most notably the .NET Aspire `ServiceDefaults` template, whose `ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler())` blankets all clients, `warp-webhooks` included.

The failure mode is subtle because delivery still works: an external handler retries the HTTP POST *inside* a single scheduled attempt, so each transient failure (a `503` from a down receiver, a timeout) is retried several times before the attempt returns. The executor records one `AdapterCallLog` row per scheduled attempt, around the whole `SendAsync` call, so those inner retries are hidden inside it: the timeline shows one attempt with only the last response and the combined duration, the receiver sees more requests than the dashboard admits, and you now have two uncoordinated backoff layers. Keep resilience **per-adapter** (`a.ConfigureHttpClientBuilder(...)` on the adapters that want it) rather than global, or scope the global handler to exclude the `warp-webhooks` client.

### Stuck-delivery recovery

The executor claims each attempt atomically *before* the HTTP leg and commits the outcome (delivered / retry scheduled / exhausted) *after* it. If a transient database fault hits that second commit, the delivery is left `Pending` with no live executor job — the retry job was staged inside the failed transaction. Nothing scans `NextAttemptAt` in normal operation, so `StaleJobRecovery` (an existing server task) carries the one deliberate recovery sweep: any `Pending` delivery whose `NextAttemptAt` is more than `WarpConfiguration.WebhookStuckDeliveryGrace` (default 10 minutes) in the past gets a fresh executor job.

Duplicate jobs are guarded twice over. First, the sweep skips any candidate that still has a live executor job on the webhooks queue — a delivery whose workers are merely backlogged past the grace is left alone. Second, the guarded `NextAttemptAt` bump and the new executor job commit in **one transaction** — the server task's own — so a fault can never leave a bumped row without its job or a job without its bump, and consecutive sweeps never double-enqueue. Recovery is still at-least-once end to end: the recovered attempt may repeat a POST whose *outcome* was lost — the same delivery guarantee webhooks already carry.

## Redirects are not followed

Since 8.1 the `warp-webhooks` client does **not** follow redirects (`AllowAutoRedirect = false` on Warp's default `SocketsHttpHandler`). A `3xx` is the attempt's response: under the default success codes (any `2xx`) it is a failed attempt, retried per the schedule and recorded with its status code, and the redirect target never receives a request. Why:

- **A redirect can drop the payload.** `301`, `302` and `303` turn the POST into a GET, so the receiver at the new location gets no body and the attempt can still look successful.
- **Signature headers would be re-sent to the redirect target.** `webhook-id`, `webhook-timestamp`, `webhook-signature` and any custom headers would go to whatever host the `Location` names.

Point the subscription at the final URL instead. If a receiver really needs redirects, configure the client with `UseSocketsHttpHandler((handler, _) => handler.AllowAutoRedirect = true)`: a `SocketsHttpHandler` you configure keeps your choice (with `PublicAddressesOnly` on, every hop still goes through the guard). A primary handler of your own registered with `ConfigurePrimaryHttpMessageHandler` is used as is, with its own `AllowAutoRedirect` (which defaults to `true`).

## Success codes

By default any 2xx response marks the delivery `Delivered`. Pin `SuccessCodes` when the receiver's contract is narrower — e.g. a receiver that must return `200` and where a `202 Accepted` means "queued, not yet durable" and should be retried:

```csharp
new WebhookSend { SuccessCodes = [200] };   // a 202 now counts as a failed attempt
```

Any response outside the success set (or a thrown transport exception) is a failed attempt: retried if the schedule has entries left, otherwise exhausted.

**Any exception from the HTTP leg consumes a schedule slot.** The executor treats *every* throw while attempting the HTTP request as one failed attempt — this includes a rate-limit rejection (`AdapterRateLimitedException`) if the host attaches the adapters shared rate limiter to the `warp-webhooks` client. A throttled attempt is not free: it increments `AttemptCount` and burns the next entry of `RetrySchedule` exactly as a `500` or a connection failure would, and the delivery exhausts once the schedule is consumed regardless of *why* the attempts failed. This is deliberate — the delivery state machine does not distinguish a throttle from any other failure — but it means a rate limiter tuned too tight can exhaust a delivery on throttles alone. If you rate-limit the webhooks client, size `RetrySchedule` with that in mind (a throttle-heavy endpoint wants more, longer-spaced entries), or leave the `warp-webhooks` adapter unthrottled and shape delivery rate through the schedule instead.

## Attempt limits

Each attempt is bounded in time and in memory:

- **The response body is never buffered.** The request is sent with `HttpCompletionOption.ResponseHeadersRead`; the status code decides the outcome. When the adapter captures the response body (the `warp-webhooks` default), only its first `MaxCapturedBodySize` bytes (8 KiB by default) are read; the rest is never read or drained, and the response is disposed. When body capture is off, the body is not read at all. Before 8.1 the whole body was buffered (up to 2 GB) before the capture read.
- **`WebhookAttemptTimeout`** (default 30 seconds, on `WarpConfiguration`) bounds the send, the response headers and that capture read together. An attempt whose response headers do not arrive in time is a **failed attempt**: recorded with a `TimeoutException` on the attempt row and retried per `RetrySchedule` like any other failure. A slow receiver therefore costs a schedule slot; size the timeout for your slowest legitimate receiver. If the headers arrive in time but the body is still trickling in when the budget runs out, only the body capture is cut short: the status code has already decided the outcome, so a `2xx` stays `Delivered` (with no captured body) rather than sending the event again.

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.WebhookAttemptTimeout = TimeSpan.FromSeconds(10);
});
```

The timeout is a cancellation token, not `HttpClient.Timeout`, so a `warp-webhooks` client you configure yourself keeps its own settings. That client's `Timeout` (100 s by default) still applies to the send, so raise it as well before setting `WebhookAttemptTimeout` above 100 s. Host shutdown also cancels an attempt; the log line says which of the two happened ("timed out after ..." versus "was cancelled because the host is shutting down").

## Public addresses only

A webhook URL usually comes from a customer, and nothing about a URL stops it from pointing at `localhost`, a private network or a cloud metadata endpoint (`169.254.169.254`). **Since 8.1 `PublicAddressesOnly` is on by default**, in every process, with or without `AddWebhooks`. Turn it off only for local development (a receiver on `localhost`) and in tests that stub the client:

```csharp
opt.AddWebhooks(w => w.PublicAddressesOnly = false); // local development only
```

Only an explicit setting changes it: a later `AddWebhooks(...)` call that does not mention it leaves an earlier opt-out in place.

With it on, the `warp-webhooks` client's primary handler is a `SocketsHttpHandler` whose `ConnectCallback` resolves the host, drops every address that is not publicly routable and connects to exactly the addresses that passed. The address checked is the address connected to, so a DNS answer that changes between a check and the connection (DNS rebinding) cannot get through, and a redirect opens its connection through the same callback. A host that resolves to no public address fails the attempt with `WebhookAddressNotPublicException` ("Webhook host '…' does not resolve to a public address."); the resolved addresses are never logged or recorded. Like any failed attempt it is retried per the schedule, so validate URLs when subscriptions are created as well.

**Refused up front where it is certain.** In a process with the guard on, `SendAsync` throws `ArgumentException` for a URL whose host is a non-public **IP literal** (`http://10.0.0.5/…`, `http://[::1]/…`, `http://169.254.169.254/…`) or `localhost` (and any name under `.localhost`). A host **name** is accepted and checked at attempt time, because its DNS answer can change between send and attempt.

Refused ranges, from the IANA IPv4 and IPv6 special-purpose address registries (RFC 6890 and its successors): `0.0.0.0/8`, `10.0.0.0/8`, `100.64.0.0/10`, `127.0.0.0/8`, `169.254.0.0/16`, `172.16.0.0/12`, `192.0.0.0/24`, `192.0.2.0/24`, `192.88.99.0/24`, `192.168.0.0/16`, `198.18.0.0/15`, `198.51.100.0/24`, `203.0.113.0/24`, `224.0.0.0/4` and everything above it (multicast, `240.0.0.0/4`, broadcast); for IPv6 everything outside `2000::/3` (which covers `::`, `::1`, `fc00::/7`, `fe80::/10`, `fec0::/10`, `ff00::/8`, `64:ff9b:1::/48`, `100::/64`), plus `2001::/23`, `2001:db8::/32` and `3fff::/20`. IPv4-mapped (`::ffff:0:0/96`), NAT64 (`64:ff9b::/96`) and 6to4 (`2002::/16`) addresses are judged by the IPv4 address they embed. The classification is public as `WebhookAddressGuard.IsPublic(IPAddress)`, so you can run the same check when a subscription is saved.

**It applies per process.** The guard sits on the `warp-webhooks` client of the process that runs the attempt, which is whichever server picks up the executor job. Keep it on (the default) in every server that runs a worker; an opt-out on the API process that calls `SendAsync` only relaxes the up-front check there.

**Upgrading a client you configured yourself.** A `warp-webhooks` primary handler that is not a `SocketsHttpHandler` (an `HttpClientHandler` carrying a proxy or client certificates) is replaced by the guard on upgrade, with a Warning; move its settings into `UseSocketsHttpHandler`, or opt out.

**Tests that stub the client.** With the guard on, a stub primary handler (`ConfigurePrimaryHttpMessageHandler(() => new MyStub())`) is not a `SocketsHttpHandler`, so it is replaced by Warp's guarded handler (with the Warning below) and your test talks to the network instead of the stub. Set `PublicAddressesOnly = false` in those tests.

**With an HTTP proxy** (an explicit `Proxy`, or the system proxy from `HTTP_PROXY` / `HTTPS_PROXY`), the client connects to the proxy, not to the destination. The guard then checks the proxy's address — a proxy on a private address is refused — and **the proxy must enforce the policy** for the destination itself.

**Configuring the client yourself.** Configure the `warp-webhooks` client with `UseSocketsHttpHandler((handler, _) => ...)`; it composes with the guard and every setting you make is kept, except a `ConnectCallback` of your own, which the guard replaces. A primary handler of any other type (for example an `HttpClientHandler` you set with `ConfigurePrimaryHttpMessageHandler` to carry a proxy or client certificates), registered before or after `AddWebhooks`, cannot carry the guard: it is replaced by Warp's default `SocketsHttpHandler` (redirects off) and **its settings are lost**. Warp logs one Warning at startup naming the `warp-webhooks` client and the replaced handler type. Move those settings into `UseSocketsHttpHandler`. The startup check never stops the host: if your handler can only be built after startup, the guard is applied when it is first built.

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
- **Rotation.** The stamped secret is the one in force at `SendAsync`; deliveries still retrying keep it. To redeliver a settled delivery with a rotated key, use [`Redeliver` with `WebhookRedeliveryOptions.Secret`](#redelivery).
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

**Redeliver with a rotated secret.** A delivery signs with the secret stamped at `SendAsync`, so a plain redeliver after you rotate an endpoint's key signs with the old key. Pass the new secret and it replaces the stamped one in the same atomic settled→`Pending` flip:

```csharp
await webhookCommands.Redeliver(
    deliveryId,
    new WebhookRedeliveryOptions { Secret = subscription.SigningSecret }, // the rotated whsec_… key
    ct);
```

The new secret is validated like `WebhookSend.Secret` (at most 512 characters; base64 for `StandardWebhooks`), and a delivery sent with `Signing = None` rejects one with `ArgumentException`. Without `Secret` the overload behaves exactly like `Redeliver(deliveryId)`. A rejected redeliver (already `Pending`) leaves the stamped secret alone. The dashboard's Redeliver button keeps the stamped secret; the new secret is never returned by any read surface. The overload is a new `IWebhookCommandService` member with a default implementation, so an implementation of your own keeps compiling: without a secret it forwards to `Redeliver(deliveryId)`, and with one it throws `NotSupportedException` instead of signing with the old key.

A `Parked` or `Cancelled` delivery can be redelivered too (anything but `Pending`). A redeliver leaves the [last-attempt fields](#last-attempt) as they are until the new attempt overwrites them.

`Redeliver` returns a `WebhookRedeliveryResult` so callers map outcomes precisely: `Enqueued` (requeued), `NotFound` (unknown id), and `Rejected` (already `Pending` — it owns a live job). Because the redelivery enqueuer is part of Core and registered by `AddWarp`, `Redeliver` works from **any** process (dashboard-only / publisher-only included) — it stages the executor job through the outbox and a server elsewhere runs it. (The `Unavailable` result remains defined for defensiveness but is no longer reachable in a normal `AddWarp` process now that the enqueuer is always present.) The REST endpoint maps `Enqueued`→`200`, `NotFound`→`404`, and `Rejected`→`409`.

## Endpoint health

An **endpoint** is a delivery's `Group` (`WebhookDelivery.GroupName`): you decide what a group is (one subscription, one customer's URL, one tenant). Endpoint health (8.1) watches each group and, while one is down, holds its deliveries instead of exhausting them, then catches up by itself once the receiver is back. It is **opt-in, per sending process**:

```csharp
opt.AddWebhooks(w =>
{
    w.TrackEndpointHealth = true;
    w.OnEndpointHealthChanged<SubscriptionHealthHandler>(); // optional host callback
});
```

The choice is **stamped on each delivery at `SendAsync`** (`TrackEndpointHealth`), so it holds whichever server runs the attempt — set it where you send. **Deliveries without a `Group` are never tracked** and behave as before. One group's success never marks another group healthy, even when both point at the same URL.

**How it works:**

1. A tracked delivery that runs out of retries while its group is **Healthy** is `Exhausted` as usual (the exhausted callback fires) and marks the group **Failing**.
2. While a group is **Failing**, new deliveries are still sent and retried on their normal schedule. One that runs out of retries is **`Parked`** instead of `Exhausted`: it waits with no live job, no exhausted callback, and is not removed by retention.
3. Every `WebhookEndpointProbeInterval` (5 minutes by default) the `WebhookEndpointMonitor` server task **probes** each failing group: it re-sends the group's **oldest parked** delivery once. If that fails, the delivery parks again and the next probe is one interval later.
4. **Any success recovers the group**: the probe, a retry, or a new delivery to that group. The group turns **Healthy** in the same commit as the successful delivery, and the monitor then **releases** its parked deliveries back to `Pending` with a fresh attempt budget, **oldest first**, at most 200 per run (it re-runs at once while there are more). Released deliveries go through the normal queue, so recovery sends no faster than your workers already would.
5. A failing group with nothing parked (nothing ran out of retries since it started failing) has nothing to probe; it recovers on its next successful delivery. It never stays Failing because of a probe it could not make.

**Retry now.** `IWebhookCommandService.ProbeEndpoint(group)`, `POST {prefix}/api/webhooks/groups/retry?group=…` and the dashboard's **Retry now** button probe a failing group at once instead of waiting for the interval (`WebhookProbeResult`: `Probing`, `NotFound` — no health record, `NotFailing`, `NothingParked`, `Unavailable`).

**Callbacks and alerts.** Warp never disables anything: you own the subscription and decide. It tells you about three changes, after the change commits, through `IWebhookEndpointHealthHandler` (every member has a default no-op) and as [operational notifications](./operational-notifications.md):

| Change | Handler member | `WarpEventType` |
|---|---|---|
| A group started failing | `OnEndpointFailingAsync` | `WebhookEndpointFailing` |
| A failing group recovered | `OnEndpointRecoveredAsync` | `WebhookEndpointRecovered` |
| A group has been failing for `WebhookEndpointStillFailingAfter` (30 days by default), once per failing period | `OnEndpointStillFailingAsync` | `WebhookEndpointStillFailing` |

Both are best effort, like every operational notification: a crash between the commit and the call loses that call. The group's state on the dashboard and in `IWebhookQueryService.GetGroups(WebhookGroupBy.Endpoint)` is the record. A typical handler disables the subscription after the still-failing alert, or pages someone on the first failure of an important endpoint.

**Settings** (on `WarpConfiguration`): `WebhookEndpointProbeInterval` (5 min), `WebhookEndpointStillFailingAfter` (30 days), `WebhookEndpointMonitorInterval` (how often the monitor runs, 30 s; `null` turns it off, and parked deliveries then wait). The monitor runs on any server with a worker; the state lives in the `WebhookEndpoint` table, one row per group, created the first time a tracked delivery to it runs out of retries.

**Things to know.**

- Parked deliveries are kept until the group recovers or you [cancel](#cancelling-deliveries) them: they are excluded from the retention sweeps like `Pending` ones. A group that never recovers keeps its parked deliveries; the still-failing alert is your cue to act.
- A probe re-sends the parked delivery itself: the same payload, headers and event id (the subscriber's idempotency key), signed afresh like every attempt.
- Release order is the order the parked deliveries were created. Workers run deliveries in parallel, so the receiver can still see them out of order, as with any webhook delivery.

## Cancelling deliveries

`IWebhookCommandService.Cancel(id)` cancels a `Pending` or `Parked` delivery (8.1): it becomes `Cancelled`, is never attempted again unless redelivered, and is removed by retention like any settled delivery. A live executor job finds it no longer `Pending` and completes without sending; an attempt already in flight still reaches the receiver, but its outcome is not recorded and no retry follows. A settled delivery returns `Rejected`, an unknown id `NotFound`. `CancelGroup(group)` cancels every pending and parked delivery of a group and returns how many; the group's health is left as it is. REST: `POST {prefix}/api/webhooks/{id}/cancel` (`200`; `409` already settled; `404`) and `POST {prefix}/api/webhooks/groups/cancel?group=…` (`200 { "cancelled": n }`); the dashboard has **Cancel** on the delivery and **Cancel all** on the endpoint.

## Deduplication

Opt-in, per sending process (8.1):

```csharp
opt.AddWebhooks(w => w.DeduplicateByEventId = true);
```

`SendAsync` then returns the existing delivery's id, and stores and sends nothing, when a delivery of the **same `EventId` to the same `Url`** still exists, in any status, until retention deletes it. Different URLs (or the same event to another endpoint) are separate deliveries. It only means something when you set `EventId` yourself: a send without one gets a fresh id and never matches. The key is a SHA-256 of the event id and URL in a uniquely indexed `DedupeKey` column, set only on deduplicated sends. The check is a read before the insert; **two concurrent sends of the same pair race on the unique index, and the loser's save fails with a unique-constraint error** — treat that as "already sent". Redeliver is unaffected.

## Last attempt

Every delivery carries the outcome of its last attempt (8.1), stamped in the attempt's outcome commit and shown on the dashboard list and detail and in `IWebhookQueryService`:

- `LastStatusCode` — the HTTP status, or null when there was no response.
- `LastFailureReason` — null after a success, otherwise `WebhookFailureReason`: `Timeout`, `DnsFailure`, `ConnectionRefused`, `TlsError`, `ConnectionFailed`, `AddressNotPublic`, `UnsuccessfulStatus` (see `LastStatusCode`) or `Other` (building or signing the request failed, a rate limiter refused it).
- `LastAttemptAt` — when the attempt finished; for a `Delivered` row, when it was delivered.

A redeliver leaves them as they are until the next attempt overwrites them. Rows from before 8.1 have none.

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

`WebhookDelivery` rows carry an `ExpireAt` from `WarpConfiguration.WebhookDeliveryRetention` (default 30 days). `ExpirationCleanup` — an existing server task, so the worker hot path is untouched — deletes expired **settled** deliveries (`Delivered` / `Exhausted` / `Cancelled`) on each tick. `Pending` deliveries are excluded from the sweep: a long retry backoff can outrun retention, and an in-flight delivery must never vanish out from under its own scheduled executor job. `Parked` deliveries are excluded too: they wait for their endpoint to recover. The count cap (`WebhookDeliveryRetentionCount`) skips both as well. Redelivery also refreshes `ExpireAt`, so a requeued delivery gets a full retention window from the moment it is requeued. The `warp-webhooks` adapter's call-log retention is aligned to the same value by default, so attempt rows and delivery rows age together (they expire independently — there is no FK cascade — but the aligned default keeps them in step).

The `WebhookDelivery` table is added to the model **unconditionally** by `WarpModelCustomizer`, whether or not any host calls `AddWebhooks()` — same principle as the other addon entities, so the migration story is independent of which processes opt in.

## Multi-application provenance

In a shared-database deployment with [multi-application observability](./applications.md) enabled (`opt.ApplicationName` set), every `WebhookDelivery` row is stamped with the **producing application** — the app that called `SendAsync` — as a nullable `Application` column, so deliveries can be filtered by the app that sent them. When `ApplicationName` is unset the column is `null` and nothing changes.

## Telemetry

Three counters, in addition to the HTTP leg's spans/duration/error counters that come from the adapter layer (not duplicated here):

- `warp.webhooks.deliveries` (tags: outcome `delivered|exhausted|parked`)
- `warp.webhooks.attempts` (tags: outcome)
- `warp.webhooks.redeliveries`

## Dashboard

A **Webhooks** nav item (always shown — webhooks is a Core feature; the `webhooks` flag from `GET {prefix}/api/addons` gates on the always-registered redelivery enqueuer) opens two screens:

**Deliveries list** — summary tiles (deliveries, delivered %, pending, parked, exhausted) over a table of status, event, endpoint (the group value), reference, attempts, last result (status code or failure reason), next-attempt time, and created time — with filters for status, event type, reference, group, and date.

**Endpoint health** (8.1) — one row per endpoint (group): state (Healthy / Failing, or none for an untracked group), failing since, parked count, last success and last failure, success rate and p95 send-to-delivered time over the last 24 hours (from the delivery rows), with **Retry now** for a failing endpoint and **Cancel all** for its pending and parked deliveries.

**Delivery detail** — the self-contained contract exactly as stamped at `SendAsync` time: URL, group, reference, signing mode, retry schedule, success codes, attempt count, next-attempt/created/expires timestamps — the secret rendered only as `***` via the `HasSecret` flag and `Authorization`-class headers redacted (not caller-toggleable). Below it, the **per-attempt timeline** assembled from `AdapterCallLog` rows by `CorrelationId` — each attempt's outcome, status code, duration, and captured response body — the last attempt's status code, failure reason and time, a **Redeliver** action for anything not `Pending`, and **Cancel** for `Pending` and `Parked` deliveries.

The same data is served over Core query/command services (`IWebhookQueryService` / `IWebhookCommandService`, resolvable in any `AddWarp` process), so a host can equally expose it through its own permission-gated portal:

- `GET {prefix}/api/webhooks` — filtered, paged delivery list.
- `GET {prefix}/api/webhooks/{id}` — one delivery's contract + attempt timeline (secret reduced to `HasSecret`).
- `GET {prefix}/api/webhooks/summary` — tile counts.
- `GET {prefix}/api/webhooks/groups?by=endpoint|type` — per-group counts; by endpoint, with the health fields above.
- `POST {prefix}/api/webhooks/{id}/redeliver` — requeue a settled delivery (`200` requeued; `404` unknown; `409` already pending or no webhooks worker in this process).
- `POST {prefix}/api/webhooks/{id}/cancel` — cancel a pending or parked delivery (`200`; `409` already settled; `404` unknown).
- `POST {prefix}/api/webhooks/groups/cancel?group=…` — cancel a group's pending and parked deliveries (`200 { "cancelled": n }`).
- `POST {prefix}/api/webhooks/groups/retry?group=…` — probe a failing endpoint now (`200`; `404` no health record; `409` healthy, nothing parked, or no webhooks worker in this process).

## Not in scope

By design (host-owned or deferred):

- **Subscription / registration modeling and fan-out** — host-owned; Warp starts at `SendAsync`.
- **Automatic endpoint disabling** — the host decides, via `IWebhookDeliveryExhaustedHandler` or the [endpoint health](#endpoint-health) callbacks.
- **Per-endpoint keyed rate limiting of deliveries** — a deferred extension of the adapters shared limiter.
- **FIFO / ordering guarantees across deliveries** — independent deliveries execute independently.
- **Payload transformations, receiving-side webhooks, migration tooling for legacy delivery tables.**
