# Warp.Adapters — Outbound Service Call Observability

- **Date:** 2026-07-09
- **Branch:** `feat/adapters`
- **Scope:** new-feature
- **Security impact:** pii-exposure (opt-in body/header capture can persist user data; §1.2 responsibility model + redaction defaults)
- **Origin:** brainstorming session 2026-07-09 (approaches A/B/C compared; converged on protocol-agnostic core + HTTP binding + optional Refit sugar)

## Summary

Every Moberg project hand-writes the same glue around outbound service calls: logging handlers, ad-hoc metrics, per-project retry config, and nothing at all for failure forensics. `Warp.Adapters` makes outbound dependencies ("adapters") first-class in Warp: named, observable, captured on failure, and visible in the dashboard — with cluster-shared rate limiting that per-process Polly cannot provide.

Design pillars (converged in brainstorming):

1. **Protocol-agnostic core** in `Warp.Core` — a call-scope API (`BeginCall(adapter, operation)` → succeed/fail) that records telemetry, `Counter` rows, and `AdapterCallLog` rows. Works for anything (SOAP proxies, vendor SDKs) via manual scopes.
2. **HTTP binding** as a new package `Warp.Adapters.Http` — a `DelegatingHandler` that creates scopes automatically for `IHttpClientFactory` clients; Polly (`Microsoft.Extensions.Http.Resilience`) handles retry/timeout; escape hatches expose `IHttpClientBuilder` raw.
3. **Optional Refit sugar** as `Warp.Adapters.Refit` — one-call registration + operation names from `RestMethodInfo`. Refit dependency isolated to this package.
4. **Cluster-shared rate limiting** reusing `RateLimitBucket` with token leasing (no per-call DB round-trip). Shared circuit breaker is an explicit fast-follow, not v1.
5. **Tiered capture** — metadata always; bodies and headers independently `None | OnFailure | Always`, truncated, with a user-owned redaction denylist (defaults provided, fully overridable per user decision).
6. **DB-only storage** — same philosophy as `JobLog`/`ServerLog`; OTel spans are the unconditional high-volume escape valve. Recording seam stays `internal` (`IAdapterCallRecorder`) so pluggable storage remains a cheap future option without a public contract today.

## Integration shapes (validated use cases)

The design must serve all of the following consumer shapes — validated against a real multi-vendor payments codebase (13 vendors, ~19 Refit interfaces, hand-rolled SOAP and GraphQL, webhook fan-out, zero pre-existing outbound observability):

1. **Refit REST** (the common case) — registration sugar via `Warp.Adapters.Refit`; existing Refit interfaces, DTOs, and auth `DelegatingHandler`s unchanged; XML-over-REST works via `RefitSettings` passthrough; per-operation names from `RestMethodInfo`.
2. **Hand-rolled SOAP over `HttpClient`** — vendor-specific envelope/WS-Security/signing code stays user-owned; integration is a named client + `WithWarpOperation(soapAction)` in the shared transport method (~2 lines for an entire vendor). No WCF channel stack required.
3. **GraphQL, single endpoint, dynamic per-tenant base URL** — all operations POST to one URL, so explicit operation names (`WithWarpOperation` / ambient scope) are the required path; the URL heuristic cannot distinguish them. `BaseUrl` is **optional**: when unset, requests carry absolute URIs and flow through the same handler pipeline unchanged.
4. **Webhook / fan-out dispatch** — tracked as an ordinary HTTP adapter using **groups**: one adapter, unbounded destination URLs (works because `BaseUrl` is optional), group = destination endpoint, operation = event type. Per-endpoint call/error-rate stats come from the generic Groups mechanism — no webhook-specific machinery. Webhook *delivery* concerns (redelivery, per-destination backoff/disable) are explicitly not this feature's job.
5. **Vendor SDKs / non-HTTP transports** — manual `BeginCall` scope; identical telemetry, capture, and dashboard treatment.
6. **mTLS / custom primary handlers** — via `ConfigureHttpClientBuilder(x => x.ConfigurePrimaryHttpMessageHandler(...))`; the config object is sugar over `IHttpClientBuilder`, never a wall.
7. **Observability-only adoption** — with no `UseResilience`/`UseSharedRateLimit`, the pipeline contains only the passive observing handler: zero behavioral change to existing calls (same timeouts, single attempt, same exceptions). This is the documented recommended rollout: observe first, add policy per adapter once the data justifies it.
8. **Adapter granularity rule** (docs guidance): **adapter = policy + health boundary (registration-time); group = runtime sub-identity (who/where — endpoint, tenant, shop, region); operation = what.** Start with one adapter per vendor; split into multiple adapters only when policies (e.g. retry on idempotent reads vs none on payment writes) or health SLOs genuinely diverge; use groups when the split dimension is a runtime value, not a policy boundary.

## Success criteria

