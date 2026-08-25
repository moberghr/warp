---
sidebar_position: 4
---

import Screenshot from '@site/src/components/Screenshot';

# Webhooks

Outbound webhook deliveries. Warp owns everything after your code calls
`IWebhookDispatcher.SendAsync` — the retry schedule, exhaustion, signing and this page. Your app still
owns subscriptions, fan-out and payload building.

Four tiles summarise the fleet — **Deliveries**, **Delivered**, **Pending**, **Exhausted** — over a
delivery-statistics chart. The table lists one row per delivery: **Created**, **Event**, **Endpoint**,
**Reference**, **Status**, **Attempts** and **Next attempt**.

<Screenshot light="/img/screenshots/34-webhooks.png" dark="/img/screenshots/34-webhooks-dark.png" alt="Webhooks page with delivery totals and a table of deliveries by status and next attempt" />

The nav entry is always shown: webhooks are a Core feature, not an addon. Nothing appears until something
calls `SendAsync`.

## The delivery is the state machine

The executor job **always completes** — every attempt exception is caught and recorded as an attempt
failure. Webhook trouble therefore never shows up as failed jobs or pollutes the Jobs pages; it lives on
the delivery row, in one of three states:

| Status | Meaning |
|---|---|
| **Pending** | Attempts remain; `Next attempt` is when the next one fires |
| **Delivered** | An attempt returned a success code |
| **Exhausted** | The retry schedule ran out; your `IWebhookDeliveryExhaustedHandler` was invoked |

## Delivery detail

A delivery shows its URL, headers, payload and signing mode, plus the **attempt timeline**. Attempts are
recorded as adapter calls against the built-in `warp-webhooks` adapter, so each one carries the response
body and timing.

That timeline needs `opt.AddAdapters()` — DB recording of call logs is opt-in. Without it the delivery
still works completely (retries, exhaustion, redelivery, status); only the per-attempt HTTP detail is
empty.

**Redeliver** requeues a settled delivery (`Delivered` or `Exhausted`) as `Pending` with an immediate
attempt. A `Pending` delivery is rejected rather than double-sent.

## Secrets

Signing secrets are stored on the delivery row so an in-flight delivery is self-contained and a later
config deploy can't reshape it. Every read surface — this page included — reduces the secret to a
`HasSecret` flag and redacts `Authorization`-class headers. That is not caller-toggleable. If storing
secrets at rest is unacceptable in your environment, use `Signing = None` and sign inside your own payload.

## Adoption note

A Warp **server** must be running somewhere to drain the `warp:webhooks` queue. Any `AddWarpServer` with a
worker does it — there is no per-process opt-in to forget.

## See also

- [Webhooks](/docs/features/webhooks) — dispatch, retry schedules, signing, exhaustion callbacks.
