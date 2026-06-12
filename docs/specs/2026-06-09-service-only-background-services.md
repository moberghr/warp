# Spec — Service-only deployment: `AddWarpBackgroundServices<TContext>()`

**Date:** 2026-06-09
**Branch:** `feat/service-only`
**Scope classification:** Feature (additive, non-breaking). New public registration entry point + internal refactor of the worker registration path. `security_impact = none`.

## Problem

Today there is no supported way to run Warp **only** for background services (`WarpBackgroundService`) without also booting the entire job-processing worker.

- `AddWarp<TContext>` registers only the publish/read surface (incl. the read-only `IBackgroundServiceQueryService`). It registers **nothing that executes** a `WarpBackgroundService` — `BackgroundServiceHost<TContext>` is not in this path.
- `AddWarpWorker<TContext>` is the only path that registers `BackgroundServiceHost<TContext>`, but it *unconditionally* also registers the full job stack: `WarpDispatcherHost`, `WarpSingleWorkerHost`, and the six job-only server tasks (`Orchestrator`, `MessageRouter`, `ScheduledJobActivation`, `RecurringJobScheduler`, `StaleJobRecovery`, `CounterAggregator`).

The only workaround is `WorkerCount = 0` plus manually nulling five interval properties — and `ScheduledActivationInterval` is a non-nullable `TimeSpan` with no off switch, so even that is incomplete. The result: a "service-only" process still takes job-routing distributed locks and polls the DB for work that never arrives.

## Goal

Add a third, first-class registration tier:

```csharp
services.AddDbContext<AppDb>(...);
services.AddWarpBackgroundServices<AppDb>(opt =>
{
    opt.UsePostgreSql();                 // or UseSqlServer() — provider still required
    opt.AddBackgroundService<EmailPump>();
});
```

This boots the shared **server-host infrastructure** needed to run background services correctly, and registers **none** of the job worker hosts or job-only server tasks.

## What the service-only host MUST register (shared infra)

Verified by reading the actual dependency graph:

- Everything from `AddWarp<TContext>()` (called internally) — entities, publish/query services, `ServerTaskSignals<TContext>`, `IWarpNotificationTransport`, `IBackgroundServiceQueryService`.
- Singletons: `PauseStateHolder` (ctor dep of `Heartbeat` + initialized by `WarpServerRegistration`), `ServerRegistrationState` (populated by `WarpServerRegistration`), `ProcessCpuTracker` + `HeartbeatLeaseTracker` (ctor deps of `Heartbeat`).
- Scoped bg-service coordinators: `IBackgroundServiceStateService`, `IBackgroundServiceLeaseCoordinator`, `IBackgroundServiceLogStore`.
- `IBackgroundServiceStatusObserver` → `NullBackgroundServiceStatusObserver` (TryAdd default).
- Server tasks (`IServerTask`): **`Heartbeat`** (renews singleton lease + bumps instance heartbeat), **`ServerCleanup`** (releases dead-server leases/instances), **`ExpirationCleanup`** (owns bg-service log retention + orphan-`Definition` GC — its job-cleanup half no-ops with zero jobs).
- Hosted services, registered in this order so `WarpServerRegistration` runs first: `WarpServerRegistration<TContext>`, `ServerTaskHost<TContext>`, `BackgroundServiceHost<TContext>` (guarded against duplicate registration).

## What the service-only host MUST NOT register

- Worker hosts: `WarpDispatcherHost<TContext>`, `WarpSingleWorkerHost<TContext>`.
- Job-only server tasks: `Orchestrator`, `MessageRouter`, `ScheduledJobActivation`, `RecurringJobScheduler`, `StaleJobRecovery`, `CounterAggregator`.
- `DispatcherRegistry` (dispatcher-only).
- The job-log `AddLogging`/`JobLoggerProvider` block (job handler log capture — irrelevant here; `BackgroundServiceHost` wires its own per-service log provider).

## Design

**Refactor `Warp.Worker/ServiceConfiguration.cs`:** extract a private `AddServerHostCore<TContext>(IServiceCollection)` that registers the shared infra above. Then:

- `AddWarpWorkerInner` = `AddServerHostCore` + worker-only pieces (`DispatcherRegistry`, the `AddLogging` block, the six job server tasks, the two worker hosts). **Net registration set for `AddWarpWorker` is unchanged** — only the *order* of the two worker hosts moves (they now register after `ServerTaskHost`/`BackgroundServiceHost` instead of between `WarpServerRegistration` and `ServerTaskHost`). `WarpServerRegistration` remains first, so `ServerRegistrationState` is populated before any host's `StartAsync`; the `StopAsync` reverse-order invariant (lease/instance delete before `Server` row delete) is preserved.
- `AddWarpBackgroundServices<TContext>` = new public entry point. Builds a `WarpBackgroundServicesBuilder<TContext>`, invokes the user lambda, **forces `WorkerCount = 0`** (so `WarpServerRegistration` creates no `Worker`/`WorkerGroup` rows) and `UseDispatcher = false`, registers `IOptions<WarpWorkerConfiguration>` + `IOptions<WarpConfiguration>` (TryAdd, same pattern as `AddWarpWorker`), then calls `AddServerHostCore<TContext>`.

