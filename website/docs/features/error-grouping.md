---
sidebar_position: 19
---

import Screenshot from '@site/src/components/Screenshot';

# Error grouping / Issues

Warp already persists every error signal it produces — a failed job's exception in `JobLog`, a 5xx in `EndpointCallLog`, a failed outbound call in `AdapterCallLog`, a browser error in `ClientEventLog`. Individually they're a firehose of rows. **Error grouping** folds that firehose into **issues**: one durable row per *real problem*, not per occurrence, with a count, first/last-seen, a trend, a sample, and an `Unresolved / Resolved / Ignored` lifecycle. It's Sentry-lite for everything Warp can already see, built on the same lossy-fold → durable-`Counter` pipeline as the rest of the observability stack — so the trends **survive raw-row cleanup**.

It's an **always-on Core feature** — no addon to enable, like [queue metrics](./queue-metrics.md). Set `ErrorGroupingInterval = null` to turn it off.

<Screenshot light="/img/screenshots/31-issues.png" dark="/img/screenshots/31-issues-dark.png" alt="Issues page grouping errors across jobs, endpoints, adapters and client by fingerprint" />

## What it groups

Four sources feed one issue list. Each contributes an occurrence when it sees a real error signal:

| Source | Counts as an occurrence | Grouped by | Sample / trace |
|---|---|---|---|
| **Job** | **every** caught exception — retry attempts *and* the terminal failure, so flaky handlers stay visible | exception type + **top in-app stack frame** | `Job.TraceId` |
| **Endpoint** | `Failed` (5xx / unhandled) **and** any **4xx** | 5xx: type + route · 4xx: **status + route** | `TraceId` |
| **Adapter** | `Failed` only (Throttled / CircuitOpen are expected backpressure, excluded) | exception type + `adapter.operation` | `TraceId` |
| **Client** | browser `Error` events only | type + **top stack frame** | `TraceId` |

**Endpoint 4xx** become `status + route` groups (no exception). They're **default-filtered** in the UI (the list shows real errors first; toggle to include them) and **kept off the reliability SLI** — [endpoint observability](./endpoint-observability.md) stays 5xx-only, because "is a member of an issue" is not the same as "counts against the error rate". A `404 /orders/{id}` group is a diagnostic, not an outage.

Because the four sources share one list, an issue's **source badge** is part of its identity: a browser `TypeError` and a server-side exception are never the same issue, and you can slice the list by source.

## How it works

The obvious way to build this — scan the log tables for errors — would need new indexes on the hottest tables in the system, write-amplifying every `JobLog` and `*CallLog` insert. Warp doesn't do that. Instead it uses an **inbox-drain** write path that adds **zero cost to the worker hot path**:

- **Each source appends one `ErrorOccurrence` inbox row** — a transient, write-optimized, self-clearing row (the same pattern as `Counter`). A **job** appends its row inside the finalization `SaveChanges` it was already doing — no fingerprint computed on the worker, no extra round-trip. The **endpoint** middleware appends for 4xx and 5xx (so a 4xx group exists even under `RecordCalls=FailuresOnly`, since the middleware sees every request while the call-log flusher may not). **Adapters and client** append in their existing flushers.
- **A server task drains it.** `ErrorGroupAggregator` runs off the hot path (like `CounterAggregator`), **drains-and-deletes** the inbox each tick (exactly-once by construction — no cursor to get wrong), computes the fingerprint, and **upserts** the `ErrorGroup` (bump `Count`, `LastSeenAt`, latest sample) while folding an hourly trend `Counter`. Because the trend lives in `Counter → Statistic`, the sparkline and totals **outlive** the raw rows once retention sweeps them.

### The fingerprint

An issue's identity is `hash(source + exception-type + locus)`. The **message is normalized out of the identity** — digits, GUIDs, hex, and quoted literals are replaced with placeholders (`<num>`, `<guid>`, `<hex>`, `<str>`) — so `Order 4021 not found` and `Order 5535 not found` are the *same* issue, and the normalized message becomes the issue's **Title** (which is also why the Title is PII-safe).

