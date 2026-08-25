---
sidebar_position: 1
---

import Screenshot from '@site/src/components/Screenshot';

# Adapters

Outbound service dependencies — every call Warp makes *to* another service. The inbound mirror is
[Endpoints](/docs/dashboard/traffic/endpoints).

The list gives one row per adapter: a sparkline **Trend**, **Calls**, **Error %**, **Avg latency**,
a **Health** pill, and **Last seen**. Three tiles above it summarise the fleet — adapter count, recorded
calls, and overall error rate.

<Screenshot light="/img/screenshots/33-adapters.png" dark="/img/screenshots/33-adapters-dark.png" alt="Adapters list with per-adapter trend, calls, error rate, latency and health" />

The page is always available (`IAdapterQueryService` is registered by `AddWarp` itself), so a
dashboard-only process serves it without running a server. Rows only appear once something calls
`IWarpAdapters.BeginCall` — directly, or through the `Warp.Adapters.Http` / `Warp.Adapters.Refit`
bindings.

## Detail page

Click an adapter for its detail:

- **Total calls**, **Error rate** and **Avg latency** tiles, plus a performance chart over time.
- **Operations** — the API-contract axis (`GetOrders`, `payment.completed`). An operation red across all
  groups points at a caller-side bug.
- **Groups** — the runtime who/where axis (destination, tenant, region). A group red across all operations
  points at the counterparty. The card is labelled with the adapter's own `groupLabel`.
- **Recent calls** — individual call rows, each opening a call detail with captured headers and bodies.
- **Records** — what this adapter captures (request body, response body, headers) and its rate limit.

Counts, error rate and average latency come from durable `Counter` → `Statistic` aggregates, so they
survive call-log cleanup. Only the recent-calls list and last-failure timestamps read raw rows, and those
degrade to empty once retention sweeps them.

## Capture and redaction

Metadata is always recorded. Request bodies, response bodies and headers are each independently
`None` / `OnFailure` / `Always`, truncated, and passed through the user-owned `RedactedHeaders` denylist —
`Authorization`, `Cookie` and friends show as `[REDACTED]`. Payloads may contain user data, so treat this
page as PII-bearing and set the capture modes deliberately.

## See also

- [Adapters](/docs/features/adapters) — registration, policy, shared rate limiting, and the three-axis
  identity model.
- [Endpoints](/docs/dashboard/traffic/endpoints) — the inbound counterpart.
