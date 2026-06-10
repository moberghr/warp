# Spec — Unify worker/background-services registration under `AddWarpServer<TContext>()`

**Date:** 2026-06-09
**Branch:** `feat/service-only`
**Scope classification:** Feature + public-API redesign. Obsoletes a 1.0 public API (`AddWarpWorker`) via `[Obsolete]` (compile warning, not removal) and renames the central config/builder types. Non-runtime-breaking for existing callers (obsolete shims preserve behavior). `security_impact = none`.

**Supersedes** `docs/specs/2026-06-09-service-only-background-services.md`. That spec's standalone `AddWarpBackgroundServices` entry point + `WarpBackgroundServicesBuilder` are **removed** and folded into `AddWarpServer` with the worker disabled. The `WarpServerRegistration` zero-worker-group skip and the service-only tests from that work are **kept** (rewired to the new API).

## Problem / motivation

The session added a third sibling entry point (`AddWarpBackgroundServices`) beside `AddWarp` and `AddWarpWorker`. Three siblings invite the "call both" footgun: `AddWarpWorker` is a superset of `AddWarpBackgroundServices` (both call the shared `AddServerHostCore`), and calling both yields order-dependent behavior — `AddWarpBackgroundServices`-first forces `WorkerCount = 0` onto the shared `IOptions<WarpWorkerConfiguration>` (first `TryAdd` wins) and silently produces a worker that processes nothing.

The correct mental model: a process registers a **server** (the `Server` row, `Heartbeat`/`ServerCleanup`/`ExpirationCleanup`, `ServerTaskHost`, `BackgroundServiceHost`); the **worker** (job fetch/execute + the six job-only server tasks) is one *optional component* of that server. One entry point — `AddWarpServer` — removes the "both" case entirely.

## Goal

```csharp
// Full server (worker runs — the default)
services.AddWarpServer<AppDb>(opt =>
{
    opt.UsePostgreSql();
    opt.WorkerCount = 20;
    opt.AddBackgroundService<EmailPump>();
});

// Service-only server (no job worker)
services.AddWarpServer<AppDb>(opt =>
{
    opt.UsePostgreSql();
    opt.DisableWorker();
    opt.AddBackgroundService<EmailPump>();
});
```

## Decisions (resolved with engineer)

1. **Worker on by default; `opt.DisableWorker()` opts out.** Worker config stays flat on the builder. `AddWarpServer()` with no opt-out behaves exactly like the old `AddWarpWorker()`.
2. **Full rename with `[Obsolete]` aliases:** `WarpWorkerConfiguration` → `WarpServerConfiguration`, `WarpWorkerBuilder<T>` → `WarpServerBuilder<T>`. Old names kept as `[Obsolete]` subclasses for external source-compat.

## Design

### New runtime knob — `RunWorker`
- `WarpServerConfiguration.RunWorker` (bool, default `true`) + `void DisableWorker()` which sets `RunWorker = false` **and** `WorkerCount = 0`.
- `AddWarpServer` registration: always `AddServerHostCore<TContext>` (server infra). **Only when `RunWorker`** does it add the worker-only pieces — `DispatcherRegistry`, the `JobLoggerProvider` logging block, the six job server tasks (Orchestrator/MessageRouter/ScheduledJobActivation/RecurringJobScheduler/StaleJobRecovery/CounterAggregator), and the two worker hosts (`WarpDispatcherHost`/`WarpSingleWorkerHost`). This replaces the old `AddWarpWorkerInner` body.
- `WarpServerRegistration.StartAsync`: when `!RunWorker`, create **no** worker groups/rows at all (skip the group loop entirely); the existing per-group `WorkerCount == 0` skip remains for the explicit-group edge case. Guarantees a service-only server leaves no `Worker`/`WorkerGroup` rows even if a stray `WorkerCount`/`AddWorkerGroup` was set before `DisableWorker()`.

### Entry points
- `AddWarpServer<TContext>(Action<WarpServerBuilder<TContext>>? configure = null)` — builds a `WarpServerBuilder`, invokes the lambda, `TryAdd`s `IOptions<WarpServerConfiguration>` + `IOptions<WarpConfiguration>`, then a private `AddWarpServerCore<TContext>(services)` does the conditional registration above.
- `[Obsolete("Use AddWarpServer; the worker is a component of the server.")] AddWarpWorker<TContext>(Action<WarpWorkerBuilder<TContext>>? configure = null)` — constructs a `WarpWorkerBuilder` (the obsolete subclass), invokes `configure`, then runs the same core path. Preserves exact behavior (worker on).
- `AddWarp<TContext>` — unchanged (publish/dashboard-only tier).

### Rename mechanics (TreatWarningsAsErrors-safe)
- Rename `WarpWorkerConfiguration` → `WarpServerConfiguration` and `WarpWorkerBuilder` → `WarpServerBuilder` across **all** of `src/` (core, tests, benchmarks, demo). Because `[Obsolete]` raises warnings-as-errors, no internal/test code may keep the old names — the sweep is total.
- Add, in a single place, the obsolete aliases:
  - `[Obsolete("Renamed to WarpServerConfiguration")] public class WarpWorkerConfiguration : WarpServerConfiguration { }`
  - `[Obsolete("Renamed to WarpServerBuilder")] public sealed class WarpWorkerBuilder<TContext> : WarpServerBuilder<TContext> { public WarpWorkerBuilder(IServiceCollection s) : base(s) {} }` (make `WarpServerBuilder` non-sealed, or use composition — see risks).
