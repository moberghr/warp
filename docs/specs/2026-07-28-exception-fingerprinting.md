# Spec — Error grouping / "Issues" (exception fingerprinting) — 3.9

Feature 2 of the 4-part observability program (queue-wait ✅ 3.7, trace waterfall ✅ 3.8, **this** → 3.9, SLO/error-budget → later). Own slice, own PR. Does **not** include proactive spike-alerting — that's the SLO/error-budget feature (this one gives grouping + visible trends; SLO makes them push).

## Goal

Group error signals from **all four** already-persisted surfaces into Sentry-lite **issues** — one line per real problem, not per occurrence — with count, first/last-seen, trend, sample, and an `Unresolved/Resolved/Ignored` lifecycle. Reuses the recorder→fold→durable-Counter→query machinery; the trend survives raw-row cleanup.

Grilled decisions (2026-07-28): inbox-drain write path · fine (stack-frame) grouping · broadened to include endpoint 4xx status-code groups · always-on · raw samples · on-read bidirectional navigation.

## Sources & what counts (broadened beyond exceptions → "error signals")

| Source | Counts as an occurrence | Group key | Culprit | Sample / trace |
|---|---|---|---|---|
| **Job** | **every** caught exception — retry attempts AND terminal (`FinalizeJobState` has `e` either way); flaky handlers must be visible | type + **top in-app frame** (parsed from `JobLog.Exception`) | handler | `Job.TraceId` |
| **Endpoint** | `Outcome=Failed` (5xx/unhandled) **and** any **4xx** (status-code group) | 5xx w/ exception: type + route · 4xx: **status + route** | `Method`+`RouteTemplate` | `TraceId` |
| **Adapter** | `Outcome=Failed` only (Throttled/CircuitOpen excluded — expected backpressure) | `ExceptionType` + `adapter.operation` | `AdapterName`+`Operation` | `TraceId` (string) |
| **Client** | `Type=Error` only | type + **top frame** (parsed from `Stack`) | `Url` | `TraceId` |

**Endpoint 4xx** are `status + route` groups (no exception): **default-filtered** in the UI (list defaults to real errors; toggle to include), **kept off the reliability SLI** (§8.21 stays 5xx-only — issue membership ≠ error rate), and fed to the inbox from the **`WarpInboundObservabilityMiddleware`** (which sees every request), NOT the call-log flusher — so the group exists even under `RecordCalls=FailuresOnly`.

## Fingerprint (`ErrorFingerprint`, `Warp.Core.ErrorGrouping`, pure + unit-tested)

`hash(source + type + locus)` → 32-hex. **Message is normalized OUT of identity** (digits/GUIDs/hex/quoted literals → `<num>/<guid>/<hex>/<str>`) so message-varying occurrences group; the normalized message is the **Title**. **Fine grouping** — for stack-bearing sources (job, client) the locus is the **top in-app stack frame** (first frame not matching the in-app denylist `Warp.*`/`System.*`/`Microsoft.*`/`Npgsql.*`, configurable), so two different bugs in one handler are two issues; stackless sources (endpoint, adapter) use route/operation; 4xx uses `status + route`. **Source is part of identity** (a browser `TypeError` ≠ a server exception; the UI slices by source).

## Write path — inbox-drain, zero hot-path change (§0.2/§6.1)

`JobLog`/`*CallLog` have no `(discriminator, Timestamp)` index — scanning them would need new indexes on hot tables (write-amplifying every `JobLog` insert). So instead:

- **`ErrorOccurrence` inbox** (transient, write-optimized, self-clearing — the `Counter` pattern): each source appends one row carrying `Source`, `ExceptionType`, raw `Message`, `Stack`/sample (truncated), `Culprit`, `StatusCode?`, `TraceId`, `Application`, `Timestamp`. **Jobs** append **one insert in the existing finalization `SaveChanges`** (no fingerprint CPU on the worker, no round-trip — same shape as the queue-wait counter, §8.26); **endpoint** appends from the middleware (4xx+5xx), **adapter/client** append in their flushers.
- **`ErrorGroupAggregator<TContext>`** — an `IServerTask` (§2.3, like `CounterAggregator`); `LockKey="warp:error-grouping"`, cadence `WarpServerConfiguration.ErrorGroupingInterval` (default **15s**, `null` disables). **Drains and deletes** the inbox (exactly-once by construction — no cursor), computes fingerprints **off the hot path**, and **upserts** `ErrorGroup` (`Count += n`, `LastSeenAt`, `LastSample`, `SampleTraceId`; sets `FirstSeenAt`/`Title`/`Culprit` on insert) + folds an `errorgroup:{fingerprint}:count` **Counter** (hourly trend → `Statistic`, §6.2, survives raw-row cleanup).

**Cardinality guard** (§8.19/§8.27 — critical: the client source is fed from the public ingest endpoint, hostile input) — cap distinct `ErrorGroup`s per source (per source+app when app-sliced) at `MaxDistinctErrorGroups` (default **2000**); overflow collapses into a per-source `{other}` group (still counted, keeps a sample); one-time warning on first overflow.

## Entities (`Warp.Core.Data.Entities`, §8.13; always-in-schema §2.11; mirrored by `WarpServerContext` §2.14)

