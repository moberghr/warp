---
sidebar_position: 12
---

# Inbound Endpoint Observability (Warp.Http)

Endpoint observability is the **inbound** mirror of [outbound adapters](./adapters.md): where adapters observe the calls your app *makes to* other services, endpoint observability observes the requests *made to* your app's Warp-exposed HTTP endpoints — **who called** (IP, user-agent, authenticated user), how long it took, the status/outcome, and — opt-in — the request and response headers and bodies.

It observes **only Warp HTTP endpoints** (handlers exposed via `MapWarpHttp`). It never observes your own MVC/minimal-API controllers, the dashboard, health checks, or static files — the middleware no-ops for anything that isn't a Warp-mapped endpoint.

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
        // Optional low-cardinality caller group (channel / client / tenant) for per-caller stats:
        o.GroupSelector = ctx => ctx.Request.Headers["X-Client-Id"].FirstOrDefault();
    });
});

var app = builder.Build();

app.UseRouting();                 // so the matched endpoint + its identity are resolved
app.UseWarpHttpObservability();   // observe Warp endpoints; no-ops for everything else
app.MapWarpHttp();
```

`UseWarpHttpObservability()` must run **after** `UseRouting()` (so the matched endpoint is known) and requires `AddEndpointObservability()` to have registered the recorder.

## What it records

Each request to a Warp endpoint produces one `EndpointCallLog` row (subject to `RecordCalls`), carrying:

- **Identity** — HTTP method + route *template* (`GET /orders/{id}`), and the handler/route operation name. The route template is the identity, so there is no runtime path-cardinality explosion; inline constraints (`{id:int}`) are normalized away (`{id}`).
- **Caller** — remote IP (`Connection.RemoteIpAddress`, or the first `X-Forwarded-For` hop when `UseForwardedForIp` is on), user-agent, and the authenticated user (`HttpContext.User.Identity.Name`).
- **Timing + outcome** — duration, final status code, and outcome (`Failed` when the status is ≥ 500 or an unhandled exception propagated, else `Success`).
- **Captured payloads** — request/response headers and bodies per the capture tiers, redacted and truncated (see below).

## Capture tiers, redaction, truncation (PII)

`CaptureRequestBodies` / `CaptureResponseBodies` / `CaptureHeaders` are each `None` / `OnFailure` / `Always`, defaulting to `OnFailure`. Bodies are captured through a bounded pass-through stream (a large or streaming response is truncated at `MaxCapturedBodySize`, default 8 KB, while the client still receives the full response). Headers are truncated at `MaxCapturedHeaderSize` (default 4 KB).

**Caller metadata (IP / user-agent / user) is PII** (§1.2). It is captured by default because it is the point of the feature, but: header values on the `RedactedHeaders` denylist (prepopulated with `Authorization` / `Cookie` / `X-Api-Key` etc., fully user-overridable) are stored as `***`, and captured payloads are never logged at Info+. Disable body/header capture (`CaptureMode.None`) for endpoints handling sensitive data.

`RecordCalls = FailuresOnly` writes a row only for failed requests (the volume knob for chatty endpoints) — success **counters** are still recorded, so error rates keep a real denominator.

## Metrics survive log deletion

Call counts, error rate, and **average latency** are read from aggregated `Counter`→`Statistic` rows (a duration-sum counter backs the average), so they persist after the raw `EndpointCallLog` rows are cleaned up — the same model jobs and adapters use. The recent-calls list and per-caller last-failure timestamp read the retained rows and degrade to empty/null once logs age out.

## Retention

`EndpointCallLog` rows are cleaned up by `ExpirationCleanup` on **both** an age cap and a count cap, whichever trims first:

- `WarpConfiguration.EndpointCallLogRetention` (default 7 days) — the middleware stamps `ExpireAt`.
- `WarpConfiguration.EndpointCallLogRetentionCount` (default null = disabled) — keep at most N rows per endpoint (method + route template), deleting the oldest.

## Dashboard

The **Endpoints** nav appears when `GET {prefix}/api/addons` reports `endpoints: true` (only where `AddEndpointObservability()` ran). The list shows each observed endpoint with its call volume, error rate, and average latency. The detail page adds a per-caller (group) table and a paged recent-calls list; each call opens a drawer with the caller metadata and the captured (redacted) headers and bodies.

Dashboard-only / publisher-only processes resolve `IEndpointQueryService` (registered by `AddWarp` itself) and serve `/api/endpoints*` without running the middleware — the tables are always in the schema.

## Not in scope

Endpoint observability records diagnostics, not an audit trail (same stance as `AdapterCallLog` / `JobLog`): recording is lossy under load (a full channel drops the record, never blocking the request), and there is no per-request guaranteed delivery. For high-volume export, use the OpenTelemetry ASP.NET Core instrumentation alongside it.