- Replace `AddWarpWorker(...)` **call sites** in tests/benchmarks/demo with `AddWarpServer(...)`. Keep exactly one obsolete-shim test that calls `AddWarpWorker` under `#pragma warning disable CS0618` to prove the shim still wires a worker.

### Known migration caveat (documented, not fixed)
`IOptions<WarpServerConfiguration>` is the new DI key. A user who injected `IOptions<WarpWorkerConfiguration>` directly (deep-internals usage, not the builder lambda) must switch to `IOptions<WarpServerConfiguration>` — the obsolete subclass is a *different* generic key and won't resolve. Documented in release notes; the public surface that touched the type by name was minimal (the builder lambda uses field access, unaffected).

## Public contracts

Added: `AddWarpServer<TContext>`, `WarpServerConfiguration`, `WarpServerBuilder<TContext>`, `WarpServerConfiguration.RunWorker`, `WarpServerConfiguration.DisableWorker()`.
Obsoleted (kept): `AddWarpWorker<TContext>`, `WarpWorkerConfiguration`, `WarpWorkerBuilder<TContext>`.
Removed (never shipped): `AddWarpBackgroundServices`, `WarpBackgroundServicesBuilder`.

## Change manifest

1. `src/core/Warp.Worker/Configuration.cs` — rename `WarpWorkerConfiguration` → `WarpServerConfiguration`; add `RunWorker` + `DisableWorker()`; add `[Obsolete] WarpWorkerConfiguration` alias.
2. `src/core/Warp.Worker/WarpServerBuilder.cs` — renamed from `WarpWorkerBuilder.cs`; `[Obsolete] WarpWorkerBuilder<T>` alias (same file or sibling).
3. `src/core/Warp.Worker/ServiceConfiguration.cs` — add `AddWarpServer` + private `AddWarpServerCore` (conditional on `RunWorker`); obsolete `AddWarpWorker` shim; drop `AddWarpBackgroundServices`.
4. `src/core/Warp.Worker/WarpBackgroundServicesBuilder.cs` — **delete**.
5. `src/core/Warp.Worker/WarpServerRegistration.cs` — skip all worker groups when `!RunWorker` (keep the per-group `WorkerCount==0` skip).
6. Global rename sweep across remaining `src/core` files referencing the old type names (server tasks, hosts, `WarpTestServer` is in tests).
7. `src/tests/**` — rename sweep; rewire service-only tests (`DeploymentShapeTests`, `ServiceOnlyHostTestsBase`) to `AddWarpServer` + `DisableWorker()`; keep `WarpServerRegistrationTests` zero/mixed-group tests; add an obsolete-shim test.
8. `src/benchmarks/**`, `src/demo/**` — rename sweep + `AddWarpWorker`→`AddWarpServer` call sites.
9. `website/docs/features/background-services.md` — replace the `AddWarpBackgroundServices` tier doc with `AddWarpServer` + `DisableWorker()`.
10. `.claude/rules/architecture.md` §2.5/§2.13, `.claude/rules/project-specific.md` §8.18 — describe `AddWarpServer` (worker = optional server component); note `AddWarpWorker` obsolete.

## Test manifest

- **NoDb (`DeploymentShapeTests`):**
  - `ServerWithWorkerShape_AddWarpServer_RegistersWorkerAndBgServices` — worker hosts + 6 job tasks + bg-service host all registered.
  - `ServiceOnlyShape_AddWarpServerDisableWorker_OmitsWorker` — `DisableWorker()` ⇒ no worker hosts, none of the 6 job tasks; bg-service host + Heartbeat/ServerCleanup/ExpirationCleanup present.
  - `ObsoleteAddWarpWorker_StillRegistersFullWorker` — under `#pragma warning disable CS0618`, the obsolete shim yields the same shape as `AddWarpServer` default.
- **Integration (`ServiceOnlyHostTestsBase`, PG + SQL Server):** rewired to `AddWarpServer(opt => opt.DisableWorker())`. Keep: PerServer reaches user code; Server row + zero Worker/WorkerGroup rows; graceful-delete; Singleton lease acquired; ExpirationCleanup orphan GC.
- **Unit (`WarpServerRegistrationTests`):** keep zero-worker + mixed-group tests; add `!RunWorker` ⇒ no groups even with `WorkerCount>0` set.
- Bare `[TimedFact]` for NoDb; existing `[TimedFact(15_000)]` precedent for bg-service integration lifecycle tests.

## Assumptions & risks

- **Risk (sealed builder):** `WarpServerBuilder` must be non-sealed (or the obsolete `WarpWorkerBuilder` uses composition) so the `[Obsolete]` subclass alias compiles. Today `WarpWorkerBuilder` is `sealed`. Drop `sealed` on `WarpServerBuilder`.
- **Risk (rename sweep misses):** mitigated by TreatWarningsAsErrors + the obsolete aliases — any missed internal reference fails the build. The full suite is the behavioral net.
- **Risk (obsolete-shim behavior):** the shim must produce an identical registration set to the old `AddWarpWorker`. Verified by the shape test + full worker suite.
- **Out of scope:** the explicit `AddWorker(...)` sub-builder model (rejected — worker config stays flat); removing `AddWarpWorker` outright (kept obsolete for one release); renaming `WorkerGroupConfiguration`/`WorkerCount`/queue fields (the worker's own knobs keep their names — they configure the worker component).

## Open decisions

None. Worker-toggle model and rename scope resolved with the engineer.
