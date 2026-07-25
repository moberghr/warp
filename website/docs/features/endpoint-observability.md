---
sidebar_position: 12
---

import Screenshot from '@site/src/components/Screenshot';

# Inbound Endpoint Observability (Warp.Http)

Endpoint observability is the **inbound** mirror of [outbound adapters](./adapters.md): where adapters observe the calls your app *makes to* other services, endpoint observability observes the requests *made to* your app's Warp-exposed HTTP endpoints — **who called** (IP, user-agent, authenticated user), how long it took, the status/outcome, latency percentiles, the jobs the request spawned, and — opt-in — the request and response headers and bodies.

It observes **only Warp HTTP endpoints** (handlers exposed via `MapWarpHttp`). It never observes your own MVC/minimal-API controllers, the dashboard, health checks, or static files — the middleware no-ops for anything that isn't a Warp-mapped endpoint.

<Screenshot light="/img/screenshots/23-endpoints-list.png" dark="/img/screenshots/23-endpoints-list-dark.png" alt="Endpoints list showing each observed endpoint with call volume, error rate and average latency" />

## Setup

Two calls: register the recording pipeline inside the `AddWarp`/`AddWarpServer` lambda, and install the middleware after routing.

```csharp
builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();

    opt.AddEndpointObservability(o =>
    {
        o.CaptureRequestBodies = CaptureMode.OnFailure;   // None / OnFailure / Always
        o.CaptureResponseBodies = CaptureMode.OnFailure;
        o.CaptureHeaders = CaptureMode.OnFailure;

        // Optional low-cardinality caller group (channel / client / tenant) — a metrics dimension:
        o.GroupSelector = ctx => ctx.Request.Headers["X-Client-Id"].FirstOrDefault();

        // Optional free-form per-request tags (NOT a metrics dimension — high cardinality is fine):
        o.Enrich = (ctx, tags) =>
        {
            if (ctx.User.FindFirst("sub")?.Value is { } userId)
            {
                tags["userId"] = userId;
            }

            tags["scheme"] = ctx.Request.Scheme;
        };
    });
});

var app = builder.Build();

app.UseRouting();                 // so the matched endpoint + its identity are resolved
app.UseWarpHttpObservability();   // observe Warp endpoints; no-ops for everything else
app.MapWarpHttp();
```

`UseWarpHttpObservability()` must run **after** `UseRouting()` (so the matched endpoint is known) and requires `AddEndpointObservability()` to have registered the recorder.

## What it records

Each request to a Warp endpoint produces one `EndpointCallLog` row (subject to `RecordCalls` / `SampleRate`), carrying:

