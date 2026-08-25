---
sidebar_position: 2
---

import Screenshot from '@site/src/components/Screenshot';

# Endpoints

Inbound HTTP requests made *to* your Warp-exposed handlers. The outbound mirror is
[Adapters](/docs/dashboard/traffic/adapters).

It observes **only Warp HTTP endpoints** (handlers exposed via `MapWarpHttp`) — never your own
controllers, the dashboard, health checks or static files. Identity is the HTTP method plus the route
*template* (`GET /orders/{id}`), so a path parameter never inflates the row count.

<Screenshot light="/img/screenshots/23-endpoints-list.png" dark="/img/screenshots/23-endpoints-list-dark.png" alt="Endpoints list showing each observed endpoint with call volume, error rate and average latency" />

The list gives **Calls**, **Error %**, **Avg latency** and a **Health** pill per route, over a chart of
all endpoints combined.

## Detail page

<Screenshot light="/img/screenshots/24-endpoint-detail.png" dark="/img/screenshots/24-endpoint-detail-dark.png" alt="Endpoint detail with call volume, error rate, latency percentiles, per-caller table and recent calls" />

Beyond average latency the detail page reports **p90 / p95 / p99**, computed from a fixed-bucket histogram
folded through the same `Counter` → `Statistic` pipeline as the counts — so they are exact over all calls
and survive log deletion, not sampled from whatever rows remain.

A **callers** table breaks the same numbers down by group (channel, client, tenant — whatever your
`GroupSelector` returns), with each caller's last failure.

## Call detail

<Screenshot light="/img/screenshots/25-endpoint-call-drawer.png" dark="/img/screenshots/25-endpoint-call-drawer-dark.png" alt="Call detail with redacted request and response headers and bodies, enrichment tags, and the related jobs the request spawned" />

An individual call shows its captured request and response (headers redacted through the denylist, bodies
truncated), any enrichment tags, and — the useful part — **Related jobs**: everything enqueued during that
request, reached by shared trace id, with a link into the full trace.

The reverse direction works too. A job spawned inside a request shows an **Origin** card on its detail page
naming the request and the user, linking back to the call above:

<Screenshot light="/img/screenshots/26-job-detail-origin.png" dark="/img/screenshots/26-job-detail-origin-dark.png" alt="Job detail Origin card linking back to the inbound request that spawned the job" />

## What counts as an error

`Failed` means the final wire status was 5xx, or an exception escaped after the response started. A 4xx is
a client error and stays out of the error rate, so the number keeps meaning "we broke it". 4xx responses
still form [issues](/docs/dashboard/health/issues), filtered out of that view by default.

## Nav visibility

The page appears once `AddEndpointObservability()` is registered. The query service itself is registered by
`AddWarp`, so a dashboard-only process can serve the data without running the middleware.

## See also

- [Endpoint observability](/docs/features/endpoint-observability) — setup, capture modes, retention.
- [Adapters](/docs/dashboard/traffic/adapters) — the outbound counterpart.
