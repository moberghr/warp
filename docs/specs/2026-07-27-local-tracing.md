# Spec — Local tracing (embedded, DB-backed distributed tracing)

*2026-07-27 · target release 3.8.0 (with client observability) · branch `feat/client-observability`*

## Summary

Give Warp a **local, DB-backed tracing store + waterfall UI** — the Jaeger/Tempo experience with **no external backend**. Warp already emits OTel spans (`WarpTelemetry` ActivitySource, W3C `traceparent` propagated browser → API → jobs); this feature **captures those spans into the database** and renders a **trace waterfall** in the dashboard. The escape hatch is the existing sink model: when the DB gets too slow at your volume, flip the sink to an external OTLP collector — the spans are already emitted, so it's a config change, not a rewrite.

This is the "single screen with everything for a given trace" (per-action). It joins by **`TraceId`** (shared by the client request, the server request/endpoint, and the jobs) — not session id.

## Storage — the `Span` entity

`Span` in `Warp.Core.Data.Entities` (§8.13), always-in-schema (§2.11), mirrored by `WarpServerContext` (§2.14). The OTel span model, persisted:
- `Guid Id` (surrogate PK), `Guid TraceId` (joins `Job.TraceId`/`EndpointCallLog.TraceId`), `string SpanId` (16-hex), `string? ParentSpanId`,
- `string Name`, `SpanKind Kind` (§8.11 from 1: `Internal=1, Server=2, Client=3, Producer=4, Consumer=5` — mirrors `ActivityKind`), `SpanStatus Status` (`Unset=1, Ok=2, Error=3`),
- `DateTime StartTime`, `double DurationMs`, `string? Application`, `string? Attributes` (JSON tag map), `DateTime? ExpireAt`.
- Indexes: `(TraceId)`, `ExpireAt`.

Retention on `WarpConfiguration`: `SpanRetention` (age, default 3d — spans are the highest-volume signal) + `SpanRetentionCount` (count cap), swept by `ExpirationCleanup` (age + count, §8.22).

## Capture — the in-process collector

`WarpSpanCollector` — an `IHostedService` that registers a `System.Diagnostics.ActivityListener` (pure BCL — no OpenTelemetry SDK dependency, self-contained):
- **Sources**: `WarpTelemetry.ServiceName` ("Warp") by default (job/receive/producer/adapter/webhook spans Warp already emits), **plus** `Microsoft.AspNetCore` (so the inbound server request span — which adopts the browser's `traceparent` — is captured as a real span), plus any `o.AdditionalSources` (e.g. `System.Net.Http`, EF) to dial up to "capture everything".
- **Sampling**: `o.SampleRate` (default 1.0 in dev; a real deployment lowers it). One consistent per-trace decision (root span decides; children follow), so a trace is captured whole or not at all.
- **On `ActivityStopped`** (sampled + recording): build a `SpanRecord` from the `Activity` (ids, name, kind, start, duration, status, tags→JSON, `warp.application`) → push to `DbSpanRecorder` (bounded lossy channel — drop + `warp.tracing.spans_dropped` on overflow, never blocks the app) → `SpanFlusher<TContext>` drains to `Span` rows on an `IServiceScopeFactory` `TContext` scope (§0.5). Same recorder/flusher/retention pattern as adapters/endpoints/client (§2.15).

Client (browser) spans: the shipped script already mints a `traceparent` (trace + span id) per fetch and a page context; extend it to POST **spans** (not just events) to the ingest endpoint, which hands them to the same `ISpanRecorder`. So the browser hop is a real span parented into the server trace.

## Sink (§8.24)

`opt.AddLocalTracing(o => { o.Sink = …; o.SampleRate = …; o.AdditionalSources = […]; })`:
- `Database` (default) → the collector + flusher populate the local `Span` store; the dashboard waterfall works.
- `Otel` → **no collector/flusher** (no DB rows); rely on the host's `AddOtlpExporter` — the spans are already emitted. This is the "grow into an external backend" path.
- `Both` → local store **and** external export.

Marker (`ILocalTracingMarker`) gates the dashboard flag regardless of sink. The internal telemetry (spans/meters) is always emitted (§8.24 track (a)); this feature is only the DB **recording** track.

## Read + UI — the waterfall

`ITraceQueryService.GetTrace(traceId)` (registered by `AddWarp`, resolves in any process) → all `Span` rows for the trace → an ordered tree (`TraceSpanModel`: id/parent/name/kind/status/relative-start/duration/attributes). `GET {prefix}/api/traces/{traceId}`.

Dashboard: rework `/trace/{traceId}` (`TracePage.tsx`) from the job-only DAG into a **Gantt waterfall** — rows are spans, indented by parent, with a start-offset + duration bar on a shared time axis, colored by kind, error spans flagged; clicking a span shows its attributes. The existing job-DAG stays reachable, but the waterfall is the default "everything for this trace" view: browser fetch → server request → job spans → adapter/webhook spans, top to bottom.

Entry points: the existing **trace →** links (session timeline, endpoint detail, job detail, client event detail) now land on the waterfall.

## Migration

Additive: 1 new table (`span`) + config. Picked up by a standard `dotnet ef migrations add` / `database update`. `null` sink/feature-off ⇒ no collector, no rows — byte-for-byte current behavior.

## Tests (both providers, §4.2)

- NoDb: `ActivityListener` capture → `SpanRecord` mapping (ids/kind/status/tags); sampling gate; recorder drop-on-full; sink gating (Otel ⇒ no collector/flusher registered).
- `[GenerateDatabaseTests]`: flusher persistence + `ExpireAt`; `GetTrace` builds the tree (parent/child ordering, relative offsets); age + count cleanup.
- A capture round-trip: start a Warp `Activity` under a trace, assert a `Span` row lands with the right parent.
- UI: vitest navigation test — `/trace/{id}` renders the waterfall (spans + bars) from the demo mock.

## Docs

`website/docs/features/local-tracing.md` (contrast: local store vs external OTLP; when to graduate), rules §8.28, CLAUDE.md, releases 3.8.0. Demo: the existing `/client-demo` already produces a browser→endpoint→job trace; screenshot `30-trace-waterfall`.
