# Warp.Adapters.Webhooks — Durable Outbound Webhook Delivery

- **Date:** 2026-07-10
- **Branch:** `feat/adapters` (follow-up to `docs/specs/2026-07-09-warp-adapters.md` — **depends on adapters v1**: `CorrelationId`, groups, `RecordCalls`)
- **Scope:** new-feature
- **Security impact:** pii-exposure + secrets-change (webhook payloads may carry user data; per-delivery signing secrets are stored at rest on the delivery row)
- **Origin:** design converged in-session 2026-07-10 (library research + real-world implementation mapping)

## Summary

There is no maintained embedded .NET library for *sending* webhooks — Microsoft's ASP.NET WebHooks is archived, and the serious options (Svix, Hookdeck Outpost, Convoy) are standalone Rust/Go services you deploy and operate. Teams therefore hand-roll delivery on top of their job scheduler, accumulating the same defects (per-attempt correlation ids, 200-only success checks, hand-coupled backoff switches, no redelivery, no dead-letter). `Warp.Adapters.Webhooks` makes durable webhook delivery a Warp feature: **the host owns subscriptions and fan-out; Warp owns everything after `SendAsync`** — durability, signing, scheduled retries, tracking, redelivery, and a dedicated dashboard section. Positioning mirrors Warp itself: durable delivery on the Postgres/SQL Server you already run, no extra infrastructure.

Design pillars:

