---
sidebar_position: 12
---

# Outbound Adapters (Warp.Adapters)

An **adapter** is a named outbound dependency — a vendor API, a SOAP service, a webhook receiver, a GraphQL endpoint. `Warp.Adapters` makes every call you *make to* one of them first-class in Warp: named, timed, telemetered, captured on failure, and visible in the dashboard — with **cluster-shared rate limiting** that per-process Polly cannot provide.

The value proposition is the glue you stop hand-writing: logging handlers, ad-hoc metrics, per-project retry config, and — the part almost nobody builds — failure forensics. When a payment vendor starts returning 500s at 3am, the adapter call log already has the redacted request, the response, the exception, and the trace id.

## Inbound vs outbound — `Warp.Http` is not `Warp.Adapters.Http`

The two package names look alike and do opposite things. Keep them straight:

| Package | Direction | What it does |
|---|---|---|
| **`Warp.Http`** | **Inbound** | Exposes your Warp `IRequest<T>` handlers *as* ASP.NET HTTP endpoints — traffic coming **in**. See [HTTP Endpoints](./http.md). |
| **`Warp.Adapters.Http`** | **Outbound** | Observes the HTTP calls your app *makes to* other services — traffic going **out**. This page. |

If you are annotating a handler to serve a request, that is `Warp.Http`. If you are calling someone else's API, that is `Warp.Adapters.Http`.

## Three layers

1. **Protocol-agnostic core** (`Warp.Core.Adapters`) — a call-scope API (`BeginCall` → `Succeed`/`Fail`) that works for anything: SOAP proxies, vendor SDKs, gRPC, a raw socket. Manual scopes, no HTTP assumed.
2. **HTTP binding** (`Warp.Adapters.Http`) — a `DelegatingHandler` that creates scopes automatically for `IHttpClientFactory` clients. Polly (`Microsoft.Extensions.Http.Resilience`) handles retry/timeout; the raw `IHttpClientBuilder` is always reachable.
3. **Refit sugar** (`Warp.Adapters.Refit`) — one-call registration for an existing Refit interface, with operation names read from Refit's `RestMethodInfo`. Refit is referenced only by this package.

## Setup

Adapters need only `AddWarp<TContext>` — **no server, no worker**. They run in any process that has a Warp DbContext: an API host, a console app, a function, something that already runs a different job system entirely. Recording persists through a hosted flusher on your `TContext`; nothing about adapters requires you to run Warp's job worker.

```csharp
builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddAdapters();          // registers the recorder + flusher (opt-in)
});
```

