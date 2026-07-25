# Plan — Operational-event notifier seam (`IWarpNotifier`)

**Date:** 2026-07-25 · **Slug:** `operational-notifier-seam` · **Spec:** `docs/specs/2026-07-25-operational-notifier-seam.md`

Rigor: **HIGH** (3 batches → hard-trigger floor; external public contract added). Plan-only — no code until the Phase 2.5 gate is approved.

## Batch 1 — Core seam (contract + event model + dispatcher + registration)

**Files (new unless noted):**
- `src/core/Warp.Core/Data/Enums/WarpEventType.cs` — `WarpEventType` (5 values, 1 & 5 reserved) + `WarpEventSeverity` (ns `Warp.Core.Enums`).
- `src/core/Warp.Core/Notifiers/IWarpNotifier.cs` — the seam interface.
- `src/core/Warp.Core/Notifiers/WarpOperationalEvent.cs` — abstract base + `WebhookDeliveryExhaustedEvent` / `SagaForceCompletedEvent` / `InstanceDownEvent` subtypes.
- `src/core/Warp.Core/Notifiers/WarpNotifierDispatcher.cs` — singleton; injects `IEnumerable<IWarpNotifier>`; one guarded fan-out loop (await → catch → `LogWarning` → continue; empty-set fast path).
- `src/core/Warp.Core/Notifiers/NotifierServiceConfiguration.cs` — `AddNotifier<T>()` on non-generic `IWarpBuilder` (§2.13); `TryAddEnumerable(Singleton<IWarpNotifier,T>)`.
- `src/core/Warp.Core/ServiceConfiguration.cs` (edit) — `TryAddSingleton<WarpNotifierDispatcher>()` in `AddWarp`, beside the `ServerTaskSignals<TContext>` registration.
- `src/tests/Warp.Tests/Notifiers/WarpNotifierDispatcherTests.cs` (NoDb) — fires-all-once; throwing-notifier swallowed+logged, others still fire; zero-notifier inert; `AddNotifier<T>` enumerable registration; redaction-safe field set.

**Acceptance:** dispatcher never throws out; empty set = no-op; both registered notifiers fire; build analyzer-clean.
**Boundary:** no dispatch-site wiring yet; no DB.

## Batch 2 — Wire the three v1 dispatch sites (+ DB tests)

**Files (edit):**
- `src/core/Warp.Core/Webhooks/ExecuteWebhookDelivery.cs` — inject `WarpNotifierDispatcher`; after the existing post-commit `InvokeExhaustedHandlersAsync`, build `WebhookDeliveryExhaustedEvent` from the same snapshot and dispatch. Coexists with `IWebhookDeliveryExhaustedHandler`.
- `src/core/Warp.Core/Services/SagaCommandService.cs` — inject dispatcher; after `SaveChanges` (~`:119`, beside the audit log `:123`) dispatch `SagaForceCompletedEvent`.
- `src/core/Warp.Worker/Services/ExpirationCleanup.cs` — inject dispatcher; after the StaleSwept `SaveChangesAsync` (~`:606`) dispatch one `InstanceDownEvent` per swept `ApplicationInstance`. **Confirm** whether `ServerCleanup` has a stale-server delete site; if yes, source `InstanceDown` there too (`IsServer=true`); if no, note the gap and keep v1 non-server-only.
- `src/tests/Warp.Tests/Notifiers/NotifierDispatchTestsBase.cs` (`[GenerateDatabaseTests]`, both providers) — webhook-exhaustion → event; saga force-complete → event (saga row already gone); stale `ApplicationInstance` sweep → event.

**Acceptance:** each site dispatches its event post-commit; a registered spy notifier receives it on both providers; existing webhook/saga/cleanup tests stay green.
**Boundary:** no new detection logic; `JobDeadLettered`/`BacklogBreached` untouched; worker hot path untouched.

## Batch 3 — Docs & rules

**Files:**
- `website/docs/features/operational-notifications.md` (new) — the seam, the contract/guarantees (post-commit, at-least-once, never-throws), the v1 event taxonomy + reserved values, `AddNotifier<T>` usage, the captive-dependency footgun, and the webhooks-vs-alerting distinction.
- `.claude/rules/project-specific.md` — new **§8.25** summarising the seam for future work.
- `CLAUDE.md` — one-line mention in the addon/feature list.

**Acceptance:** page renders; links resolve; rule cross-references §8.20/§2.9/§8.18.
**Boundary:** docs only.

## Sequencing & verification

Batch 1 → 2 → 3. After batch 2, run the full suite on both providers (`dotnet test src/tests/Warp.Tests/Warp.Tests.csproj`). Analyzer-clean throughout (`TreatWarningsAsErrors`). Behavioral diff before review. Two-stage review (Stage 1 compliance; Stage 2 `test-reviewer` + `architecture-reviewer` at HIGH — new cross-cutting seam + dispatch-site edits qualify for the boundary reviewer).

## Deferred follow-ups (explicitly not this PR)

- `JobDeadLettered` slice: `BackgroundService<TContext>` consuming `ServerTaskSignal.JobFinalized` + claim mechanism (marker column vs cursor) + first-enable watermark.
- `BacklogBreached`: after queue-wait/backlog metrics land.
- `HeartbeatLost` lapse-detection (earlier-than-reap instance warning).