- **`ErrorGroup`** — `Fingerprint` (unique), `Source` (`ErrorSource` from 1: `Job=1,Endpoint=2,Adapter=3,Client=4`), `Kind` (`ErrorKind`: `Exception=1,StatusCode=2`), `ExceptionType`, `Title`, `Culprit`, `StatusCode?`, `Application?`, `FirstSeenAt`, `LastSeenAt`, `Count`, `LastSample` (raw, truncated, §1.2), `SampleTraceId?`, `Status` (`ErrorGroupStatus` from 1: `Unresolved=1,Resolved=2,Ignored=3`), `StatusChangedAt?`.
- **`ErrorOccurrence`** — the transient inbox (fields above); drained+deleted each tick. A safety sweep in `ExpirationCleanup` removes any orphaned rows past a grace (defensive, like the webhook stuck-recovery).

## Lifecycle (Sentry-lite)

- **New** (first-ever) — **UI only**, a "new" badge on issues first-seen within a recent window. No operational alert (a fresh deploy mints many new types — paging on each is noise).
- **Resolve / Ignore** — `IErrorGroupCommandService.SetStatus(fingerprint, status)` (mutex + structured `LogInformation` audit; mirrors `SagaCommandService.ForceComplete`). `Ignored` still counts, hidden from the default view.
- **Regression** — a `Resolved` group re-opens **only** on an occurrence with `Timestamp > StatusChangedAt` (so pre-resolve inbox backlog can't false-reopen) → `Unresolved` + "regressed" badge + **`WarpEventType.IssueRegressed`** operational event, dispatched **post-commit** from the aggregator's `OnCommittedAsync` (§8.25 — never `ExecuteAsync`). `Ignored` never auto-re-opens. Opt-in/inert notifier seam (zero registered = no-op).
- **Retention (§8.22)** — `ExpirationCleanup` removes `ErrorGroup`s past `ErrorGroupRetention` (age by `LastSeenAt`, default 30d) + `ErrorGroupRetentionCount` (count, null=off). Trend `Statistic`s outlive the groups per existing aggregate retention.

## Navigation (bidirectional — "you should be able to navigate around")

- **Issue → occurrence → trace**: issue detail lists recent occurrences (trace ids) linking into the [unified trace view](§8.28).
- **Occurrence → issue** (reverse): job / endpoint / adapter / client **detail pages recompute the fingerprint on-read** (pure function of the row they already loaded — no stored `Fingerprint` column, no hot-path change) and show a **"Part of issue: … · N events →"** chip linking to `/issues/{fingerprint}`.

## Read side & dashboard

`IErrorGroupQueryService` (registered by `AddWarp` itself — resolves dashboard/publisher-only, §8.27/§8.28). Metrics (count/trend) from durable aggregates survive raw-row cleanup; recent-occurrences degrade to empty once swept. **Issues** page (`/issues`, always-shown built-in nav) — grouped list (source badge, count, trend sparkline, status chip, new/regressed badges, filters: source/status/application, 4xx toggle) + detail (`/issues/:fingerprint`: title, raw sample stack, trend chart, occurrences → trace, Resolve/Ignore). Screenshot per the update-screenshots rule. API `GET /api/issues`, `GET /api/issues/{fingerprint}`, `POST /api/issues/{fingerprint}/status`.

## Config, multi-app, sink

- `WarpConfiguration`: `ErrorGroupingInterval` (15s, null=off), `ErrorGroupRetention` (30d), `ErrorGroupRetentionCount` (null), `MaxDistinctErrorGroups` (2000), `CaptureErrorSamples` (true), `InAppNamespaceDenylist` (default list, editable).
- **Multi-app (§8.23)** — `Application` on `ErrorGroup` = **executor** app for jobs (execution attribution), source app otherwise; disjoint counter namespace `errorgroup`/`errorgroup-app` (first-segment-equality safe, §8.6/§8.19); `?application=` filter.
- **Sink (§8.24)** — Otel-only sources write no rows → no inbox rows from them → no issues (consistent: Otel-only = no DB dashboards). Jobs always feed (they always persist `JobLog`); disable via `ErrorGroupingInterval = null`.

## Tests (both providers, §4.2)

- **NoDb** — `ErrorFingerprint`: message-varying → one fp; different type/frame → different; in-app frame extraction across .NET + browser stack formats; denylist skips framework frames; 4xx `status+route` key; cardinality `{other}` collapse.
- **DB** — aggregator drains+deletes inbox and folds each source (Count/First-Last/Title/Culprit/SampleTraceId/Application); drain is exactly-once across two ticks; trend Counter survives an `ErrorGroup` raw-row cleanup; job every-attempt (retry+terminal both land); endpoint 4xx group from middleware under `FailuresOnly`; regression flips only on post-`StatusChangedAt` occurrence + buffers event; Ignored no re-open; `SetStatus` + audit; retention age+count; multi-app disjoint namespace; on-read backlink fingerprint matches the aggregator's.

## Docs

`website/docs/features/error-grouping.md`, rules **§8.29**, `CLAUDE.md` mention, `releases.md` 3.9 entry, `/issues` screenshot.

## Non-goals (v1)

Custom fingerprint/merge-issue rules, assignee/comments, per-issue alert thresholds (SLO feature), source-map symbolication of client stacks, proactive spike detection (SLO feature).