| id | Criterion | Verification | Evidence channel | Observable |
|---|---|---|---|---|
| SC1 | Manual `BeginCall` scope with `Fail(ex)` persists an `AdapterCallLog` row (outcome `Failed`, exception type/message, machine name, trace id); `Succeed()` persists a success row under default `RecordCalls = All`; under `FailuresOnly` a success persists no row | `AdapterRecorderTestsBase` (PG + SQL Server) | test-run | generated `_PostgreSql`/`_SqlServer` tests pass |
| SC2 | Operation name precedence in the HTTP handler: request option > ambient scope > URL heuristic (numeric/GUID segments collapsed to `{id}`) | `OperationNameResolverTests` (NoDb) | test-run | NoDb tests pass |
| SC3 | Capture tiers behave: `None` writes no bodies/headers; `OnFailure` writes them only on non-success; `Always` writes on success too; truncation caps applied; redacted header values stored as `***` | `CaptureRedactionTests` (NoDb, handler-level) + `AdapterRecorderTestsBase` (DB) | test-run | tests pass |
| SC4 | Adapter calls write `Counter` rows that `CounterAggregator` collapses into `Statistic` rows queryable per adapter/operation/outcome | `AdapterCounterTestsBase` (PG + SQL Server) | test-run | tests pass |
| SC5 | Shared rate limiter: with `limit=2` and two concurrent callers pinned by `BarrierSignal`, no over-admission across two simulated processes sharing one DB; `Wait` overflow delays until a token frees; `FailFast` throws `AdapterRateLimitedException` | `AdapterRateLimitTestsBase` (PG + SQL Server) | test-run | tests pass |
| SC6 | `ExpirationCleanup` deletes `AdapterCallLog` rows past `ExpireAt` and `AdapterDefinition` rows with `LastSeenAt` older than the orphan grace | `AdapterCleanupTestsBase` (PG + SQL Server) | test-run | tests pass |
| SC7 | `GET {prefix}/api/adapters` returns registered definitions with stats; `GET .../adapters/{name}` returns operations + recent calls; `GET /api/addons` reports `adapters: true` iff `AddAdapters()` was called | `AdapterEndpointTestsBase` (PG + SQL Server) | test-run | tests pass |
| SC8 | Refit-registered adapter records operation names equal to the interface method names, via a stubbed `HttpMessageHandler` (no live network) | `RefitAdapterTests` (NoDb) | test-run | tests pass |
| SC9 | Whole solution builds analyzer-clean with the two new projects in `Warp.slnx` | `dotnet build src/Warp.slnx` | build-output | exit 0, zero warnings |
| SC10 | Adapter spans (`ActivityKind.Client`, name `{adapter}.{operation}`) and `warp.adapter.calls`/`warp.adapter.duration` meters are emitted per call | `AdapterTelemetryTests` (NoDb, AsyncLocal-sentinel listener harness per tasks/lessons.md 2026-05-07) | test-run | tests pass |
| SC11 | Frontend builds with the new Adapters pages and demo-mode data | `npm run build` in `src/ui` | build-output | exit 0 |
| SC12 | Cardinality guard: heuristic-derived operation names beyond `MaxDistinctOperations` collapse into `{other}` with a warning; explicitly-named operations are never collapsed | `OperationNameResolverTests` (NoDb) | test-run | NoDb tests pass |
| SC13 | Shared-policy conflict: a process whose shared rate-limit config differs from the persisted definition enforces the persisted config, increments `warp.adapter.config_conflicts`, and flags the definition | `AdapterRateLimitTestsBase` (PG + SQL Server) | test-run | tests pass |
| SC14 | An adapter with no `BaseUrl` routes absolute-URI requests through the full pipeline (operation naming, recording) unchanged | `CaptureRedactionTests`/`OperationNameResolverTests` (NoDb, stub handler) | test-run | NoDb tests pass |
| SC15 | Groups: a call carrying a group (option > ambient > scope) records it on the call-log row and counter key (successes included → per-group error rates); group is a span attribute but not a meter tag unless `IncludeGroupInMetrics`; group-less calls behave exactly as before | `AdapterGroupTests` (NoDb) + `AdapterCounterTestsBase` (DB×2) | test-run | tests pass |
| SC16 | Group cardinality guard: distinct group values beyond `MaxDistinctGroups` record under `{other}` with a one-time warning | `AdapterGroupTests` (NoDb) | test-run | NoDb tests pass |

## Architecture and design

### Core (`Warp.Core/Adapters/`)