1. **The delivery, not the job, is the state machine.** Executor jobs always complete; failure lives on `WebhookDelivery` (`Pending → Delivered | Exhausted`). No failed jobs, ever — webhooks are visible in their own dashboard section, not the Jobs UI.
2. **Self-contained delivery row.** Everything needed to execute to completion is on the row at `SendAsync` time: URL, headers, payload, retry schedule, success codes, signing mode + secret, group, reference. Nothing is looked up from ambient config mid-flight; a config deploy never reshapes in-flight deliveries.
3. **Per-send cadence.** `RetrySchedule` is a plain `IReadOnlyList<TimeSpan>` on the send request (N entries = N retries; `[]` = single attempt; omitted = library built-in `[1m, 10m, 1h, 6h]`). There is deliberately **no app-level schedule setting** — cadence is a property of what's being delivered, which only the host knows.
4. **Attempts are adapter calls.** The HTTP leg goes through a `warp-webhooks` adapter (group = endpoint, operation = event type, `CorrelationId` = delivery id). No separate attempt table — the attempt timeline is `AdapterCallLog WHERE AdapterName='warp-webhooks' AND CorrelationId=@deliveryId`. One capture/redaction/retention machinery; aggregate health appears on the Adapters page for free.
5. **No timers, no scans.** The executor job *is* the clock: first attempt enqueued immediately (signal-driven pickup §6.3), each retry is a job in `State.Scheduled` activated by `ScheduledJobActivation` (§2.8). `NextAttemptAt` on the row is display metadata, never a scan target. Precision is delay + up to `ScheduledActivationInterval` (~5s) + worker pickup — documented as fine for backoff, not a precision scheduler.
6. **Standard Webhooks signing, pluggable.** `WebhookSigning { None = 1, StandardWebhooks = 2, Custom = 3 }`. `StandardWebhooks` = HMAC-SHA256 over `{webhook-id}.{webhook-timestamp}.{payload}`, headers `webhook-id`/`webhook-timestamp`/`webhook-signature`, `webhook-id` = `EventId` (stable across retries — the consumer's idempotency key). `None` supports hosts with existing body-embedded signatures (migration path); `Custom` resolves a registered `IWebhookSigner`.

## Integration boundary (normative)

| Host owns | Warp owns |
|---|---|
| Subscriptions (who receives what), their storage, portals, permissions | `WebhookDelivery` lifecycle after `SendAsync` |
| Fan-out (which subscriptions match an event) | Executor jobs, retry scheduling, exhaustion |
| Payload building + serialization (Payload is an opaque string) | Signing at attempt time (per configured mode) |
| Endpoint enable/disable decisions | `OnDeliveryExhausted` signal (host callback) |
| Secrets lifecycle in its own tables | Secret carried on the delivery row, redacted on all read surfaces |
| Legacy body-embedded signing (via `Signing = None`) | Standard Webhooks header signing |

## Success criteria

| id | Criterion | Verification | Evidence channel | Observable |
|---|---|---|---|---|
| WSC1 | `SendAsync` persists a self-contained delivery row; `RetrySchedule` converter roundtrips empty, single-entry, and multi-hour lists identically on both providers | `WebhookDeliveryPersistenceTestsBase` (PG + SQL Server) | test-run | generated tests pass |
| WSC2 | Successful attempt ⇒ delivery `Delivered`; an `AdapterCallLog` row exists with `AdapterName='warp-webhooks'`, `CorrelationId` = delivery id, operation = event type, group = configured group; the executor job completes (no failed job rows) | `WebhookExecutionTestsBase` (integration, stub `HttpMessageHandler`) | test-run | tests pass |
| WSC3 | Failed attempt with retries left ⇒ delivery stays `Pending`, `AttemptCount` incremented, `NextAttemptAt` = now + `schedule[N-1]`, a `Scheduled` executor job exists with matching `ScheduleTime`; executor job itself completes | `WebhookExecutionTestsBase` | test-run | tests pass |
| WSC4 | Schedule exhausted ⇒ delivery `Exhausted`, registered `IWebhookDeliveryExhaustedHandler` invoked once **per exhaustion transition; at-least-once under crash/retry edges, post-commit** [amended 2026-07-12, review W-1: the handler fires *after* the `Exhausted` row commits — never ahead of it (so a rollback can't re-fire) — and is re-invoked if the process crashes between commit and callback; handlers must be idempotent], executor job completes; `[]` schedule ⇒ single attempt then `Exhausted` | `WebhookExecutionTestsBase` | test-run | tests pass |
| WSC5 | `SuccessCodes` honored: default treats any 2xx as delivered; explicit `[200]` treats 202 as failure | `WebhookExecutionTestsBase` | test-run | tests pass |
| WSC6 | `StandardWebhooks` signing emits `webhook-id`/`webhook-timestamp`/`webhook-signature` matching a known Standard Webhooks test vector; `None` adds no headers; `Custom` invokes the registered `IWebhookSigner` | `WebhookSigningTests` (NoDb) | test-run | NoDb tests pass |
| WSC7 | `Redeliver(deliveryId)` on a `Delivered`/`Exhausted` delivery resets it to `Pending` and enqueues an attempt; redelivering a `Pending` delivery is rejected | `WebhookCommandServiceTestsBase` (PG + SQL Server) | test-run | tests pass |
| WSC8 | `IWebhookQueryService` filters by status/event type/reference/group/date; `GET {prefix}/api/webhooks` + detail endpoint return deliveries with attempt timelines; `/api/addons` reports `webhooks` | `WebhookEndpointTestsBase` (PG + SQL Server) | test-run | tests pass |
| WSC9 | `ExpirationCleanup` deletes `WebhookDelivery` rows past `ExpireAt` | `WebhookCleanupTestsBase` (PG + SQL Server) | test-run | tests pass |
| WSC10 | Solution builds analyzer-clean with the new project in `Warp.slnx` | `dotnet build src/Warp.slnx` | build-output | exit 0, zero warnings |
| WSC11 | Frontend builds with the Webhooks pages and demo fixtures | `npm run build` in `src/ui` | build-output | exit 0 |

## Architecture and design

### Entity (`Warp.Core.Data.Entities`, §8.13; added unconditionally by `WarpModelCustomizer`, §2.11)

`WebhookDelivery` — the only new table (attempts live in `AdapterCallLog`):

```
Id (Guid)
EventType, EventId          what happened; EventId = stable idempotency key → webhook-id header
Url, HeadersJson            destination + per-delivery headers (redacted on all read surfaces)
GroupName?                  endpoint/tenant → forwarded as the adapter group
Reference?                  host's opaque link to its own subscription/definition (indexed)
PayloadJson                 exact bytes to send, host-serialized; stored once
SigningMode                 WebhookSigning { None = 1, StandardWebhooks = 2, Custom = 3 }
Secret?                     signing secret (self-contained row; redacted everywhere on read)
RetrySchedule               IReadOnlyList<TimeSpan> ↔ text column "[60,600,3600,21600]" (seconds)
                            via ValueConverter + ValueComparer in Data/Converters (§8.16 roundtrip tests mandated)
SuccessCodesJson?           null = any 2xx
Status                      WebhookDeliveryStatus { Pending = 1, Delivered = 2, Exhausted = 3 }
AttemptCount, NextAttemptAt?
CreatedAt, ExpireAt         retention from WarpConfiguration.WebhookDeliveryRetention (default 30 days)
```

Indexes: `(Status, NextAttemptAt)`, `Reference`, `(EventType, CreatedAt)`, `ExpireAt`. Execution never mutates the schedule column — `(RetrySchedule, AttemptCount)` fully determines the remaining plan; attempt N's failure schedules delay `schedule[N-1]`; exhausted when `AttemptCount > schedule.Count`.

### Packaging (split forced by dependency rules)

- **`Warp.Core/Webhooks/`**: entity, enums, `RetryScheduleConverter`, `IWebhookQueryService` + implementation, `IWebhookCommandService` (redeliver) + implementation. In Core because `WarpModelCustomizer` must reference the entity (§2.11) and dashboard-only processes must resolve the query service (§2.13 precedent, `IBackgroundServiceQueryService`).
- **`Warp.Adapters.Webhooks`** (new project, references `Warp.Adapters.Http`): `IWebhookDispatcher` + `WebhookSend`, the executor job contract + handler, `IWebhookSigner` + `StandardWebhooksSigner`, `IWebhookDeliveryExhaustedHandler`, `WebhookServiceConfiguration.AddWebhooks(...)`. Separate package because the executor needs `IHttpClientFactory` — the same dependency line that keeps HTTP out of Core for adapters.
- `AddWebhooks(w => ...)` configures **infrastructure only**: `WebhookDeliveryRetention`, the exhausted-handler registration, and the `warp-webhooks` adapter it registers automatically (`RecordCalls = All`, `CaptureResponseBodies = Always`, `CaptureRequestBodies = None` — payload already lives on the delivery — `CallLogRetention` aligned to delivery retention). Everything describing *a delivery* rides the `WebhookSend`.

### Execution flow

```
SendAsync(WebhookSend) ─→ WebhookDelivery row (Pending) + executor job (Enqueued, queue "warp:webhooks")
  executor job (always completes):
    sign per SigningMode ─→ POST via warp-webhooks adapter
       (operation = EventType, group = GroupName, SetCorrelation(deliveryId))
    ├─ success (per SuccessCodes) ─→ Status = Delivered
    ├─ failure, retries left ─→ AttemptCount++, NextAttemptAt set,
    │                            next executor job published with ScheduleTime = NextAttemptAt
    └─ exhausted ─→ Status = Exhausted, IWebhookDeliveryExhaustedHandler invoked (exceptions logged, never thrown)
  Redeliver(deliveryId) [Delivered/Exhausted only] ─→ Status = Pending + immediate executor job
```

- The executor is an ordinary source-generated job handler — worker hot path untouched (§0.2/§6.1). All exceptions inside an attempt are caught and recorded as attempt failure; the job itself never fails. Overlap protection comes from worker claim semantics plus a status guard on the delivery write.
- **Requires a Warp server** (worker) somewhere in the deployment — unlike adapters-only observability, webhooks execute jobs. Documented as the adoption prerequisite; coexistence with other job systems (e.g. Hangfire) is a documented supported shape.

### Telemetry

`warp.webhooks.deliveries` (counter, tags: outcome `delivered|exhausted`), `warp.webhooks.attempts` (counter, tags: outcome), `warp.webhooks.redeliveries` (counter). The HTTP leg's spans/duration/error counters come from the adapter layer — not duplicated.

### Dashboard

Own nav item ("Webhooks", flag on `GET /api/addons`, same detection pattern as sagas): deliveries list (filters: status, event type, reference, group, date) + summary tiles (deliveries, delivered %, pending, exhausted) + delivery detail (self-contained contract shown — schedule, success codes, signing mode; attempt timeline from `AdapterCallLog` via `CorrelationId`; **Redeliver** action). Secrets and `Authorization`-class headers render redacted. Endpoints in `Warp.UI` over the Core query/command services, so hosts can equally serve the same data through their own permission-gated portals.

## Constitution Check

- **§0.1/§7.5** — no push; PR after review.
- **§0.2/§6.1** — executor is a normal job handler; no worker/dispatcher changes. `ExpirationCleanup` gains one bounded delete.
- **§0.5/§2.4** — no `IServiceProvider`; scoped services via constructor injection; flusher-style needs use `IServiceScopeFactory`.
- **§2.11** — `WebhookDelivery` added unconditionally by `WarpModelCustomizer`; `AddWebhooks` gates runtime only.
- **§5.1/§5.7/§5.10** — EF LINQ only; `TimeProvider` for all timestamps; async EF with `CancellationToken`.
- **§6.2** — webhook counters via `Counter` rows where dashboard stats need them; meters for OTel.
- **§8.11** — `WebhookSigning`, `WebhookDeliveryStatus` from 1.
- **§8.13** — entity in `Warp.Core.Data.Entities`.
- **§8.16 (lesson)** — `RetryScheduleConverter` ships with explicit roundtrip tests (empty/single/multi-hour) on both providers; non-primitive persistence is a known burn site.
- **§1.2** — payloads are user data: stored (that's the feature) but never logged at Info+; secrets/`Authorization` headers redacted on every read surface. Hence `security_impact` above.

## Change manifest

**Core — new (`src/core/Warp.Core/Webhooks/`):** `IWebhookQueryService.cs`, `WebhookQueryService.cs`, `IWebhookCommandService.cs`, `WebhookCommandService.cs`. **Core — new entity/enums/converter:** `src/core/Warp.Core/Data/Entities/WebhookDelivery.cs`, `src/core/Warp.Core/Data/Enums/WebhookDeliveryStatus.cs`, `.../WebhookSigning.cs`, `src/core/Warp.Core/Data/Converters/RetryScheduleConverter.cs`. **Core — modified:** `ServiceConfiguration.cs` (entity model method + query/command service registration), `WarpModelCustomizer.cs`, `Configuration.cs` (`WebhookDeliveryRetention`), `Logging/WarpTelemetry.cs`.

**New project (`src/core/Warp.Adapters.Webhooks/`):** `Warp.Adapters.Webhooks.csproj`, `WebhookServiceConfiguration.cs` (`AddWebhooks`), `IWebhookDispatcher.cs`, `WebhookSend.cs`, `WebhookDispatcher.cs`, `ExecuteWebhookDelivery.cs` (job contract + handler), `IWebhookSigner.cs`, `StandardWebhooksSigner.cs`, `IWebhookDeliveryExhaustedHandler.cs`; `src/Warp.slnx` (modified).

**Worker — modified:** `src/core/Warp.Worker/Services/ExpirationCleanup.cs`.

**UI backend — modified:** `src/core/Warp.UI/Endpoints/WarpEndpoints.cs`, `.../WarpAddonsInfo.cs`.

**Frontend:** new `src/ui/src/pages/webhooks/WebhooksPage.tsx`, `.../WebhookDetailPage.tsx`; modified `MainLayout.tsx`, `App.tsx`, `api/index.ts`, `types/webhooks.ts` (new), `demo/` fixtures.

**Tests (`src/tests/Warp.Tests/Webhooks/`):** `WebhookSigningTests.cs` (NoDb), `RetryScheduleConverterTestsBase.cs`, `WebhookDeliveryPersistenceTestsBase.cs`, `WebhookExecutionTestsBase.cs`, `WebhookCommandServiceTestsBase.cs`, `WebhookEndpointTestsBase.cs`, `WebhookCleanupTestsBase.cs` (all `[GenerateDatabaseTests]` except signing).

**Docs/rules:** `website/docs/features/webhooks.md` (new — includes the host/Warp boundary table, migration guidance from hand-rolled implementations, Hangfire-coexistence note), `.claude/rules/project-specific.md` (+§8.20), `CLAUDE.md` (ships-as list), `docs/specs/2026-07-09-warp-adapters.md` (cross-link only).

## Test manifest

| Test file | Covers |
|---|---|
| `WebhookSigningTests.cs` (NoDb) | WSC6 |
| `RetryScheduleConverterTestsBase.cs` (DB×2) | WSC1 (converter roundtrip) |
| `WebhookDeliveryPersistenceTestsBase.cs` (DB×2) | WSC1 |
| `WebhookExecutionTestsBase.cs` (DB×2, integration, stub `HttpMessageHandler` — no live network) | WSC2, WSC3, WSC4, WSC5 |
| `WebhookCommandServiceTestsBase.cs` (DB×2) | WSC7 |
| `WebhookEndpointTestsBase.cs` (DB×2) | WSC8 |
| `WebhookCleanupTestsBase.cs` (DB×2) | WSC9 |

## Implementation batches

1. **Entity + converter + Core services** — entity/enums/converter, model wiring, retention config, query/command services. Checkpoint: converter roundtrip + persistence tests green on PG + SQL Server.
2. **Dispatcher + executor** — new project, `SendAsync`, executor handler, retry scheduling, exhausted callback, telemetry. Checkpoint: execution tests green (success/failure/exhaust/empty-schedule/success-codes).
3. **Signing** — `StandardWebhooksSigner` against published test vectors, `None`/`Custom` paths. Checkpoint: signing tests green.
4. **Redelivery + endpoints + addons flag** — command service guard, UI endpoints. Checkpoint: command + endpoint tests green.
5. **Frontend** — pages, nav, routes, demo fixtures. Checkpoint: `npm run build` exit 0.
6. **Docs + rules** — feature doc (boundary table, migration path, coexistence, cadence precision note), §8.20, cross-links. Checkpoint: full suite green.

## Requirements

### Ubiquitous
- The system shall store every field required to execute a delivery to completion on the `WebhookDelivery` row at `SendAsync` time.
- The system shall redact `Secret` and configured sensitive headers on every read surface (query service, endpoints, dashboard).
- The system shall record every delivery attempt as an `AdapterCallLog` row with `CorrelationId` equal to the delivery id.

### Event-driven
- When `SendAsync` is called, the system shall persist a `Pending` delivery and enqueue an executor job immediately.
- When an attempt succeeds per the delivery's success codes, the system shall set `Status = Delivered` and schedule no further jobs.
- When an attempt fails with retries remaining, the system shall increment `AttemptCount`, set `NextAttemptAt` from the stored schedule, and publish the next executor job with a matching `ScheduleTime`.
- When the stored schedule is exhausted by a failed attempt, the system shall set `Status = Exhausted` and invoke the registered `IWebhookDeliveryExhaustedHandler` exactly once for that delivery.
- When `Redeliver` is invoked on a `Delivered` or `Exhausted` delivery, the system shall reset it to `Pending` and enqueue an immediate executor job.
- When `ExpirationCleanup` runs, the system shall delete `WebhookDelivery` rows whose `ExpireAt` has passed.

### State-driven
- While a delivery is `Pending` with a live executor job, the system shall not permit a concurrent second attempt for the same delivery (worker claim + status guard).

### Optional
- Where `SigningMode = StandardWebhooks`, the system may add only the three `webhook-*` headers to the outgoing request.
- Where a group is set on the send, the system may forward it as the adapter group for the attempt rows.

### Unwanted behaviours
- If an attempt throws any exception, then the system shall record it as a failed attempt and complete the executor job successfully — executor jobs shall never enter a failed state.
- If `RetrySchedule` is an empty list, then the system shall make exactly one attempt and set `Status = Exhausted` on failure.
- If the exhausted handler throws, then the system shall log at Warning and leave the delivery `Exhausted` — the failure shall not propagate to the job.
- If `Redeliver` is invoked on a `Pending` delivery, then the system shall reject the call without side effects.
- If `SigningMode = Custom` and no `IWebhookSigner` is registered, then the system shall fail at `AddWebhooks` registration time, not at send time.

## Rejected alternatives (trap register)

- **trap: deliveries as plain Warp jobs with failed-jobs-as-dead-letter** — rejected by engineer: webhook failures must not pollute the Jobs UI; the delivery row is the state machine and executor jobs always complete.
- **trap: separate `WebhookAttempt` table** — attempts are adapter calls; a second table duplicates capture/redaction/retention machinery. Merged into `AdapterCallLog` via `CorrelationId` (requires adapters v1 `RecordCalls = All` default).
- **trap: app-level retry schedule config** — cadence is a property of the delivery, not the app; per-send `IReadOnlyList<TimeSpan>`, no app-level knob to disagree with itself across processes.
- **trap: mandatory Standard Webhooks signing** — breaks consumers of existing hand-rolled formats (body-embedded HMACs); `None` is the migration path, standard headers adopt additively.
- **trap: webhook subscription/fan-out modeling in Warp** — the host owns who-receives-what; Warp modeling it would duplicate every host's tenancy model badly.
- **trap: auto-disable failing endpoints** — endpoint lifecycle belongs to the host's subscription record; Warp signals (`OnDeliveryExhausted`), host decides.
- **trap: dedicated delivery-pump server task scanning `NextAttemptAt`** — reinvents the scheduler; scheduled executor jobs ride `ScheduledJobActivation` with zero new machinery and no idle scans.
- **trap: resolving secrets at attempt time via host callback** — breaks self-containment (attempt fails if host data moved); secret rides the row, redacted on read.
- **trap: TimeSpan-string serialization for the schedule** — format-sensitive, unreadable; JSON array of seconds via ValueConverter.
- **trap: FIFO/ordering guarantees across deliveries** — Svix-class feature, not promised; independent deliveries execute independently.

## Risks and assumptions

- `[ASSUMED]` Standard Webhooks published test vectors are sufficient to verify signer correctness; validate in batch 3.
- `[VERIFIED:docs/specs/2026-07-09-warp-adapters.md]` adapters v1 provides `CorrelationId` (indexed), groups, `RecordCalls`, per-adapter `CallLogRetention` — all prerequisites of this design.
- Risk: secrets at rest on delivery rows — accepted (self-containment) with redaction everywhere on read + docs guidance; hosts with stricter requirements can use `Signing = None` and sign in their own payload-building code.
- Risk: attempt rows and delivery rows expire independently (no FK cascade); mitigated by aligning `warp-webhooks` adapter `CallLogRetention` with `WebhookDeliveryRetention` by default.
- Risk: retry timing precision is delay + up to ~5s activation + worker pickup; documented, acceptable for backoff semantics.
- Adoption prerequisite: a Warp server (worker) must run somewhere; documented alongside Hangfire-coexistence guidance.
- dirty-worktree: `docs/specs/*` working artifacts only.

## Prior Work Check

- No webhook symbols in `src/core` (adapters spec's greps cover the namespace); no prior webhook branches in history.
- Library landscape verified 2026-07-10: no maintained embedded .NET sender (ASP.NET WebHooks archived); standalone services only (Svix / Hookdeck Outpost / Convoy) — the embedded-library gap is the feature's rationale.
- Real-world implementation mapped (multi-tenant payments platform, Hangfire-based): boundary and migration path validated; four design additions (pluggable signing, per-delivery headers, `Reference`, public query service) originated from that mapping.

## Open questions

None blocking. Deferred: per-endpoint keyed rate limiting for deliveries (extension of the adapters shared limiter), payload transformations, FIFO ordering, receiving-side webhooks, migration tooling for legacy delivery tables.
