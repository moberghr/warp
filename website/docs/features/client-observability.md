---
sidebar_position: 18
---

import Screenshot from '@site/src/components/Screenshot';

# Client (frontend) observability

Client observability is the **browser-side** complement to Warp's server-side observability. Where [adapters](./adapters.md) observe the calls your app makes to other services and [endpoint observability](./endpoint-observability.md) observes requests made to your Warp HTTP endpoints, client observability observes what happens **in the browser**: unhandled errors, Core Web Vitals, explicit logs, and custom events — reported through a public ingest endpoint, attributed to an [application](./applications.md).

It's Sentry-lite for the front end, built on the same pipeline as the rest of Warp: a lossy ingest channel → flusher → rows + durable `Counter` aggregates → dashboard, with age+count retention and the DB/OTel/Both [sink model](./observability-sinks.md).

<Screenshot light="/img/screenshots/26-client.png" dark="/img/screenshots/26-client-dark.png" alt="Client page showing error rate, Core Web Vitals p75 tiles, top errors and a recent event stream" />

## What it captures

One primitive — a `ClientEvent` — with four types:

- **Error** — unhandled errors and promise rejections (message, stack, breadcrumb trail).
- **Vital** — Core Web Vitals (LCP, CLS, INP, FCP, TTFB).
- **Log** — explicit `warp.log(level, message, props)`.
- **Event** — custom `warp.track(name, props)`.

`log()` and `track()` are thin sugar over the same row; auto-captured errors and vitals are the same row with a reserved type. There is no second subsystem to learn.

> **Not** in scope: this is observability ("is the frontend broken/slow?"), not a product-analytics suite — no funnels, cohorts, retention curves, or session replay. For those, point a dedicated analytics SDK at your own backend.

## Setup

Two steps: register the pipeline inside the `AddWarp` lambda, and map the ingest endpoint.

```csharp
builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();

    opt.AddClientObservability(o =>
    {
        o.AddIngestKey("shop-web", "pk_live_abc123");   // a public write-only DSN key → the trusted app name
        o.AllowedOrigins.Add("https://shop.example.com");
        o.CaptureRemoteIp = false;                       // IP is PII — opt in explicitly
    });
});

var app = builder.Build();
app.UseRouting();
app.MapWarpClientObservability();   // POST /warp/ingest  +  GET /warp/ingest/client.js
```

Then include the shipped script on your pages:

```html
<script src="https://shop.example.com/warp/ingest/client.js"
        data-key="pk_live_abc123"
        data-release="1.4.2"
        data-sample-rate="1.0"></script>
```

That's it — the script auto-captures errors and web vitals, keeps a breadcrumb trail, and exposes `window.warp.log(...)` / `window.warp.track(...)`. It batches events and flushes via `fetch(keepalive)` / `sendBeacon` on an interval and on page hide.

## The public endpoint (a hostile client)

The ingest endpoint is browser-facing, so it's guarded accordingly:

- **Auth is a public DSN key.** You can't hide a secret in a bundle, so the key is *write-only*: it identifies the app and authorizes writes, never reads. The endpoint maps the key to a **trusted** application name server-side — the browser can't spoof another app's identity. Unknown key → 401.
- **CORS allowlist** — only configured origins may post; others get 403.
- **In-memory rate limit** per caller **IP** (the DSN key is public, so IP is the meaningful abuse dimension), checked before the body is read; never DB-backed — a browser beacon must not touch the database on the request path — plus hard payload-size and batch-size caps. Over-limit is dropped, never queued.
- **Lossy** — a full ingest buffer drops the event (and increments `warp.client.events.dropped`); the browser is never blocked or failed.

## Storage: bounded rows, durable trends

Two tiers, so the database never grows unbounded:

- **Raw `ClientEventLog` rows** are diagnostics, trimmed on **both** an age cap (`ClientEventLogRetention`, default 7d) and a row-count cap (`ClientEventLogRetentionCount`, default 100 000 per app), whichever hits first.
- **Aggregate trends** fold into `Counter → Statistic` (event counts by type, error rate, top error/event names, log counts by level, and web-vital **p75** via a duration-sum + histogram) so the numbers on the dashboard **survive raw-row cleanup**. Because the endpoint is public, *no* client-sent name is trusted to be bounded: error/event names and log levels collapse to `{other}` beyond a per-type cap, and vital names are matched against a fixed allowlist (the 5 Core Web Vitals) — so a hostile client can't explode the metric key space or the meter-tag cardinality. The raw row always keeps the real name.

## PII

Browser payloads are PII-dense, so capture is tiered and host-owned (§1.2): caller IP is off by default; property maps are redacted through a denylist (`authorization`/`cookie`/`password`/`token`/`secret`/…) and truncated; nothing is logged at Info+. **Consent/GDPR for behavioral data is the host's responsibility** — Warp stores only what your script sends.

## Sinks

`o.Sink` selects where events go ([observability sinks](./observability-sinks.md)): `Database` (default) / `Both` write rows + the dashboard works; `Otel` skips the DB entirely and the always-on meters (`warp.client.events`, `warp.client.vitals`, `warp.client.events.dropped`) carry the data to your collector.

## Multi-application

Each ingest key maps to an application name, so a frontend app appears on the [Applications](./applications.md) roster like any server process. Per-type event counts are sliced per app; vital percentiles and top-error lists are global in v1.

## Session correlation (client ↔ server)

The shipped script propagates two W3C headers on same-origin API calls, so a frontend session threads through the whole system:

- **`traceparent`** — a per-request **trace id**. The server adopts it into its request/job telemetry (`EndpointCallLog.TraceId`, `Job.TraceId`), so a single action's client request → endpoint → jobs share one trace and drill into the [job trace waterfall](./tracing.md).
- **`baggage: session.id=…`** — the [OTel `session.id`](https://opentelemetry.io/docs/specs/semconv/general/session/) for the whole browser session. The API stamps it onto `EndpointCallLog.Session`, and the publisher threads it onto every `Job.Session` it spawns (inherited by child jobs). It's also set as the `session.id` span attribute on the request and job spans, so any OTel backend can slice a trace by session.

The **Client → session timeline** page uses this: it merges the session's client events (errors, logs, vitals, custom events, and the API **requests** it made) with the **server endpoint calls** stamped with that session id, in one chronological view — then each request/server row links into the job trace waterfall. One session, client and server, on one page.

A trace id identifies *one action* end-to-end; a session id groups *all the actions* in a visit — they're complementary, and Warp propagates both.

## API

- `GET {prefix}/api/client/summary` (`?application=`) — counts, error rate, vital p75s, top errors/events, hourly history.
- `GET {prefix}/api/client/events` — recent event stream (filter by `application`/`type`/`session`, paged).
- `GET {prefix}/api/client/events/{id}` — a single event (stack, properties, breadcrumbs, redacted).
- `GET {prefix}/api/client/applications` — the apps reporting client events.

All resolve in any `AddWarp` process (dashboard-only / publisher-only included).