- `IWarpAdapters.BeginCall(string adapter, string operation, string? group = null)` returns an `AdapterCallScope` (`IDisposable`): `Succeed()`, `Fail(Exception)`, `SetGroup(string)`, `SetCorrelation(string)` (generic caller-supplied key linking the call row to a domain record — e.g. a webhook delivery id; indexed, feature-agnostic), `Tags` dictionary-free enrichment via `SetTag(string, string)` (§8.12 — no bare `Dictionary<,>` API). Dispose without explicit outcome = failed-by-default if an exception is unwinding, else success (implementation detail: `Marshal`-free, explicit is encouraged).
- **Groups — runtime sub-identity.** A call may carry a *group*: a runtime value naming *who/where* within the adapter (destination endpoint, tenant, shop, region), orthogonal to operation (*what*). Always explicit — set via `SetGroup`/`BeginCall` (core), or `WithWarpOperation`-style request option `WithWarpGroup(string)` / ambient `WarpAdapterCall.Group(string)` (HTTP binding); never heuristic-derived. Cardinality strategy: group is recorded on the span attribute (`warp.adapter.group`), the call-log row, and **counter keys** (so per-group success *and* failure counts exist → real per-group error rates), but is **excluded from meter tags** unless the adapter opts in via `IncludeGroupInMetrics` (for bounded group sets). Guarded by `MaxDistinctGroups` (default 500): once exceeded, new group values record under a literal `{other}` with a one-time warning. No group set ⇒ identical behavior to a group-less adapter.
- **Operation vs group (definitional).** Operation is the *API-contract axis*: what the call is (`GetOrders`, `payment.completed`) — compile-time known, bounded, the same set for every group. Group is the *data axis*: for whom / to which instance (shop, merchant, region, key, webhook receiver) — runtime, unbounded, doesn't change what the call structurally is. **Litmus test:** if swapping the value changes the request's structure (route, verb, payload shape) → operation; if it only changes where it goes or on whose behalf → group. The two axes diagnose different failure modes, which is why both exist: an *operation* red across all groups = caller-side bug (malformed payload, schema drift); a *group* red across all operations = counterparty problem (dead token, downed receiver); everything red = the adapter/vendor itself. The machinery mirrors the semantics: operations are bounded → meter tags allowed, heuristic derivation allowed; groups are unbounded → excluded from metrics by default, always explicit, higher cardinality cap. This distinction goes verbatim into the feature docs.
- Recording is **asynchronous and lossy-by-design**: scopes complete into a bounded `Channel`; an `IHostedService` flusher (registered by `AddAdapters()`) drains batches into a DI scope created via `IServiceScopeFactory` (§0.5) on the user's `TContext`. Channel-full ⇒ drop + `warp.adapter.records_dropped` counter — user calls are never blocked or failed by recording. Call logs are diagnostics, not an audit trail (same stance as `JobLog`).
- The flusher also lazily upserts `AdapterDefinition.LastSeenAt` (only when stale > 5 min — no per-call write) and stamps `ExpireAt` on call-log rows from `WarpConfiguration.AdapterCallLogRetention` (default 7 days).
- OTel span + meters are emitted **unconditionally** in the scope itself (null-listener pattern, zero cost without a listener); only DB recording is gated by `AddAdapters()`.
- Builder: `AddAdapters()` on the non-generic `IWarpBuilder` receiver where possible (mirrors `AddBackgroundService<T>` precedent §2.13); per-adapter config object `WarpAdapterOptions`: `RecordCalls` (`CallRecording { All = 1, FailuresOnly = 2 }`, default `All` — controls whether a call-log **row** exists; decoupled from capture, which controls payload richness only), `CaptureRequestBodies`/`CaptureResponseBodies`/`CaptureHeaders` (`CaptureMode`; request and response bodies configured independently), `MaxCapturedBodySize` (default 8 KB), `MaxCapturedHeaderSize` (default 4 KB), `CallLogRetention?` (per-adapter override of the global retention), `RedactedHeaders` (mutable `ISet<string>`, case-insensitive, prepopulated with `Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`; user may `Add`/`Remove`/`Clear`), `EnrichCall` callback, `MaxDistinctOperations` (default 50 — **cardinality guard**: once an adapter has recorded this many distinct *heuristic-derived* operation names, further heuristic names record under a literal `{other}` plus a one-time warning; explicitly-supplied names are never collapsed. Protects counters, definition stats, and metric tag cardinality from fan-out adapters registered without explicit operation names), `MaxDistinctGroups` (default 500 — same guard for group values, which are runtime data and unbounded by nature), `GroupLabel` (dashboard display name for the group dimension, e.g. "Endpoint", "Shop"; default "Group"), `IncludeGroupInMetrics` (default false — opt-in meter tag for bounded group sets).
- Enums (§8.11, from 1): `CallRecording { All = 1, FailuresOnly = 2 }`; `CaptureMode { None = 1, OnFailure = 2, Always = 3 }`; `AdapterCallOutcome { Success = 1, Failed = 2, Throttled = 3, CircuitOpen = 4 }` (`CircuitOpen` reserved for the fast-follow); `AdapterRateLimitOverflow { Wait = 1, FailFast = 2 }`.

### Entities (`Warp.Core.Data.Entities`, §8.13; added unconditionally by `WarpModelCustomizer`, §2.11)