- **Identity** — HTTP method + route *template* (`GET /orders/{id}`), and the handler/route operation name. The route template is the identity, so there is no runtime path-cardinality explosion; inline constraints (`{id:int}`) are normalized away (`{id}`).
- **Caller** — remote IP (`Connection.RemoteIpAddress`, or the first `X-Forwarded-For` hop when `UseForwardedForIp` is on), user-agent, and the authenticated user (`HttpContext.User.Identity.Name`).
- **Timing + outcome** — duration, final status code, and outcome (`Failed` when the status is ≥ 500 or an unhandled exception propagated, else `Success`).
- **Trace id** — built the *same way* jobs build theirs (`new Guid` over the 32-hex trace id), so a request and the jobs it spawns share a trace and link both ways (see [Trace drill-down](#trace-drill-down)).
- **Tags** — the free-form key/value pairs your `Enrich` callback added.
- **Captured payloads** — request/response headers and bodies per the capture tiers, redacted and truncated (see below).

## Latency percentiles

Beyond average latency, the detail page reports **p90 / p95 / p99**. Percentiles are computed from a fixed-bucket latency histogram (`5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000 ms`) written to the same `Counter`→`Statistic` aggregate pipeline as the counts — so, like the counts, they are **exact-over-all-calls and survive log deletion** (they are not sampled from the retained rows). The reported percentile is the upper edge of the bucket the rank falls in.

<Screenshot light="/img/screenshots/24-endpoint-detail.png" dark="/img/screenshots/24-endpoint-detail-dark.png" alt="Endpoint detail with call volume, error rate, latency percentiles, per-caller table and recent calls" />

## Trace drill-down

Because an inbound request and the jobs it enqueues share one trace id, the dashboard links them **both directions**:

- **Request → jobs.** A call's drawer lists the **Related jobs** it spawned, each with its current state, plus a **View full trace →** link into the trace view.
- **Job → request.** A job spawned inside a request shows an **Origin** card on its detail page (`Request: POST /orders`, the authenticated user, and a link back to the originating call), so you can walk from a stuck background job back to the HTTP request that started it.

<Screenshot light="/img/screenshots/25-endpoint-call-drawer.png" dark="/img/screenshots/25-endpoint-call-drawer-dark.png" alt="Call drawer with redacted request/response headers and bodies, enrichment tags, and the related jobs the request spawned" />

<Screenshot light="/img/screenshots/26-job-detail-origin.png" dark="/img/screenshots/26-job-detail-origin-dark.png" alt="Job detail Origin card linking back to the inbound request that spawned the job" />

## Capture tiers, redaction, truncation (PII)

`CaptureRequestBodies` / `CaptureResponseBodies` / `CaptureHeaders` are each `None` / `OnFailure` / `Always`, defaulting to `OnFailure`. Bodies are captured through a bounded pass-through stream (a large or streaming response is truncated at `MaxCapturedBodySize`, default 8 KB, while the client still receives the full response). Headers are truncated at `MaxCapturedHeaderSize` (default 4 KB).

**Caller metadata (IP / user-agent / user) and tags are PII** (§1.2). They are captured by default because they are the point of the feature, but: header values on the `RedactedHeaders` denylist (prepopulated with `Authorization` / `Proxy-Authorization` / `Cookie` / `Set-Cookie` / `X-Api-Key`, fully user-overridable via `Add`/`Remove`/`Clear`) are stored as `***`, and captured payloads are never logged at Info+. Do not put secrets in `Enrich` tags. Disable body/header capture (`CaptureMode.None`) for endpoints handling sensitive data.

## Volume controls

Three orthogonal knobs bound how much you store, none of which affect the metrics (counts, error rate, latency percentiles are always recorded for **every** call):

- **`RecordCalls`** — `All` (default) writes a row per call; `FailuresOnly` writes rows only for failures. The coarse on/off for chatty endpoints.
- **`SampleRate`** (0.0–1.0, default 1.0) — the fraction of **successful** requests to keep a row for. Failures are always kept. `0.1` keeps ~10% of successful rows while the aggregates stay exact — the Sentry-style knob for high-traffic endpoints where you want representative payloads, not all of them.
- **`ForceCapture`** — a `Func<HttpContext, bool>` evaluated at request start. When it returns `true`, the request is captured at **full fidelity** (bodies + headers, even on success and even if the tier is `None`/`OnFailure`) and its row is **always written**, bypassing both `SampleRate` and `RecordCalls`. Use it for targeted diagnostics — a debug header, a specific caller, a canary tenant:

```csharp
o.SampleRate = 0.1;   // keep 10% of successful rows
o.ForceCapture = ctx => ctx.Request.Headers.ContainsKey("X-Debug-Capture");
```

## Buffer + flusher (why logs can lag)

Rows are handed to a **bounded in-memory channel** and drained to the database by a background **flusher** — recording never touches the DB on the request path, so it never blocks or fails a request. This means rows appear in the dashboard with a short lag (the flush interval), which is the intended trade-off: *"it's not important if logs take some time to appear."*

- `WarpConfiguration.CallLogBufferCapacity` (default 10,000) — the channel size, shared-shape with the adapter recorder. If the buffer fills faster than the flusher drains (a genuine burst beyond capacity), the **oldest** overflow records are dropped and counted — the aggregates stay exact, only some raw rows are lost. Raise it for bursty high-volume observability; lower it to cap memory. (A full 10k buffer is ≈ 2 MB of metadata-only records; with `Always` body+header capture on large payloads it can reach hundreds of MB — size it against your capture tiers, or lean on `SampleRate`.)
- `WarpConfiguration.CallLogFlushBatchSize` (default 500) — how many records the flusher folds into one scope + `SaveChanges`.

## Metrics survive log deletion

Call counts, error rate, **average latency**, and the **latency percentiles** are read from aggregated `Counter`→`Statistic` rows (a duration-sum counter backs the average, a bucket histogram backs the percentiles), so they persist after the raw `EndpointCallLog` rows are cleaned up — the same model jobs and adapters use. The recent-calls list and per-caller last-failure timestamp read the retained rows and degrade to empty/null once logs age out.

## Routing to OpenTelemetry instead of the database

By default the per-request detail lands as `EndpointCallLog` rows and the aggregates as `Counter`→`Statistic` rows in your database. `opt.AddEndpointObservability(o => o.Sink = RecordingSink.Otel)` routes the captured detail onto the ambient ASP.NET request span (as `warp.endpoint.*` attributes) and relies on the always-on `warp.endpoint.*` meters for the aggregates — **no call-log rows and no `Counter` writes**, keeping the database out of the hot path. `Both` does both; `Database` (default) is unchanged. See [Observability sinks](./observability-sinks.md).

## Retention

`EndpointCallLog` rows are cleaned up by `ExpirationCleanup` on **both** an age cap and a count cap, whichever trims first:

- `WarpConfiguration.EndpointCallLogRetention` (default 7 days) — the middleware stamps `ExpireAt`.
- `WarpConfiguration.EndpointCallLogRetentionCount` (default null = disabled) — keep at most N rows per endpoint (method + route template), deleting the oldest.

## Multi-application provenance

In a shared-database deployment with [multi-application observability](./applications.md) enabled (`opt.ApplicationName` set), every `EndpointCallLog` row is stamped with the **producing application** — the app that served the request — as a nullable `Application` column, and per-application endpoint metrics accrue under a disjoint counter-key namespace. `Application` also becomes part of **endpoint identity**, so the same route template served by two applications stays two distinct aggregates rather than collapsing into one. The dashboard's global application filter then scopes the Endpoints surfaces to one app. When `ApplicationName` is unset the column is `null` and identity is unchanged.

## Dashboard

The **Endpoints** nav appears when `GET {prefix}/api/addons` reports `endpoints: true` (only where `AddEndpointObservability()` ran). The list shows each observed endpoint with its call volume, error rate, and average latency. The detail page adds latency percentiles, a per-caller (group) table, and a paged recent-calls list; each call opens a drawer with the caller metadata, enrichment tags, the related jobs the request spawned, and the captured (redacted) headers and bodies.

Dashboard-only / publisher-only processes resolve `IEndpointQueryService` (registered by `AddWarp` itself) and serve `/api/endpoints*` without running the middleware — the tables are always in the schema.

## Not in scope

Endpoint observability records diagnostics, not an audit trail (same stance as `AdapterCallLog` / `JobLog`): the raw call rows are lossy under load (a full buffer drops the record, never blocking the request), and there is no per-request guaranteed delivery. The **aggregate metrics** (counts, error rate, latency percentiles) are exact regardless. For high-volume export, route the surface to OpenTelemetry with `Sink = RecordingSink.Otel` (see [Observability sinks](./observability-sinks.md)) — or run the standard OpenTelemetry ASP.NET Core instrumentation alongside it.