**New file `Warp.Worker/WarpBackgroundServicesBuilder.cs`:** `sealed class WarpBackgroundServicesBuilder<TContext> : WarpWorkerConfiguration, IWarpBuilder<TContext>` — structurally identical to `WarpWorkerBuilder`. Inheriting `WarpWorkerConfiguration` is required because `BackgroundServiceHost`/`Heartbeat` read `IOptions<WarpWorkerConfiguration>` (ServerId, HealthCheckInterval, lease TTL, log retention, orphan grace). Worker-only fields (WorkerCount/Queues/PollingInterval/dispatcher) are present on the type but documented as ignored in this mode; `WorkerCount` is overwritten to 0 by the entry point regardless of user input.

**Provider still required.** `IWarpLockProvider` (ServerCleanup/ExpirationCleanup locks) and `IWarpSqlQueries<TContext>` (Heartbeat) come from `opt.UsePostgreSql()`/`UseSqlServer()`. Omitting the provider fails fast on first lock/query resolve — existing behavior, no new guard needed.

## Public contracts added

- `public static IServiceCollection AddWarpBackgroundServices<TContext>(this IServiceCollection, Action<WarpBackgroundServicesBuilder<TContext>>? configure = null) where TContext : DbContext` (in `Warp.Worker.ServiceConfiguration`).
- `public sealed class WarpBackgroundServicesBuilder<TContext> : WarpWorkerConfiguration, IWarpBuilder<TContext>` (in `Warp.Worker`).

No existing signatures change. Additive only.

## Change manifest

1. `src/core/Warp.Worker/WarpBackgroundServicesBuilder.cs` — NEW builder.
2. `src/core/Warp.Worker/ServiceConfiguration.cs` — extract `AddServerHostCore`; add `AddWarpBackgroundServices`.
3. `src/tests/Warp.Tests/Admin/DeploymentShapeTests.cs` — add `ServiceOnlyShape_*` NoDb tests (positive + negative registration assertions).
4. `src/tests/Warp.Tests/BackgroundServices/ServiceOnlyHostTestsBase.cs` — NEW `[GenerateDatabaseTests]` integration base (PG + SQL Server) booting a service-only `IHost`.
5. `src/core/Warp.Worker/WarpServerRegistration.cs` — **(amendment, added during impl)** skip worker groups with `WorkerCount == 0` so a service-only host (and any explicit 0-worker group) leaves no `WorkerGroup`/`Worker` rows. Discovered because `GetEffectiveWorkerGroups()` always emits the implicit default group even at `WorkerCount=0`; the worker hosts already iterate the resulting registrations, so this is the single source of truth and a strict improvement for the degenerate case.
6. `website/docs/features/background-services.md` — document the new entry point + service-only deployment shape.
6. `CLAUDE.md` (§ Domain refresher / Skill table is fine as-is) + `.claude/rules/architecture.md` §2.13 and `.claude/rules/project-specific.md` §8.18 — add one sentence: service-only hosts use `AddWarpBackgroundServices` (no job worker).

## Test manifest

- **NoDb (`DeploymentShapeTests`):**
  - `ServiceOnlyShape_AddWarpBackgroundServices_RegistersHostAndBgServices` — `BackgroundServiceHost`, `ServerTaskHost`, `WarpServerRegistration` registered as `IHostedService`; `IBackgroundServiceStateService`/`LeaseCoordinator`/`LogStore` resolve; core API resolves.
  - `ServiceOnlyShape_OmitsJobWorkerHostsAndTasks` — `WarpDispatcherHost`/`WarpSingleWorkerHost` NOT registered as hosted services; resolved `IEnumerable<IServerTask>` contains `Heartbeat`/`ServerCleanup`/`ExpirationCleanup` and NOT the six job tasks.
  - `ServiceOnlyShape_AddBackgroundServiceAndProvider_Compose` — `opt.AddBackgroundService<T>()` and `opt.UsePostgreSql()` are callable on the builder and register the service alias.
- **Integration (`ServiceOnlyHostTestsBase`, PG + SQL Server):**
  - A `PerServer` service reaches `ExecuteAsync` (counter/barrier) under a service-only host — proves the host runs services with no worker present.
  - No `Worker`/`WorkerGroup` rows are created (`WorkerCount = 0`); a `Server` row IS created (FK target).
  - Graceful shutdown deletes the `BackgroundServiceInstance` row.
  - (If cheap) a `Singleton` service acquires a `BackgroundServiceLease`.
- Use `BarrierSignal`/`BackgroundServiceBarrierSignal` to pin handlers — no spray-N, no `Task.Delay` (§4.5/§4.7). Bare `[TimedFact]` where possible; integration lifecycle tests follow the existing `[TimedFact(15_000)]` precedent in `PerServerLifecycleTestsBase`.

## Assumptions & risks

- **Risk: hosted-service start order.** Moving the two worker hosts to register after `ServerTaskHost`/`BackgroundServiceHost` is benign (each `StartAsync` is independent; `WarpServerRegistration` stays first). The full worker integration suite is the regression net — run it.
- **Assumption:** `ExpirationCleanup` running in a job-less host is harmless (queries return zero rows). Confirmed by reading the task.
- **Assumption:** reusing `WarpWorkerConfiguration` as the service-only options backing is acceptable despite exposing unused worker fields; the alternative (a separate config type) can't satisfy `IOptions<WarpWorkerConfiguration>` that the host/heartbeat require.
- **Out of scope:** HTTP dashboard wiring for service-only processes (a dashboard process uses `AddWarp` + `Warp.Http` and already serves `/api/services`); any change to the worker hot path (§0.2/§6.1 — untouched); a config-flag alternative on `AddWarpWorker` (rejected in favor of the dedicated entry point).

## Open decisions

None. API shape resolved with the engineer: dedicated `AddWarpBackgroundServices` entry point.