Grouping is **fine-grained**: for stack-bearing sources (jobs, client) the *locus* is the **top in-app stack frame** — the first frame that isn't framework code (`Warp.*` / `System.*` / `Microsoft.*` / `Npgsql.*` by default, editable via `InAppNamespaceDenylist`). Two different bugs in the same handler are two issues, not one. Stackless sources (endpoint, adapter) use the route/operation as the locus; 4xx uses `status + route`.

## Lifecycle

Sentry-lite, deliberately minimal:

- **New** — an issue first seen within a recent window gets a **"new" badge** in the UI. That's it: no alert. A fresh deploy mints many never-before-seen error types, and paging on each one is noise.
- **Resolve / Ignore** — `IErrorGroupCommandService.SetStatus(fingerprint, status)` flips the status under a mutex with a structured audit log. An **Ignored** issue still counts occurrences but stays hidden from the default view.
- **Regression** — a **Resolved** issue re-opens (→ Unresolved, "regressed" badge) **only** when a *new* occurrence arrives with a timestamp later than when you resolved it — so a backlog of pre-resolution occurrences can't falsely re-open it. That regression fires a `WarpEventType.IssueRegressed` [operational event](./operational-notifications.md), dispatched post-commit through the notifier seam (opt-in; inert if you've registered no notifier). **Ignored** issues never auto-re-open.

## Navigation

- **Issue → trace → everywhere.** The issue detail stores the `SampleTraceId` of its most recent occurrence, so one click jumps into the [unified trace view](./tracing.md) — where the browser request, the endpoint call, the jobs, and the outbound calls of that trace each link to their own detail page. So an issue reaches all of its related surfaces through the trace.
- **Reverse (a source detail page → its issue)** — showing a "Part of issue" chip on a job/endpoint/adapter/client detail — is a planned follow-up. Because the fingerprint is a pure function of the row a detail page already loads, it can be recomputed on-read with no stored column and no hot-path cost; it is not part of the 3.9 slice.

## Config

All on `WarpConfiguration`:

| Setting | Default | Meaning |
|---|---|---|
| `ErrorGroupingInterval` | `15s` | Aggregator cadence. **`null` disables the feature.** |
| `ErrorGroupRetention` | `30d` | Age cap (by `LastSeenAt`) for `ErrorGroup` rows. |
| `ErrorGroupRetentionCount` | `null` | Row-count cap (keep newest N), off by default. |
| `MaxDistinctErrorGroups` | `2000` | Per-source cap; overflow collapses into a per-source `{other}` group. |
| `CaptureErrorSamples` | `true` | Store a raw (truncated) sample alongside the normalized Title. |
| `InAppNamespaceDenylist` | framework list | Namespaces skipped when picking the top in-app frame. |

Retention is enforced by `ExpirationCleanup` on **both** age and count (whichever trims first), plus a defensive orphan-occurrence sweep. The trend `Statistic`s outlive the groups.

The `MaxDistinctErrorGroups` cap matters most for the **client** source, which is fed from a public ingest endpoint (hostile input) — a crafted flood of distinct error names can't explode the issue table; it collapses into `{other}` after the cap, still counted, with a one-time warning.

**Multi-application** ([applications](./applications.md)): an issue's `Application` is the **executor** app for jobs (execution attribution) and the source app otherwise, under a disjoint `errorgroup` / `errorgroup-app` counter namespace; filter the list with `?application=`.

**Sinks** ([observability sinks](./observability-sinks.md)): an `Otel`-only source writes no rows, so it produces no inbox rows and no issues (consistent — `Otel`-only means no DB dashboards). Jobs always feed the inbox because they always persist `JobLog`; use `ErrorGroupingInterval = null` to disable grouping outright.

## API

- `GET {prefix}/api/issues` — the grouped issue list (filter by `source` / `status` / `application`, plus the 4xx toggle).
- `GET {prefix}/api/issues/{fingerprint}` — one issue: title, sample stack, trend, and recent occurrences.
- `POST {prefix}/api/issues/{fingerprint}/status` — Resolve / Ignore / re-open.

All resolve in any `AddWarp` process — the query service is registered by `AddWarp` itself, so a dashboard-only or publisher-only host serves Issues without running a server.
