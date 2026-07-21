# Warp.Adapters.Webhooks — Implementation Plan

Spec: `docs/specs/2026-07-10-warp-webhooks.md` (+ `.json` sidecar — authoritative). Scope: new-feature. Security impact: pii-exposure + secrets-change (payloads stored; per-delivery signing secrets at rest, redacted on read). Rigor: **MAX** (6 batches, ~43-file manifest, security ≠ none) → subagent-per-batch, full Stage 2 review.

Depends on: Warp.Adapters v1 (implemented, uncommitted on this branch) — uses `CorrelationId`, groups, `RecordCalls`, per-adapter `CallLogRetention`, `AdapterRegistrationEntry` seam. Out of scope (hard): subscriptions/fan-out modeling, endpoint auto-disable, per-endpoint keyed rate limiting, FIFO ordering, receiving webhooks, worker hot path (§0.2).

## Commands

Same as adapters plan: `dotnet build src/Warp.slnx` (analyzer-clean), `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj -- --filter-namespace Warp.Tests.Webhooks` / `--filter-trait "Category=NoDb"`, `cd src/ui && npm run build`, `dotnet format --verbosity quiet`. Never `--nologo`/`-v` after `--`.

## Batches

### W1 — Entity + converter + Core services
**Files:** `src/core/Warp.Core/Data/Entities/WebhookDelivery.cs`, `src/core/Warp.Core/Data/Enums/{WebhookDeliveryStatus, WebhookSigning}.cs`, `src/core/Warp.Core/Data/Converters/RetryScheduleConverter.cs`, `src/core/Warp.Core/Webhooks/{IWebhookQueryService, WebhookQueryService, IWebhookCommandService, WebhookCommandService}.cs`, `ServiceConfiguration.cs` (entity model method + service registration), `Configuration.cs` (`WebhookDeliveryRetention` default 30d), tests `RetryScheduleConverterTestsBase.cs` + `WebhookDeliveryPersistenceTestsBase.cs`.
**Key rules:** entity always-in-schema (§2.11), namespace split (§8.13), enums from 1 (§8.11); converter stores seconds array (`"[60,600,3600,21600]"`), ValueConverter + ValueComparer in `Data/Converters/`; **§8.16 lesson: explicit roundtrip tests — empty list, single entry, multi-hour spans, both providers**; delivery row is self-contained (schedule/headers/payload/signing/secret/success-codes columns); indexes `(Status, NextAttemptAt)`, `Reference`, `(EventType, CreatedAt)`, `ExpireAt`; clamp persisted string fields to column caps at the single build choke point (adapters lesson 2026-07-12); secrets/`Authorization`-class headers redacted in every query-service projection.
**Acceptance:** WSC1; converter roundtrip green PG + SQL Server; build analyzer-clean.

### W2 — Dispatcher + executor
**Files:** `src/core/Warp.Adapters.Webhooks/{Warp.Adapters.Webhooks.csproj, WebhookServiceConfiguration.cs, IWebhookDispatcher.cs, WebhookSend.cs, WebhookDispatcher.cs, ExecuteWebhookDelivery.cs, IWebhookDeliveryExhaustedHandler.cs}`, `src/Warp.slnx`, `WarpTelemetry.cs` (webhook counters), tests `WebhookExecutionTestsBase.cs`.
**Key rules:** delivery is the state machine — **executor jobs ALWAYS complete** (every attempt exception caught → recorded as attempt failure; job never fails); dedicated queue `warp:webhooks`; retries = new job with `ScheduleTime = NextAttemptAt` (rides `ScheduledJobActivation`, §2.8 — no timers, no scans); HTTP leg through a `warp-webhooks` adapter registration (`RecordCalls = All`, `CaptureResponseBodies = Always`, `CaptureRequestBodies = None`, `CallLogRetention` aligned to delivery retention), group = delivery group, operation = event type, `SetCorrelation(deliveryId)`; `(RetrySchedule, AttemptCount)` fully determines the plan — schedule column never mutated; `[]` schedule = single attempt; SuccessCodes default any-2xx; exhausted → status + `IWebhookDeliveryExhaustedHandler` invoked once, exceptions logged never thrown (adapters lesson: instrumentation/callbacks never out-throw); status guard against concurrent double-attempt; worker hot path untouched — executor is an ordinary source-generated job handler.
**Acceptance:** WSC2–WSC5 green PG + SQL Server (stub HttpMessageHandler, no live network).