`AddAdapters()` gates **DB recording only**. Telemetry (spans + meters, below) is emitted unconditionally whether or not you call it — the two adapter tables are always in the schema regardless (see [Always in the schema](#always-in-the-schema)).

### HTTP adapter

```csharp
builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddAdapters();

    opt.AddAdapter("acme-payments", a =>
    {
        a.BaseUrl = "https://api.acme.example";   // optional — see below
    });
});

// Inject the named client anywhere:
public sealed class PaymentClient(IHttpClientFactory factory)
{
    public async Task<HttpResponseMessage> Charge(ChargeRequest body, CancellationToken ct)
    {
        var http = factory.CreateClient("acme-payments");
        return await http.PostAsJsonAsync("/v1/charges", body, ct);
    }
}
```

Every call through that client is now one recorded adapter call: timed, outcome-mapped (thrown exception or non-success status → `Failed`), telemetered, and (under the default `RecordCalls = All`) written as an `AdapterCallLog` row.

### Refit adapter

```csharp
opt.AddAdapter<IAcmePaymentsApi>("acme-payments", a => a.BaseUrl = "https://api.acme.example");
```

Your existing Refit interface, DTOs, and auth `DelegatingHandler`s are unchanged. Operation names come from the interface method names (`ChargeCard`, `RefundCharge`) — never the URL heuristic, never subject to the cardinality guard. `RefitSettings` (custom serializers for XML-over-REST, auth token getters, exception factories) pass through as an optional third argument.

### Manual scope (non-HTTP transports)

Vendor SDKs, SOAP proxies, anything without an `HttpClient` — inject `IWarpAdapters` and wrap the call:

```csharp
public sealed class LegacySoapClient(IWarpAdapters adapters, VendorSoapProxy proxy)
{
    public async Task<Quote> GetQuote(QuoteRequest req, CancellationToken ct)
    {
        using var call = adapters.BeginCall("legacy-quotes", operation: "GetQuote");
        try
        {
            var quote = await proxy.GetQuoteAsync(req, ct);
            call.Succeed();
            return quote;
        }
        catch (Exception ex)
        {
            call.Fail(ex);
            throw;
        }
    }
}
```

Identical telemetry, capture, and dashboard treatment as an HTTP adapter. `Succeed()`/`Fail(ex)` are explicit and encouraged; disposing without an outcome defaults to failed if an exception is unwinding, success otherwise.

## The three axes: adapter, group, operation

This is the model everything else hangs off. Get it right once and the dashboard, the metrics, and the granularity decisions all fall out.

- **Adapter = policy + health boundary** (registration-time). What you retry, what you time out, what you rate-limit, and what health/SLO you track as one unit. Chosen when you register.
- **Group = runtime who/where.** The destination endpoint, the tenant, the shop, the region, the webhook receiver. A *runtime value* — unbounded, always explicit, never heuristic-derived.
- **Operation = what.** The API-contract axis: `GetOrders`, `payment.completed`. Compile-time known, bounded, the same set for every group.

### Operation vs group — the definitional distinction

Operation is the *API-contract axis*: what the call is (`GetOrders`, `payment.completed`) — compile-time known, bounded, the same set for every group. Group is the *data axis*: for whom / to which instance (shop, merchant, region, key, webhook receiver) — runtime, unbounded, doesn't change what the call structurally is.

**Litmus test:** if swapping the value changes the request's structure (route, verb, payload shape) → operation; if it only changes where it goes or on whose behalf → group.

The two axes exist because they diagnose different failure modes:

| Failure shape | Diagnosis |
|---|---|
| An **operation red across all groups** | caller-side bug — malformed payload, schema drift |
| A **group red across all operations** | counterparty problem — dead token, downed receiver |
| **Everything red** | the adapter/vendor itself |

The machinery mirrors the semantics: operations are bounded → meter tags allowed, heuristic derivation allowed; groups are unbounded → excluded from metrics by default, always explicit, higher cardinality cap.

### The granularity rule

**Start with one adapter per vendor.** Split into multiple adapters only when *policies* genuinely diverge (retry on idempotent reads vs no retry on payment writes) or *health SLOs* diverge — a registration-time boundary. When the dimension you want to slice by is a *runtime value* (which tenant, which endpoint, which region), that is a **group**, not a new adapter.

Because **the adapter name is cluster-wide identity**: the same name means stats merge and shared rate limits are shared *by design*. Two genuinely different dependencies must get two different names; one dependency you happen to call for many tenants stays one adapter with a group per tenant.

### Groups in practice

Groups are always explicit — via the core scope (`BeginCall(adapter, operation, group)` / `call.SetGroup(...)`) or, on the HTTP binding, a request option or ambient scope:

```csharp
// Request option (reliable — flows with the request):
var req = new HttpRequestMessage(HttpMethod.Post, url)
    .WithWarpOperation("order.updated")
    .WithWarpGroup(destinationEndpoint);      // who/where

// Or ambient (convenient — does NOT flow across manually created threads):
using (WarpAdapterCall.Group(shopDomain))
using (WarpAdapterCall.Operation("order.updated"))
{
    await http.PostAsJsonAsync(url, payload, ct);
}
```

A group is recorded on the span attribute `warp.adapter.group`, on the call-log row, and on the **counter keys** — so per-group success *and* failure counts exist and the dashboard shows real per-group error rates. It is **excluded from meter tags** unless the adapter opts in with `IncludeGroupInMetrics = true` (do this only for bounded group sets). Set `GroupLabel` (default `"Group"`) to name the dimension in the dashboard — `"Endpoint"`, `"Shop"`, `"Region"`. An adapter with no groups behaves exactly as before groups existed.

The webhook fan-out case falls straight out of this: one adapter, `BaseUrl` unset, group = destination endpoint, operation = event type. Per-endpoint call/error-rate stats come from the generic groups mechanism — no webhook-specific machinery.

## Operation naming

For the HTTP binding, the operation name resolves by precedence:

1. **Request option** — `request.WithWarpOperation("ChargeCard")` (highest; flows with the request).
2. **Ambient scope** — `using (WarpAdapterCall.Operation("ChargeCard"))` (`AsyncLocal`; does not cross manually created threads).
3. **URL heuristic** — `METHOD /path` with numeric and GUID segments collapsed to `{id}` (`POST /v1/charges/{id}/refund`).

Refit adapters get the method name automatically (a fourth, highest-priority source in practice). Explicit is always better for GraphQL (one URL, many operations) and SOAP (`WithWarpOperation(soapAction)` in the shared transport method covers an entire vendor in ~2 lines).

### Cardinality guards

Fan-out adapters registered without explicit operation names can explode counter/metric cardinality. Two guards cap it:

- **`MaxDistinctOperations`** (default 50) — once an adapter has recorded this many distinct **heuristic-derived** operation names, further heuristic names record under a literal `{other}` plus a one-time warning. **Explicitly-supplied names are never collapsed.**
- **`MaxDistinctGroups`** (default 500) — the same guard for group values, which are runtime data and unbounded by nature.

## `BaseUrl` is optional

When unset — dynamic per-tenant hosts, webhook fan-out, per-service SOAP endpoints — requests carry absolute URIs and flow through the *identical* handler pipeline: operation naming, capture, recording, telemetry all work unchanged. Observability does not depend on a fixed base address.

## Capture and PII — your data, your responsibility

Adapter call logs can persist request/response bodies and headers, which may contain user data. This is the same responsibility model as `Job.Message`: **Warp gives you the controls and safe defaults; the content you choose to capture is yours to own.**

- **Metadata is always recorded** (adapter, operation, group, duration, attempts, outcome, status code, exception type/message, machine name, trace id, tags, correlation id). No payloads.
- **Bodies and headers are opt-in and independent.** `CaptureRequestBodies`, `CaptureResponseBodies`, and `CaptureHeaders` are each a `CaptureMode`:

  | Mode | Captures |
  |---|---|
  | `None` (default) | nothing |
  | `OnFailure` | only on a non-success outcome |
  | `Always` | on success too |

- **Truncation caps** apply before storage: `MaxCapturedBodySize` (8 KB), `MaxCapturedHeaderSize` (4 KB).
- **Header redaction** runs through the user-owned `RedactedHeaders` denylist — case-insensitive, prepopulated with `Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`. Redacted values store as `***`. The denylist is a **default, not a lock**: `Add`, `Remove`, and `Clear` are all yours. (Mandatory `Authorization` redaction was deliberately rejected — the responsibility model puts you in control.)

```csharp
opt.AddAdapter("acme-payments", a =>
{
    a.CaptureRequestBodies = CaptureMode.OnFailure;
    a.CaptureResponseBodies = CaptureMode.OnFailure;
    a.CaptureHeaders = CaptureMode.OnFailure;
    a.RedactedHeaders.Add("X-Acme-Signature");   // add your own
    a.RedactedHeaders.Remove("X-Api-Key");        // or opt one back in — your call
});
```

Keep group values and tags tokenised too — they render in the dashboard. Hash tenant identifiers; don't put raw emails in a group name.

## Recording volume — `RecordCalls`

`RecordCalls` decides whether a call-log **row** exists at all; it is orthogonal to capture (which controls how much payload a row carries):

| `CallRecording` | Writes a row for |
|---|---|
| `All` (default) | every call — successes included |
| `FailuresOnly` | non-success outcomes only |

`RecordCalls = All` writes one row per call, which is meaningful volume on a hot adapter. Mitigations, in order of reach:

1. Recording is **batched** through a bounded channel and drained by the flusher — never a per-call synchronous write. Channel size is `WarpConfiguration.CallLogBufferCapacity` (default 10,000, shared with inbound endpoint observability); drain batch size is `CallLogFlushBatchSize` (default 500).
2. Rows are pruned by `ExpirationCleanup` on **both** an age and a count cap, whichever trims first: `CallLogRetention` (per-adapter) / global `AdapterCallLogRetention` (default 7 days) stamps `ExpireAt`; `CallLogRetentionCount` (per-adapter) / global `AdapterCallLogRetentionCount` (default null = disabled) keeps at most N rows per adapter, deleting the oldest — the "keep last N" knob for a hot adapter that fills its age window in minutes.
3. **`SampleRate`** (0.0–1.0, default 1.0) keeps a row for that fraction of **successful** calls; failures are always kept. The Sentry-style knob for a hot adapter where representative payloads suffice — the aggregate counts, error rate, and latency percentiles still record every call, so only raw success rows thin out.
4. For genuinely hot adapters, set `RecordCalls = CallRecording.FailuresOnly` — counters and telemetry are unaffected, so you keep the rates and lose only the per-success rows.

Recording is **lossy by design.** If the channel is full, the record is dropped, `warp.adapter.records_dropped` increments, and the caller returns without delay or error. Call logs are diagnostics, not an audit trail — the same stance as `JobLog`. If you need a guaranteed attempt record, that is what the (future) webhooks feature's own delivery table is for; it reads attempts from here via `CorrelationId`.

### Correlation

`SetCorrelation(...)` / `request.WithWarpCorrelation(...)` stamps a caller-supplied, indexed key on the row — a webhook delivery id, an order id, whatever links the call back to a domain record. Feature-agnostic; query `AdapterCallLog` by `(AdapterName, CorrelationId)`.

## Observe-first rollout (recommended)

The recommended way to adopt adapters is to **add no policy on day one**. Register the adapter with neither `UseResilience` nor `UseSharedRateLimit`:

```csharp
opt.AddAdapter("acme-payments", a => a.BaseUrl = "https://api.acme.example");
// no UseResilience, no UseSharedRateLimit
```

The pipeline then contains only the passive observing handler — **zero behavioral change** to your existing calls: same timeouts, single attempt, same exceptions. You get spans, meters, and call logs and nothing else moves. Read the data for a week. *Then* add policy per adapter, once the numbers justify the split — resilience where you see transient failures, a shared rate limit where you see the vendor pushing back. Policy is a deliberate, data-driven decision, not a default you inherited.

## Resilience (Polly)

```csharp
opt.AddAdapter("acme-payments", a =>
{
    a.BaseUrl = "https://api.acme.example";
    a.UseResilience(r => r.AddRetry(new()).AddTimeout(TimeSpan.FromSeconds(10)));
});
```

`UseResilience` wires `Microsoft.Extensions.Http.Resilience` (standard Polly). Handler ordering is fixed (not configurable): the Warp observing handler is **outermost** — it times the whole logical call and records one row with the final outcome and total attempt count — so a call that succeeds on retry #3 is one green row, not three rows. Per-attempt latency lives in the resilience pipeline's own OTel telemetry.

## Cluster-shared rate limiting

Per-process Polly rate limiting multiplies by the number of servers: N hosts each limited to 10/s means the vendor sees up to 10N/s. Warp's shared limiter is DB-backed and **cluster-wide**:

```csharp
opt.AddAdapter("acme-payments", a =>
{
    a.UseSharedRateLimit(
        limit: 100, perSeconds: 60,
        overflow: AdapterRateLimitOverflow.Wait,
        maxWait: TimeSpan.FromSeconds(30));
});
```

It reuses the `RateLimitBucket` entity under a disjoint key (`warp:adapter:{name}`), and it counts **physical attempts** (the rate-limit handler is innermost — the vendor counts attempts, not logical calls). To avoid a per-call DB round-trip, each process **leases** a chunk of tokens (`max(1, limit/10)`) in one locked check-and-increment and spends them locally, returning to the DB only when the lease empties. A crash loses only unspent lease tokens — under-admission, the safe direction.

Overflow behaviour:

| `AdapterRateLimitOverflow` | On a full window |
|---|---|
| `Wait` | bounded async delay for the next lease/window, up to `maxWait`, then throws `AdapterRateLimitedException` |
| `FailFast` | throws `AdapterRateLimitedException` immediately |

Both surface as a `Throttled` outcome on telemetry, counters, and the call log.

Admin overrides ride the existing `RateLimitOverride` table.

### Shared policy is coordinated config

Rate limit is *shared* config — unlike capture/redaction/resilience, which may legitimately differ per process, a shared limit is meaningless if two processes disagree. Persistence is **first-writer-wins**: the *first* registration writes the shared policy onto `AdapterDefinition`, and it stays put. Runtime precedence:

```
RateLimitOverride admin row  >  persisted AdapterDefinition policy  >  local code
```

During lease acquisition each process compares its local policy against the persisted one (no extra round-trip — it is already reading that row's neighbourhood). On mismatch it **enforces the persisted policy**, logs a Warning, increments `warp.adapter.config_conflicts`, and sets `HasPolicyConflict` on the definition — which surfaces as a badge in the dashboard. Cluster behaviour stays deterministic even mid-rolling-deploy.

Because the first writer wins, **redeploying with a new limit in code does not change the enforced limit** — the mismatch is preserved and flagged, not silently overwritten. To actually change a shared limit, either add a `RateLimitOverride` admin row (which takes precedence over the persisted policy) or clear the persisted policy on the `AdapterDefinition` so the next registration writes fresh.

## Always in the schema

Both entities — `AdapterDefinition` and `AdapterCallLog` — are added to the model **unconditionally** by `WarpModelCustomizer`, whether or not any host calls `AddAdapters()`. This keeps the migration story independent of which processes opt in (same principle as the other addon entities). `AddAdapters()` gates the runtime recording services and the dashboard flag only, never table existence. Run `dotnet ef migrations add AddWarpAdapters`; both tables appear.

Cleanup rides `ExpirationCleanup` (an existing server task, so the worker hot path is untouched): expired `AdapterCallLog` rows go by `ExpireAt`, and orphaned `AdapterDefinition` rows (renamed/removed adapters with no live sightings) go once `LastSeenAt` is older than `AdapterDefinitionOrphanGrace` (default 30 min — kept comfortably above the flusher's 5-min lazy `LastSeenAt` refresh cadence so an actively-used adapter is never deleted and re-inserted during the refresh window).

## Telemetry

Emitted **unconditionally** in the scope (null-listener pattern — zero cost without a listener), independent of `AddAdapters()`:

- **Span** — an `ActivityKind.Client` Activity named `{adapter}.{operation}`, with `warp.adapter.group` as an attribute.
- **Meters:**
  - `warp.adapter.calls` (counter; tags: adapter, operation, outcome — and group only if `IncludeGroupInMetrics`)
  - `warp.adapter.duration` (histogram)
  - `warp.adapter.records_dropped` (counter — channel-full drops)
  - `warp.adapter.config_conflicts` (counter — shared-policy mismatches)

The OTel histogram carries exact per-call latency for external backends. The dashboard shows counts, error rate, average, and **p90/p95/p99** — the percentiles come from a fixed-bucket latency histogram folded into the same `Counter`→`Statistic` aggregates (the reported value is the upper edge of the bucket the rank falls in), so they are exact-over-all-calls and survive log-row cleanup without an OTel backend.

Statistics are written as `Counter` rows (per adapter/operation/outcome and per group/outcome, successes included, plus per-outcome duration-sum and latency-bucket counters) that `CounterAggregator` collapses into `Statistic` rows — never a direct `Statistic` write from the call path. Because average latency and the percentiles are read from these aggregates rather than the raw `AdapterCallLog` rows, they stay correct after retention prunes the rows.

### Routing to OpenTelemetry instead of the database

By default the per-call detail lands as `AdapterCallLog` rows and the aggregates as `Counter`→`Statistic` rows in your database. On a hot adapter that write volume is the expensive part. `opt.AddAdapters(o => o.Sink = RecordingSink.Otel)` routes the captured detail onto the adapter `Client` span (as `warp.adapter.*` attributes) and relies on the always-on meters for the aggregates — **no call-log rows and no `Counter` writes**, keeping the database out of the hot path. `Both` does both; `Database` (default) is unchanged. See [Observability sinks](./observability-sinks.md).

## Multi-application provenance

In a shared-database deployment with [multi-application observability](./applications.md) enabled (`opt.ApplicationName` set), every `AdapterCallLog` row is stamped with the **producing application** — the app that made the call — as a nullable `Application` column, and per-application adapter metrics (calls, error rate, latency) accrue alongside the app-agnostic totals under a disjoint counter-key namespace. The dashboard's global application filter then scopes the Adapters surfaces to one app. When `ApplicationName` is unset the column is `null` and nothing changes.

## Dashboard

An **Adapters** nav item (gated on the `adapters` flag from `GET {prefix}/api/addons` — true only where `AddAdapters()` ran, hidden otherwise) opens two screens:

**Adapters list** — one row per registered adapter: name, a **health pill** derived from the recent error rate, calls, error %, average latency, a neutral **trend sparkline** over the recent window, and last-seen time. An adapter whose local shared rate-limit policy conflicts with the persisted cluster policy carries a **policy-conflict badge** (the persisted policy is what's being enforced — see [cluster-shared rate limiting](#cluster-shared-rate-limiting)).

**Adapter detail** — metric tiles (total calls, error rate, average latency, p90/p95/p99) over:

- a **per-operation table** — calls, error rate, and average latency per operation, so a single red operation across all groups (a caller-side bug) is visible at a glance;
- a **Groups table** — the same stats per group value, with the adapter's `GroupLabel` (`"Endpoint"`, `"Shop"`, …) as the column header. Error rates have real denominators because successes are counted per group too. Shown only when the adapter's data carries groups; a single red group across all operations points at the counterparty;
- a **recent-calls list** — timestamp, operation, group, outcome, status code, duration, attempts — opening a **call-detail drawer** with the captured request/response panes (post-redaction, post-truncation — exactly what was stored, never the live secret), the exception type/message on failures, tags, correlation id, trace id, and machine name.

Latency is reported as both the average and p90/p95/p99, all from the surviving aggregates (the OTel histogram remains available for exact, backend-side analysis).

`IAdapterQueryService` is registered by `AddWarp` itself — so dashboard-only, publisher-only, and `AddWarp`-only processes resolve it and serve the endpoints without running a server or calling `AddAdapters()`:

- `GET {prefix}/api/adapters` — registered adapters with total calls, error count, error rate, average latency, and p90/p95/p99.
- `GET {prefix}/api/adapters/{name}` — per-operation and per-group stat tables (error rates include successes), a recent-calls list, and the shared-policy conflict flag.
- `GET {prefix}/api/adapters/{name}/calls/{id}` — one call's captured, already-redacted request/response payloads.
- `GET {prefix}/api/addons` reports `adapters: true` iff `AddAdapters()` was called; the dashboard **Adapters** nav item is gated on that flag (hidden otherwise).

The detail page shows a **Groups** table (headed by the adapter's `GroupLabel`) only when the adapter's data carries groups; the recent-calls list gains a group filter and renders tags generically. Adapters without groups show no Groups section.

## Not in v1

Deferred by design (some with their own specs):

- **Shared (DB-backed) circuit breaker** — fast-follow reusing the same store pattern (`AdapterCallOutcome.CircuitOpen` is reserved for it).
- **`SOAPAction`-header operation-name fallback** — `WithWarpOperation` covers it today.
- **Minimal GraphQL client generator** (`Warp.Adapters.GraphQL`) — designed fast-follow; hand-written clients over the named adapter client are the v1 path.
- **Replay of failed calls** — records only; replay needs explicit idempotency opt-in.

**Shipped since this list was written:** durable webhook *delivery* — originally the first entry here — is now a built-in Core feature, [Outbound Webhooks](./webhooks.md) (`Warp.Core.Webhooks`, always on), built exactly as designed: deliveries in their own `WebhookDelivery` table, attempts read from `AdapterCallLog` via `CorrelationId` (when `AddAdapters()` recording is on).