- `AdapterDefinition`: `Id`, `Name` (unique index), `FirstSeenAt`, `LastSeenAt`, `ConfigSummary` (non-secret display string), `SharedPolicyJson?` + `SharedPolicyHash?` (persisted shared-policy config — see rate limiter), `HasPolicyConflict` (set when a live process reports a differing shared policy; cleared on matching re-registration). No server reference (adapters run in non-server processes — decision from brainstorm). Orphan cleanup after `WarpConfiguration.AdapterDefinitionOrphanGrace` (default 2 min, mirrors §2.13).
- `AdapterCallLog`: `Id`, `AdapterName`, `Operation`, `GroupName?`, `Timestamp`, `DurationMs`, `Attempts`, `Outcome`, `StatusCode?`, `ExceptionType?`, `ExceptionMessage?` (4 KB cap), `RequestSummary`, `RequestHeaders?`, `ResponseHeaders?`, `RequestBody?`, `ResponseBody?` (all captured fields post-redaction, post-truncation), `MachineName`, `TraceId?`, `TagsJson?`, `CorrelationId?` (indexed — generic domain-record link, e.g. webhook delivery id), `ExpireAt`. Indexes: `(AdapterName, Timestamp)`, `(AdapterName, GroupName, Timestamp)`, `(AdapterName, CorrelationId)`, `ExpireAt`. Default recording policy: **a row per call, successes included** (`RecordCalls = All`); `FailuresOnly` is the volume knob for hot adapters. Capture modes only decide how much payload the row carries. This makes the call log reusable as the attempt record for higher-level features (the planned webhooks follow-up stores deliveries in its own table and reads attempts from here via `CorrelationId` — no separate attempt table).
- Both plain EF Core LINQ, no provider-native SQL (§5.1). `WarpServerContext` mirrors them automatically via `ApplyWarpModel` (§2.14); `ExpirationCleanup` (a server task) therefore operates on the server context unchanged.

### HTTP binding (`Warp.Adapters.Http`, new project)

- `opt.AddAdapter("name", a => ...)` registers a named `HttpClient`; `a.AddTypedClient<T>()` passes through to the factory's typed-client mechanism.
- **`BaseUrl` is optional.** When unset (dynamic per-tenant hosts, webhook fan-out, per-service SOAP endpoints), requests must carry absolute URIs and flow through the identical handler pipeline — observability does not depend on a fixed base address.
- **Fixed handler ordering** (not configurable): `WarpAdapterHandler` (outermost — times the logical call, records one row with final outcome + attempts) → user handlers via `a.ConfigureHttpClientBuilder(...)` → resilience handler (`a.UseResilience(...)` → `Microsoft.Extensions.Http.Resilience`) → `WarpAdapterRateLimitHandler` (innermost — one token per physical attempt, since the vendor counts attempts, not logical calls) → transport.
- Operation naming: `HttpRequestMessage.Options` extension `WithWarpOperation(string)` > `WarpAdapterCall.Operation(string)` ambient `AsyncLocal` scope > heuristic fallback (`METHOD /path` with numeric/GUID segments → `{id}`), subject to the `MaxDistinctOperations` cardinality guard. A `SOAPAction`-header fallback (for raw-HttpClient SOAP without code changes) is a noted fast-follow — `WithWarpOperation` covers it in v1. Group naming mirrors the same two explicit mechanisms (`WithWarpGroup` request option > `WarpAdapterCall.Group` ambient scope) with **no heuristic tier**.
- Escape hatches: `ConfigureHttpClient(Action<HttpClient>)`, `ConfigureHttpClientBuilder(Action<IHttpClientBuilder>)` — the adapter config is sugar over the standard builders, never a wall.

### Shared rate limiter (v1, in core with HTTP wiring in the handler)

