# Spec — Client (frontend) observability

*2026-07-26 · target release 3.8.0 · branch `feat/client-observability`*

## Summary

A browser telemetry **ingest endpoint** (Sentry-lite) so a frontend app can report its own **errors, logs, web vitals, and custom events** to Warp, attributed to an **application** (§8.23). It is the *client-side* complement to Warp's existing server-side observability (adapters §8.19, endpoints §8.21, job execution §8.23/§8.24): same ingest→bounded-lossy-channel→flusher→DB-rows+Counter-aggregates→dashboard pipeline, same age+count retention (§8.22), same DB/Otel/Both sink model (§8.24), same multi-app slicing (§8.23).

**Explicitly in scope:** error capture (message/stack/breadcrumbs), structured logs, web vitals (Google Core Web Vitals), custom named events, a shipped browser script, a dashboard page.
**Explicitly out of scope (not Warp's lane):** product-analytics suite (funnels, cohorts, retention curves), session replay, source-map symbolication. Teams wanting those point their own PostHog/GA SDK elsewhere.

## Locked decisions (from design discussion)

1. **Auth = public DSN-style ingest key.** A write-only public key per app, safe to ship in the bundle; identifies + authorizes writes only, never reads. Origin allowlist + rate limit on top.
2. **One primitive, not two.** A single `ClientEvent` row with a `Type` discriminator (`Error`/`Vital`/`Log`/`Event`). `log()` and `track()` are thin sugar over the same ingest; auto-captured errors/vitals are the same row with a reserved type. No second subsystem.
3. **Warp ships the browser script** (the ingest endpoint serves it — you need it anyway).
4. **Web vitals follow Google: p75** (their good/needs-improvement/poor thresholds are p75-based). Not p95.
5. **Never inflate the DB unbounded.** Two tiers: raw rows are lossy + trimmed (age AND count); trend data lives in `Counter → Statistic` folds that **survive the trim** (§8.22). This is the core storage contract.

## Storage model — two tiers

### Tier 1 — raw rows (`ClientEventLog`), lossy + always trimmed
`ClientEventLog` in `Warp.Core.Data.Entities` (§8.13), **always-in-schema** (§2.11 `WarpModelCustomizer`), mirrored by `WarpServerContext` (§2.14). Diagnostics, not an audit trail. Ingest goes through a bounded channel (`DbClientEventRecorder`, lossy — drop-on-full + `warp.client.events.dropped` meter, never blocks the browser). `ExpirationCleanup` trims on **both** an age cap (`ClientEventLogRetention`, default 7d, stamped as `ExpireAt`) and a row-count cap (`ClientEventLogRetentionCount`, keep-newest-N, default 100_000), batched + `MaxSweepBatchesPerTick`-bounded — mirrors `EndpointCallLog` (§8.22).

Columns: `Id`, `Application` (from the key mapping — trusted, not client-declared), `Type` (`ClientEventType`), `Name?` (error type / vital name / event name), `Level?` (logs), `Message?` (truncated), `Stack?` (truncated, errors), `Value?` (double, vitals), `Route?`/`Url?` (page), `SessionId?`, `Release?`, `UserAgent?`, `RemoteIp?` (PII, optional), `Properties?` (JSON, truncated + redacted), `Breadcrumbs?` (JSON, truncated, errors), `Timestamp` (client, clamped to a sane window), `ReceivedAt` (server), `ExpireAt`. Indexes mirror `EndpointCallLog`: `(Application, Timestamp)`, `(Type, Timestamp)`, `ExpireAt`.

`ClientEventType` enum from 1 (§8.11, `Warp.Core.Enums`): `Error = 1, Vital = 2, Log = 3, Event = 4`.

### Tier 2 — folded aggregates (`Counter → CounterAggregator → Statistic`), survive the trim
`ClientEventKeys` (`Warp.Core.Services`, `internal static`), own disjoint top-level prefixes (`clientevent` / per-app `clientevent-app`, first-segment-equality parsers reject them §8.6/§8.19). What folds:
- **Errors** → count per type + per error `Name` (cardinality-capped) → error volume + rate trends. (Grouping by a normalized *fingerprint* is a later exception-fingerprinting feature; v1 folds by error `Name` with a cap.)
- **Logs** → count per `Level`.
- **Web vitals** → per vital `Name`: count + `dur`-sum + `pct` histogram → **avg + p75 survive** (the queue-wait/adapter `dur`+`pct` pattern §8.22). CLS (unitless 0–1) is scaled ×1000 into the same integer bucket set; ms vitals (LCP/FCP/TTFB/INP) native.
- **Custom events** → count per event `Name` (cardinality-capped).
- Hourly `hist` series for time-over-change; auto-prunes at `HourlyStatisticsRetention` (7d); lifetime totals persist.

**Cardinality guard** (the guard queue-wait didn't need): event names / error names / routes are browser-controlled and can explode, so aggregate keys get the adapters `MaxDistinct*` collapse-to-`{other}` treatment (§8.19) — bounded metric keys; unbounded values live only in the (trimmed) raw rows. Vital names are bounded (5 Core Web Vitals).

## Ingest — the public endpoint

New thin HTTP binding (composes against Core's public API only, no `InternalsVisibleTo` §0.5). Core (`Warp.Core.ClientObservability`) holds the protocol-agnostic recorder/flusher/entity/options/query/keys + the parsed `ClientEventRecord` seam; the binding holds the minimal-API endpoint + DSN validation + CORS + in-memory rate limit.

- **Register**: `opt.AddClientObservability(o => { … })` in the `AddWarp` lambda (non-generic `IWarpBuilder`, mirrors `AddEndpointObservability`) wires the DB recorder + flusher (gated by `RecordingSink`). **Map**: `app.MapWarpClientObservability()` after `UseRouting` exposes the endpoint. Ingest disabled (endpoint 404s) unless ≥1 ingest key configured — safe default.
- **Endpoint**: `POST {IngestPath}` (default `/warp/ingest`) accepts a JSON batch; `GET {IngestPath}/client.js` serves the shipped browser script (so one binding serves both — "you need it anyway").
- **Auth**: `o.AddIngestKey(applicationName, publicKey)` (≥1). Browser sends the key (header `x-warp-key`); endpoint resolves key→**trusted** application name (client can't spoof another app's name). Unknown key → 401.
- **CORS**: `o.AllowedOrigins` allowlist; endpoint answers preflight `OPTIONS` and sets `Access-Control-Allow-Origin` for matching origins only.
- **Abuse/volume**: a **lightweight in-memory** per-key/per-IP token-bucket rate limiter (NOT the DB-backed cluster limiter — a public browser beacon must never hit the DB on the request path, which would defeat the "don't inflate the DB" goal); a hard payload-size cap (`MaxIngestBytes`, default 64 KB) and max batch size (`MaxEventsPerBatch`, default 100). Over-limit → 429/413, dropped, never queued.
- **Lossy + never fails the caller**: `RecordAsync` swallows its own errors; a full channel drops + increments the dropped meter.

## PII (§1.2)

Browser payloads are PII-dense (URLs with tokens, emails, breadcrumb data, IP/UA/user). Capture is tiered + redacted, host-owned:
- `CaptureRemoteIp` (default **false** — IP is PII; opt in), `CaptureUserAgent` (default true).
- `Properties`/`Breadcrumbs` truncated (`MaxCapturedBodySize` 8 KB) and passed through the same user-owned `RedactedHeaders`-style denylist (case-insensitive; prepopulated with `authorization`/`cookie`/`password`/`token`/`secret`).
- Never logged at Info+; the redaction-safe read surfaces reduce sensitive fields on every query/endpoint/dashboard read.
- Consent/GDPR for behavioral data is the **host's** responsibility (documented) — Warp captures only what the host's script sends.

## Sinks (§8.24)

`o.Sink` (`RecordingSink`, default `Database`). `Database`/`Both` → DB recorder + flusher + dashboard. `Otel` → no recorder, no flusher, no Counter writes; the always-on meters carry the data. Meters (always emit, low-cardinality tags only, PII/`name`-explosion off meter tags §1.2):
- `warp.client.events` (Counter; tags `type`, `application`)
- `warp.client.vitals` (Histogram; tags vital `name` (bounded), `application`) — records the vital value
- `warp.client.events.dropped` (Counter — channel overflow)

Nav flag keyed on a presence marker (`IClientObservabilityMarker`) registered regardless of sink.

## Read side + dashboard

- `IClientEventQueryService` + `ClientEventQueryService<TContext>` registered by **`AddWarp` itself** (like `IEndpointQueryService`), so dashboard-only/publisher-only processes resolve it. Methods: aggregate summary per app (error rate, event counts by type, vital p75s — from folded Statistics, survive trim), recent event stream (paged, filter by app/type/session/name — raw rows, degrade to empty once swept), single event detail (stack/props/breadcrumbs, redacted).
- API: `GET {prefix}/api/client/summary` (`?application=`), `/client/events` (paged, filters), `/client/events/{id}`. Resolves in any `AddWarp` process.
- Dashboard **Client** page: aggregate tiles (error rate, vital p75 tiles with good/needs-improvement/poor coloring per Google thresholds, event volume) + recent event stream. Nav gated on `WarpAddonsInfo.Client` (true iff `AddClientObservability` ran). New screenshot `26-client` (regenerate in a demo run).

## Browser script (shipped)

Self-contained TS served at `GET {IngestPath}/client.js` (and kept as source in-repo). `installWarpClient({ endpoint, key, release?, sampleRate? })`:
- Hooks `window.onerror` + `unhandledrejection` → `error` events (message/stack).
- Web vitals via `PerformanceObserver` (LCP/FCP/TTFB/CLS native; INP best-effort — documented; a host may swap in the `web-vitals` lib and feed values through `track`-equivalent).
- Breadcrumb ring buffer (navigations/clicks/fetch) attached to errors.
- Session id (generated, `sessionStorage`).
- `warp.log(level, message, props?)` and `warp.track(name, props?)` sugar.
- Batches via `navigator.sendBeacon` on an interval + `pagehide`; sampling gate (`sampleRate`) decides per-session whether to send (consistent-session sampling).

## Migration

100% additive: 1 new table (`client_event_log`) + always-in-schema, picked up by the user's standard `dotnet ef migrations add`/`database update`. No behavior change unless `AddClientObservability` + `MapWarpClientObservability` + ≥1 ingest key are configured.

## Tests (both providers, §4.2)

- **NoDb**: `ClientEventKeys` build/parse round-trip + cardinality collapse; capture/redaction/truncation; DSN key resolution (unknown→401, key→app); CORS preflight/origin match; rate-limit + size/batch caps → drop; wire-contract parsing (each event type); web-vital bucket assignment incl. CLS scaling.
- **`[GenerateDatabaseTests]`**: recorder→flusher persistence + `ExpireAt`; Counter fold per type/vital/name; `JobMetricsSink`/`Sink=Otel` skips DB writes while meter fires; age AND count cleanup; `ClientEventQueryService` summary read from durable aggregates (seed Statistics only → survives raw-row cleanup); cardinality cap collapses beyond `MaxDistinct*`.
- **Middleware/TestHost**: POST a batch to a real mapped ingest endpoint, assert rows + counters; assert PII redaction; assert 401 on bad key.

## Docs

`website/docs/features/client-observability.md` (contrast with endpoint observability §8.21 — inbound-to-Warp-endpoints vs frontend-to-ingest; and adapters §8.19); rules §8.27; CLAUDE.md mention; releases 3.8.0 entry. **No links to CLAUDE.md/.claude from docs** (breaks Docusaurus build — reference §-numbers as plain text).