### W3 — Signing
**Files:** `src/core/Warp.Adapters.Webhooks/{IWebhookSigner, StandardWebhooksSigner}.cs`, tests `WebhookSigningTests.cs` (NoDb).
**Key rules:** Standard Webhooks: HMAC-SHA256 over `{webhook-id}.{webhook-timestamp}.{payload}`, headers `webhook-id`/`webhook-timestamp`/`webhook-signature` (v1 prefix per spec), `webhook-id` = `EventId` (stable across retries); verify against published test vectors (`[ASSUMED]` — resolve here); `None` adds nothing; `Custom` resolves registered `IWebhookSigner`, **missing registration fails at `AddWebhooks` time, not send time**.
**Acceptance:** WSC6 green.

### W4 — Redelivery + endpoints + retention
**Files:** `WebhookCommandService.cs` (redeliver guard: `Delivered`/`Exhausted` only), `src/core/Warp.UI/Endpoints/{WarpEndpoints, WarpAddonsInfo}.cs` (`GET /api/webhooks`, `GET /api/webhooks/{id}`, `POST /api/webhooks/{id}/redeliver`, `webhooks` addons flag), `src/core/Warp.Worker/Services/ExpirationCleanup.cs` (expired deliveries), tests `WebhookCommandServiceTestsBase.cs` + `WebhookEndpointTestsBase.cs` + `WebhookCleanupTestsBase.cs`.
**Key rules:** endpoint tests drive **TestServer routes, not the query service** (adapters lesson TR2); addons flag gates on a webhooks-only registration marker (sagas pattern); redeliver on `Pending` rejected without side effects; attempt timeline = `AdapterCallLog WHERE CorrelationId = deliveryId` via the adapters query surface.
**Acceptance:** WSC7–WSC9 green PG + SQL Server.

### W5 — Frontend
**Files:** `src/ui/src/pages/webhooks/{WebhooksPage, WebhookDetailPage}.tsx`, `MainLayout.tsx` (nav gated on `addons.webhooks`), `App.tsx`, `api/index.ts`, `types/webhooks.ts`, `types/index.ts` (addons flag), `demo/data/webhooks.ts` + `demo/adapter.ts` routes.
**Key rules:** per artifact Screen 5 — deliveries list (status/event/reference filters, summary tiles), detail with self-contained contract display (schedule, success codes, signing mode — secret always `***`), attempt timeline from adapter call rows, Redeliver action; reuse existing components; demo deterministic; wire demo routes in the same pass (adapters lesson — fixtures without routing failed acceptance).
**Acceptance:** WSC11; nav hidden when flag false; demo renders list + detail + redeliver affordance.

### W6 — Docs + rules
**Files:** `website/docs/features/webhooks.md` (boundary table verbatim, migration path from hand-rolled implementations, Hangfire coexistence, cadence precision note, secrets-at-rest stance), `.claude/rules/project-specific.md` (+§8.20), `CLAUDE.md` (ships-as), `docs/specs/2026-07-09-warp-adapters.md` cross-link check.
**Acceptance:** WSC10 (full suite green) + npm build; docs cover the required sections.

## Verification map

WSC1–WSC11 per spec test manifest; final gate = full suite both DBs + `npm run build` + behavioral diff. Every batch: `dotnet format` + analyzer-clean build. No push (§0.1).

## Deviations from standard flow

- Same as adapters: MTK workflow scripts absent → todo checkboxes; silent-failure lens via prompted general-purpose agent at Stage 2.
- Workflow dispatch: inline batch data in script (no `args`), LF-only when patching (memory: workflow-dispatch-hardening).