- `a.UseSharedRateLimit(limit, perSeconds, overflow, maxWait)` — DB-backed token bucket on the existing `RateLimitBucket` entity, key `warp:adapter:{name}` (disjoint namespace, §8.6 principle). Admin overrides ride the existing `RateLimitOverride` table.
- **Token leasing:** each process leases a chunk (`LeaseSize`, default `max(1, limit/10)`) in one locked check-and-increment and spends it locally; returns to the DB only when the lease is empty. Crash loses only unspent lease tokens (under-admission — the safe direction). Row locking goes through `IWarpSqlQueries` per §1.4; reuse the existing bucket-lock method if present, otherwise add `LockRateLimitBucketByKeyAsync` to both provider implementations.
- Overflow: `Wait` = bounded async delay for the next window/lease (`maxWait`, then `AdapterRateLimitedException`); `FailFast` = throw immediately. Both surface as `Throttled` outcome on telemetry/counters/log.
- **Multi-process policy conflicts.** Shared policy (rate limit; later breaker) is *coordinated* config, unlike local policy (capture/redaction/resilience) which may legitimately differ per process. Registration persists the shared policy onto `AdapterDefinition` (last-writer-wins, so deploys converge). **[Amendment 2026-07-12 — as implemented: persistence is _first-writer-wins_, not last-writer-wins. The first registration writes the policy; a later mismatching process enforces the persisted value, logs a Warning, increments `warp.adapter.config_conflicts`, and sets `HasPolicyConflict` rather than overwriting — the conflict is deliberately _preserved_ (per SC13's "flags the definition"), so a redeploy alone cannot silently change an enforced cluster limit. Change a shared limit via a `RateLimitOverride` admin row (which takes precedence) or by clearing the persisted policy.]** Precedence at runtime: `RateLimitOverride` admin row > persisted definition > local code. During lease acquisition each process compares its local policy hash with the persisted one (no extra round-trip — it's already reading that row's neighborhood); on mismatch it **enforces the persisted policy**, logs a Warning, increments `warp.adapter.config_conflicts`, and sets `HasPolicyConflict` for the dashboard badge. Deterministic cluster behavior even mid-rolling-deploy. Docs rule: **adapter name = cluster-wide identity** — same name means stats merge and limits share by design; genuinely different dependencies get different names.

### Refit integration (`Warp.Adapters.Refit`, new project)

- `opt.AddAdapter<TApi>("name", a => ...)` wraps `AddRefitClient<TApi>` onto the adapter's client builder; `a.RefitSettings` passthrough. A tiny inner handler (or the `WarpAdapterHandler` itself) reads Refit's `RestMethodInfo` from `HttpRequestMessage.Options` for operation names. Only this package references Refit.

### Dashboard

- Backend: `IAdapterQueryService` + generic implementation in `Warp.Core/Services` (reads on `TContext` — dashboard-only processes must resolve it, §2.14 stays-on-TContext rule). Endpoints in `Warp.UI`: `GET /api/adapters`, `GET /api/adapters/{name}`, `GET /api/adapters/{name}/calls/{id}`. `WarpAddonsInfo` gains `Adapters` flag; `AddDashboard` detects registration the same way the other addon flags do.
- Frontend: `pages/adapters/AdaptersPage.tsx` (list: calls/errors/avg-latency per adapter over recent window, health badge) and `AdapterDetailPage.tsx` (per-operation table, recent-calls list, call-detail pane with captured request/response). Nav entry in `MainLayout.tsx` gated on the addons flag. Demo-mode fixtures added. Latency percentiles deliberately deferred to OTel — dashboard shows counts/error-rate/avg (brainstorm decision).
- **Groups view (generic):** when an adapter's data carries groups, the detail page shows a Groups table (calls, error %, avg latency, last failure per group — computed from per-group `Statistic` rows, so error rates have real denominators) with the adapter's `GroupLabel` as the column header, and the recent-calls list gains a group column + filter. Adapters without groups show no Groups section. Call tags (`TagsJson`) render generically in the recent-calls list and drawer.

## Constitution Check

- **§0.1/§7.5 (no push)** — no push; PR after engineer review.
- **§0.2/§6.1 (worker hot path sacred)** — untouched. No adapter code runs in `WarpWorkerService`/`WarpDispatcher*`; `ExpirationCleanup` (an `IServerTask`) gains two bounded deletes, consistent with its existing role.
- **§0.5/§2.4 (no `IServiceProvider`, no `InternalsVisibleTo`)** — recorder/flusher use `IServiceScopeFactory`; the two new packages compose against Core's public API only.
- **§2.11 (addon entities always in schema)** — both entities added unconditionally by `WarpModelCustomizer`; `AddAdapters()` gates runtime services + dashboard flag only.
- **§5.1 (no raw SQL in core)** — EF LINQ only; the one row-lock need goes through `IWarpSqlQueries` provider implementations (§1.4).
- **§5.7 (TimeProvider)** — all timestamps via injected `TimeProvider`.
- **§6.2 (Counter rows, never Statistic from hot paths)** — adapter stats write `Counter` rows; `CounterAggregator` collapses.
- **§1.2 (no PII in logs)** — metadata-only default, capture opt-in with redaction defaults; responsibility model documented (`Job.Message` precedent). This is why `security_impact = pii-exposure`, honestly stated.
- **§8.11 (enums from 1)** — all three new enums comply.
- **§8.12 (addon-prefixed, collision-safe naming)** — options named `AdapterCallOutcome`, `CaptureMode` scoped to adapter options; no bare dictionary-shaped public API.
- **§8.13 (entity namespace split)** — new entities in `Warp.Core.Data.Entities`.

## Change manifest

**Core — new (`src/core/Warp.Core/Adapters/`):** `IWarpAdapters.cs`, `AdapterCallScope.cs`, `WarpAdapterOptions.cs`, `AdapterServiceConfiguration.cs` (`AddAdapters` + core `AddAdapter` registration), `IAdapterCallRecorder.cs` (internal), `DbAdapterCallRecorder.cs`, `AdapterCallFlusher.cs`, `AdapterRateLimiter.cs`, `AdapterRateLimitedException.cs`.

**Core — new entities/enums:** `src/core/Warp.Core/Data/Entities/AdapterDefinition.cs`, `.../AdapterCallLog.cs`, `src/core/Warp.Core/Data/Enums/CaptureMode.cs`, `.../AdapterCallOutcome.cs`, `.../AdapterRateLimitOverflow.cs`.

**Core — modified:** `src/core/Warp.Core/ServiceConfiguration.cs` (entity model methods), `src/core/Warp.Core/WarpModelCustomizer.cs`, `src/core/Warp.Core/Configuration.cs` (`AdapterCallLogRetention`, `AdapterDefinitionOrphanGrace`), `src/core/Warp.Core/Logging/WarpTelemetry.cs` (+`WarpTelemetryAttributes.cs`) (adapter meters + `StartAdapterActivity`), `src/core/Warp.Core/Services/IAdapterQueryService.cs` + `AdapterQueryService.cs` (new files in existing folder).

**Providers — modified (only if no existing bucket-lock method fits):** `src/core/providers/Warp.Provider.PostgreSql/PostgresWarpSqlQueries.cs`, `src/core/providers/Warp.Provider.SqlServer/SqlServerWarpSqlQueries.cs`, `src/core/Warp.Core/Data/Queries/IWarpSqlQueries.cs`.

**Worker — modified:** `src/core/Warp.Worker/Services/ExpirationCleanup.cs`.

**New projects:** `src/core/Warp.Adapters.Http/` (`Warp.Adapters.Http.csproj`, `HttpAdapterServiceConfiguration.cs`, `WarpAdapterHandler.cs`, `WarpAdapterRateLimitHandler.cs`, `WarpAdapterCall.cs`, `WarpAdapterHttpOptions.cs`, `OperationNameResolver.cs`, `HttpRequestMessageExtensions.cs`); `src/core/Warp.Adapters.Refit/` (`Warp.Adapters.Refit.csproj`, `RefitAdapterServiceConfiguration.cs`, `RefitOperationNameReader.cs`); `src/Warp.slnx` (modified).

**UI backend — modified:** `src/core/Warp.UI/Endpoints/WarpEndpoints.cs`, `src/core/Warp.UI/Endpoints/WarpAddonsInfo.cs`.

**Frontend:** new `src/ui/src/pages/adapters/AdaptersPage.tsx`, `.../AdapterDetailPage.tsx`; modified `src/ui/src/layouts/MainLayout.tsx`, `src/ui/src/App.tsx`, `src/ui/src/api/index.ts`, `src/ui/src/types/` (adapter DTOs), `src/ui/src/demo/` (fixtures; note pre-existing unrelated `demo/adapter.ts` = axios adapter, leave alone).

**Tests (`src/tests/Warp.Tests/Adapters/`):** `AdapterScopeTests.cs` (NoDb), `OperationNameResolverTests.cs` (NoDb), `AdapterGroupTests.cs` (NoDb), `CaptureRedactionTests.cs` (NoDb), `AdapterTelemetryTests.cs` (NoDb), `RefitAdapterTests.cs` (NoDb), `AdapterRecorderTestsBase.cs`, `AdapterCounterTestsBase.cs`, `AdapterRateLimitTestsBase.cs`, `AdapterCleanupTestsBase.cs`, `AdapterEndpointTestsBase.cs` (all `[GenerateDatabaseTests]`).

**Docs/rules:** `website/docs/features/adapters.md` (new), `.claude/rules/project-specific.md` (+§8.19), `.claude/rules/architecture.md` (+§2.15), `CLAUDE.md` (ships-as list + addon list mentions).

## Test manifest

| Test file | Covers |
|---|---|
| `AdapterScopeTests.cs` (NoDb) | scope lifecycle, tags, drop-on-full-channel counter | 
| `OperationNameResolverTests.cs` (NoDb) | SC2, SC12, SC14 |
| `CaptureRedactionTests.cs` (NoDb) | SC3 (handler-level: modes, truncation, redaction incl. removed/cleared denylist), SC14 |
| `AdapterTelemetryTests.cs` (NoDb) | SC10 |
| `RefitAdapterTests.cs` (NoDb) | SC8 |
| `AdapterRecorderTestsBase.cs` (DB×2) | SC1, SC3 persisted shape |
| `AdapterGroupTests.cs` (NoDb) | SC15, SC16 |
| `AdapterCounterTestsBase.cs` (DB×2) | SC4, SC15 (per-group counters incl. successes) |
| `AdapterRateLimitTestsBase.cs` (DB×2) | SC5 (BarrierSignal, N=2 — §4.7), SC13 |
| `AdapterCleanupTestsBase.cs` (DB×2) | SC6 |
| `AdapterEndpointTestsBase.cs` (DB×2) | SC7 |

## Implementation batches

1. **Core scope API + entities + telemetry** — Adapters folder (minus rate limiter), entities/enums, model wiring, `WarpTelemetry` additions. NoDb tests: scope, telemetry. Checkpoint: build clean + NoDb green.
2. **Recorder + flusher + counters + retention** — `DbAdapterCallRecorder`, `AdapterCallFlusher`, `Counter` writes, `ExpirationCleanup` extension, config fields. DB tests: recorder, counters, cleanup. Checkpoint: PG + SQL Server green.
3. **`Warp.Adapters.Http`** — new project, handlers, operation naming, options, slnx. NoDb tests: resolver, capture/redaction (stub `HttpMessageHandler`). Checkpoint: build + NoDb green.
4. **Shared rate limiter** — leasing store + `WarpAdapterRateLimitHandler`, provider lock method if needed. DB tests: rate limit. Checkpoint: PG + SQL Server green.
5. **`Warp.Adapters.Refit`** — new project, registration sugar, operation-name reader. NoDb tests. Checkpoint: build + NoDb green.
6. **Dashboard backend** — query service, endpoints, addons flag. DB endpoint tests. Checkpoint: PG + SQL Server green.
7. **Frontend** — pages, nav, routes, api client, demo fixtures. Checkpoint: `npm run build` + screenshot sanity.
8. **Docs + rules** — feature doc, §2.15/§8.19, CLAUDE.md mentions. Must cover: inbound (`Warp.Http`) vs outbound (`Warp.Adapters.Http`) naming, the capture/PII stance, the shared rate limiter, **adopting via `AddWarp`-only (no server/worker, alongside other job systems)**, the **observe-first rollout** (no policies day one; add per adapter when data justifies), the **adapter granularity rule** (adapter = policy + health boundary; group = runtime who/where; operation = what), and the **operation-vs-group litmus test** (structure change → operation; destination/tenant change → group) with the row-red/column-red diagnosis table. Checkpoint: full suite green.

## Requirements

### Ubiquitous
- The system shall emit a `Client`-kind Activity named `{adapter}.{operation}` and increment `warp.adapter.calls` (tags: adapter, operation, outcome) for every completed adapter call scope, regardless of whether `AddAdapters()` was called.
- The system shall store captured bodies and headers only after applying the configured truncation caps and redaction set.
- The system shall write adapter statistics as `Counter` rows and never update `Statistic` rows directly from the call path.

### Event-driven
- When an adapter call completes with a non-`Success` outcome, the system shall enqueue an `AdapterCallLog` row containing adapter name, operation, duration, attempts, outcome, exception type and message, machine name, and trace id.
- When a call completes with `Success` and `RecordCalls` is `All`, the system shall enqueue an `AdapterCallLog` row for it (payload fields populated per the capture modes).
- When a call carries a correlation id, the system shall record it on the call-log row.
- When the recording channel is full, the system shall drop the record, increment `warp.adapter.records_dropped`, and return control to the caller without delay or error.
- When `ExpirationCleanup` runs, the system shall delete `AdapterCallLog` rows whose `ExpireAt` has passed and `AdapterDefinition` rows whose `LastSeenAt` is older than `AdapterDefinitionOrphanGrace`.
- When a shared-rate-limited adapter's local token lease is exhausted, the system shall acquire the next lease via a single row-locked check-and-increment on the `RateLimitBucket` row.
- When an adapter has no configured `BaseUrl`, the system shall process absolute-URI requests through the same handler pipeline (operation naming, capture, recording) as requests against a configured base address.
- When a process's shared rate-limit policy differs from the persisted `AdapterDefinition` policy, the system shall enforce the persisted policy, log a Warning, increment `warp.adapter.config_conflicts`, and set `HasPolicyConflict` on the definition.
- When a call carries a group, the system shall record the group on the call-log row, in the counter key (for both success and failure outcomes), and as a span attribute.

### State-driven
- While the cluster-wide window budget for a shared-rate-limited adapter is exhausted, the system shall admit zero further physical HTTP attempts for that adapter across all processes sharing the Warp database.

### Optional
- Where `CaptureMode.Always` is configured for bodies or headers, the system may write an `AdapterCallLog` row for successful calls.
- Where the Refit integration package is used, the system may resolve operation names from `RestMethodInfo` without user code.

### Unwanted behaviours
- If a caller supplies no operation name by option or ambient scope, then the system shall derive one by collapsing numeric and GUID path segments to `{id}`.
- If the count of distinct heuristic-derived operation names for an adapter exceeds `MaxDistinctOperations`, then the system shall record further heuristic-derived names under the literal operation `{other}` and log a warning once per adapter; explicitly-supplied names shall never be collapsed.
- If the count of distinct group values for an adapter exceeds `MaxDistinctGroups`, then the system shall record further new group values under the literal group `{other}` and log a warning once per adapter.
- If `IncludeGroupInMetrics` is not enabled, then the system shall not include the group value in any meter tag.
- If `RecordCalls` is `FailuresOnly`, then the system shall not write call-log rows for `Success` outcomes (counters and telemetry unaffected).
- If neither `UseResilience` nor `UseSharedRateLimit` is configured for an adapter, then the system shall add no behavior-modifying handlers to its pipeline — calls proceed with a single attempt, no throttling, and unchanged timeout semantics.
- If `Wait`-overflow waiting exceeds the configured `maxWait`, then the system shall throw `AdapterRateLimitedException` and record the call with outcome `Throttled`.
- If DB recording fails (flusher exception), then the system shall log at Warning and continue; adapter calls shall not observe the failure.
- If `AddAdapters()` was not called, then the system shall report `adapters: false` on `GET /api/addons` and register no recording services, while both adapter tables remain in the schema.

## Rejected alternatives (trap register)

- **trap: custom source-generated REST client (Refit-alike)** — dependency independence not worth rebuilding/maintaining route templating, binding, serialization; per-operation naming achievable from Refit's `RestMethodInfo`. Revisit only if Refit stagnates or `[Idempotent]`-class semantics are needed.
- **trap: mediator-integrated adapters (calls as `IRequest`)** — pipeline behaviors are `IJob`+`IJobContext`-constrained; reschedule semantics meaningless for inline request-path calls; forces ceremony.
- **trap: Polly-only rate limiting** — per-process; N servers multiply the vendor limit. The cluster-shared limiter is the feature's strongest differentiator.
- **trap: per-call DB round-trip in the limiter** — unacceptable latency/load; token leasing amortizes (accepted cost: mild burstiness, lost unspent tokens on crash — under-admission, safe direction).
- **trap: pluggable call-log storage (public `IAdapterCallStore`)** — the read side (dashboard queries, paging, retention) would balloon the contract; DB-only matches `JobLog` precedent; OTel is the scale-out valve; internal seam kept for cheap future promotion.
- **trap: automatic replay of failed calls** — replaying non-idempotent POSTs is a foot-gun; v1 records only. Replay requires explicit idempotency opt-in (future).
- **trap: mandatory `Authorization` redaction** — rejected by engineer; §1.2 responsibility model — defaults provided, fully user-owned.
- **trap: `LastServerName` on `AdapterDefinition`** — adapters run in non-server processes; a single "last" value is misleading. Dropped.
- **trap: shared circuit breaker in v1** — same store pattern as the limiter; deferred as fast-follow to keep v1 shippable.
- **trap: p95 latency in dashboard from counters** — count-based counters can't produce percentiles; avg in dashboard, percentiles via OTel histograms.

## Risks and assumptions

- `[ASSUMED]` Polly attempt count is readable from the resilience pipeline for the `Attempts` column; if not cheaply obtainable, `Attempts` records 1 for logical calls and the limitation is documented. Verify in batch 3.
- `[ASSUMED]` An existing `IWarpSqlQueries` bucket-lock method is reusable for lease acquisition; otherwise add `LockRateLimitBucketByKeyAsync` to both providers (manifest already includes the files).
- `[VERIFIED:src/core/Warp.Core/RateLimit/RateLimitStore.cs]` `RateLimitBucket` store exists and commits inside pipeline scope; the adapter limiter is a separate store with its own scope — it must not reuse `RateLimitStore`'s handler-scope commit semantics.
- `[ASSUMED]` `Counter` row naming supports free-form adapter keys without schema change; confirm `CounterAggregator` grouping in batch 2.
- Risk: `WarpAdapterHandler` measuring outside the resilience handler means per-attempt latency is invisible in Warp (visible in OTel via `Microsoft.Extensions.Http.Resilience` telemetry). Accepted: one row per logical call keeps dashboards comparable.
- Risk: `AsyncLocal` ambient operation naming does not flow across manually created threads; documented — request-option takes precedence and is the reliable path.
- Risk: name `Warp.Adapters.Http` vs existing inbound `Warp.Http` could confuse; docs must state inbound vs outbound explicitly.
- Risk: `RecordCalls = All` default writes a call-log row per call — meaningful volume on hot adapters. Mitigations: batched channel flushing, retention caps, per-adapter `FailuresOnly`; docs call this out.
- dirty-worktree: none (`git status --porcelain` clean at spec time).

## Prior Work Check

- `grep -r "AdapterCallLog|IWarpAdapters|AddAdapters" src/core` — no hits; no existing adapter implementation.
- `git log --all --grep="adapter" -i` — no prior adapter branches/commits.
- Memory `project_http_library_endpoints_blocked` reviewed — concerns *inbound* endpoints from referenced libraries; unrelated to outbound adapters.
- `scripts/learnings.sh`, `scripts/constitution-digest.sh`, `scripts/lint-ears.sh`, `.claude/schemas/` absent in this repo — fallbacks used: `tasks/lessons.md` read directly (AsyncLocal + ActivityListener lessons folded into SC10 test design), Critical Rules cited from CLAUDE.md, EARS section hand-checked (each bullet has a falsifiable counter-example).

## Open questions

None blocking. Deferred by decision: webhook delivery — now **implemented** as `Warp.Adapters.Webhooks` (spec `docs/specs/2026-07-10-warp-webhooks.md`, docs `website/docs/features/webhooks.md`), building on this spec's `CorrelationId`, groups, and `RecordCalls`; deliveries in one `WebhookDelivery` table, attempts as adapter call rows keyed by `CorrelationId` — no separate attempt table. Shared circuit breaker (fast-follow), replay-with-idempotency, WCF/gRPC binding packages, `SOAPAction` operation-name fallback, dashboard latency percentiles, and a **minimal GraphQL client generator** (`Warp.Adapters.GraphQL`: `[GraphQlOperation]` document + variables from method parameters + mandatory `errors`-array checking) — a *designed* fast-follow, distinct from the rejected REST Refit-alike because no lightweight GraphQL equivalent of Refit exists; hand-written clients over the named adapter client are the v1 path.
