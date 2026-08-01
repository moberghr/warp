# Plan — Client (frontend) observability

*Spec: `docs/specs/2026-07-26-client-observability.md` · branch `feat/client-observability` · target 3.8.0*

Built as a faithful mirror of endpoint observability (§8.21) + adapters (§8.19). Batches are dependency-ordered; each is verified before the next. Full suite both providers + review, then STOP for approval before merge/release.

## Batch 1 — Core entity + enums + keys
- `ClientEventType` enum (`Warp.Core.Enums`): `Error=1, Vital=2, Log=3, Event=4`.
- `ClientEventLog` entity (`Warp.Core.Data.Entities`) + EF config (indexes, schema via `SetSchema`); add unconditionally in `WarpModelCustomizer`; confirm `WarpServerContext` mirrors it.
- `ClientEventKeys` (`Warp.Core.Services`, internal): prefixes `clientevent`/`clientevent-app`; tokens count/dur/pct/hist; `Build(record)`; parsers; `MaxDistinct*` collapse-to-`{other}`; CLS ×1000 scaling; p75-oriented buckets.
- NoDb tests: key build/parse round-trip, cardinality collapse, vital bucket + CLS scaling.

## Batch 2 — Core recorder + flusher + options + query + registration
- `ClientEventRecord` (public seam) + `IClientEventRecorder` + `DbClientEventRecorder` (bounded lossy channel, drop + `warp.client.events.dropped`).
- `ClientEventFlusher<TContext>` (`IHostedService`, `IServiceScopeFactory` scope, drains → rows + Counters).
- `WarpClientObservabilityOptions` (ingest keys, allowed origins, capture flags, sizes, redaction denylist, retention age+count, rate-limit, `Sink`).
- `IClientEventQueryService` + `ClientEventQueryService<TContext>` (summary from folded Statistics; recent stream; detail) — registered by `AddWarp` itself.
- `IClientObservabilityMarker`; `AddClientObservability(this IWarpBuilder)` — gates recorder+flusher by `RecordingSink`; always registers marker + query.
- Meters in `WarpTelemetry` (`warp.client.events`, `warp.client.vitals`, `warp.client.events.dropped`) + `WarpTelemetryAttributes` tags.
- DB tests: recorder→flusher persistence + `ExpireAt`; Counter fold; `Sink=Otel` skips DB writes; query summary from seeded Statistics (survives raw-row cleanup); cardinality cap.

## Batch 3 — HTTP ingest binding (new thin package `Warp.ClientObservability` or `Warp.Http.ClientIngest`)
- `MapWarpClientObservability()` → `POST {IngestPath}` (batch ingest) + `GET {IngestPath}/client.js` (script).
- DSN key resolution (key→trusted app), CORS (preflight + origin allowlist), in-memory per-key/IP token-bucket rate limit, size/batch caps.
- Wire-contract parse → `ClientEventRecord[]` → recorder.
- NoDb tests: key resolution (unknown→401), CORS preflight/origin match, rate/size/batch caps → drop, wire parse per type.

## Batch 4 — Browser script
- Self-contained TS (`src/clients/browser/warp-client.ts`, built to a served asset): error hooks, web vitals (PerformanceObserver), breadcrumbs, session id, `log()`/`track()`, `sendBeacon` batching + sampling.

## Batch 5 — Dashboard + API
- `GET {prefix}/api/client/summary|events|events/{id}` in `WarpEndpoints.cs`; `WarpAddonsInfo.Client` flag.
- Frontend: `src/ui/src/pages/client/ClientPage.tsx` (+ detail), `types/client.ts`, `api/index.ts`, nav item gated on `addons.client`. Vital tiles colored by Google good/needs-improvement/poor (p75). Screenshot spec entry `26-client`.

## Batch 6 — Retention/cleanup
- `WarpConfiguration.ClientEventLogRetention` (7d) + `ClientEventLogRetentionCount` (100_000).
- `ExpirationCleanup`: `CleanupExpiredClientEventLogsAsync` (age) + count variant, batched + `MaxSweepBatchesPerTick`.
- DB tests: age + count cleanup; aggregate metrics survive.

## Batch 7 — Middleware/TestHost + full-stack
- TestHost: POST a batch to a real mapped endpoint → rows + counters; PII redaction; 401 on bad key.
- Extend `FullStackObservabilityTestsBase` with a client-ingest hit + `/api/client/summary` assertion.

## Batch 8 — Docs
- `website/docs/features/client-observability.md`; rules §8.27; CLAUDE.md mention; releases 3.8.0. (No CLAUDE.md links in docs.)

## Batch 9 — Aspire demo SPA (live end-to-end proof)
- Add a small **demo SPA** resource to the Aspire AppHost that loads the shipped `client.js` from a demo web host's `MapWarpClientObservability()` ingest endpoint, configured with a demo ingest key + its own `ApplicationName` (§8.23, alongside the existing distinct-app demo services).
- The page deliberately generates all four event types on load / button click: throws an unhandled error, an unhandled rejection, a `warp.log("warn", …)`, a `warp.track("demo_clicked", …)`, and reports web vitals — so opening the dashboard **Client** page shows real rows/aggregates flowing from a browser through ingest → DB → dashboard.
- Host it the Aspire-idiomatic way (static assets served by an existing demo web project, or `AddNpmApp`/static resource — decide from the AppHost layout); gate DB work behind the existing migrator (WaitForCompletion) per the repo's migrator pattern.
- This is the manual/visual confirmation that complements the automated ingest + flusher + query tests (Batches 3/7): tests prove the units; the SPA proves the whole path in a running cluster.

## Verification
- `dotnet build src/Warp.slnx` analyzer-clean.
- Full suite both providers green.
- Two-stage review (compliance + test + silent-failure) on the diff; fix real findings.
- Screenshots deferred to a demo run (per screenshot rule).
- STOP for approval before merge/release.
