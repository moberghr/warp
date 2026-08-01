# Spec — Unified trace view (single screen for a trace, from existing rows)

*2026-07-27 · target 3.8.0 · branch `feat/client-observability`*

## Summary

A single screen showing **everything for a given trace id** — the browser request, the server endpoint call, the jobs it spawned, and the outbound calls those jobs made — built entirely from **rows Warp already persists**. No new `Span` table, no `ActivityListener` collector: a job, an endpoint call, an adapter call, and a client request event are *already* spans (each has a trace id, a start, a duration, a status). We union them by trace id.

Boundary: this gives the **Warp-domain** trace (client → endpoint → jobs → outbound calls) with zero new storage/hot-path cost. Capturing arbitrary third-party spans (EF/HttpClient/etc.) like a full Jaeger remains the reason to graduate to an external OTLP backend (the existing sink flip) — not built here.

## The spans, from existing rows (all joined by trace id)

| Source | Row | start | duration | status | tree link |
|---|---|---|---|---|---|
| `client` | `ClientEventLog` (Type=Request) | Timestamp | Value (ms) | — | root |
| `endpoint` | `EndpointCallLog` | Timestamp | DurationMs | Outcome | under client |
| `job` | `Job` (TraceId) | CreateTime¹ | ¹ | CurrentState | `SpawnedByJobId` tree |
| `adapter` | `AdapterCallLog` | Timestamp | DurationMs | Outcome | under its job |

¹ The `Job` row stores enqueue time (`CreateTime`) but not a clean execution start/duration — those live in `JobLog`/stats. v1 uses `CreateTime` for ordering and leaves the job bar duration unset (the DAG structure via `SpawnedByJobId` is exact); a follow-up can source execution timing from `JobLog`.

`AdapterCallLog.TraceId` is stored as the 32-hex string; the query matches it as `traceId.ToString("N")`. The others are `Guid`.

## Read

New `ITraceQueryService.GetTrace(Guid traceId)` (`Warp.Core.Services`, registered by `AddWarp` so any process resolves it) → `TraceOverviewModel`:
- `IReadOnlyList<TraceSpanModel> Spans` — every span for the trace, ordered by start, each `{ Source, Id, Name, StartTime, DurationMs?, Status, ParentId? }`.
- convenience counts (jobs/endpoints/adapters/errors).

`GET {prefix}/api/traces/{traceId}` returns it. (The existing `GET trace/{traceId}` → `GetTraceTree` stays for the job DAG; the new endpoint is the superset.)

## UI

Rework `/trace/{traceId}` (`TracePage.tsx`) to show, above/around the job graph, the **client request → endpoint → (jobs) → adapter calls** as a unified, time-ordered list (a waterfall when timing is available), each row linking to its own detail (endpoint call, adapter call, job detail, client event). Keep the job DAG as the structural view. All existing **trace →** links land here.

## Tests (both providers)
- `[GenerateDatabaseTests]`: seed a trace with a client request + endpoint call + 2 jobs (parent/child) + an adapter call → `GetTrace` returns all four sources, correct parent link, ordered by start; adapter trace-id string match works.
- UI vitest: `/trace/{id}` renders the unified rows from the demo mock.

## Docs
rules §8.28 (unified trace, built from existing rows), CLAUDE.md mention, releases 3.8.0 entry, `30-trace` screenshot refresh.
