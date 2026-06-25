# Plan — Warp server context (isolate server-internal DB work)

Spec: `docs/specs/2026-06-25-warp-server-context.md`
Prototype (decision #1 validated): `WarpServerContext.cs`, `WarpServerModel.cs`, `WarpServerContextPrototypeTests.cs`.

## Shape of the change

A Warp-owned `WarpServerContext<TContext>` (full Warp model, names mirrored from `TContext`, `ExcludeFromMigrations`, **quiet `ILoggerFactory`**) becomes the execution context for all autonomous server-internal DB work. The retarget is mostly mechanical: server-side services swap the injected/resolved context from `TContext` to `WarpServerContext<TContext>`; everything stays generic over `TContext`.

**Stays on `TContext` (logging wanted, or runs in `AddWarp`-only processes):** `Publisher`/`BatchPublisher` (outbox), the handler scope + addon pipeline stores (`RateLimitStore`/`CircuitBreakerStore`/`ConcurrencyStore`/`SagaStore`), dashboard query/command services, `IBackgroundServiceQueryService`.

**Moves to `WarpServerContext<TContext>`:** worker fetch/processing-log/completion (`WarpWorkerService`, `WarpDispatcher`, `WarpDispatcherWorker`), the server tasks (`Heartbeat`, `ServerCleanup`, `ExpirationCleanup`, `StaleJobRecovery`, `CounterAggregator`, `RecurringJobScheduler`, `ScheduledJobActivation`, `Orchestrator`, `MessageRouter`, `NotificationListenerTask`), the background-service host + its state/lease/log write services.

**Stays keyed on `TContext` (shared instance):** `ServerTaskSignals<TContext>` — the publisher (`TContext`) and the server services (`WarpServerContext<TContext>`) resolve the same singleton because both are generic over `TContext`. `IWarpSqlQueries<TContext>` stays keyed on `TContext` for DI; only its method *parameter* type changes (below).

## Batch 1 — Productionize the context + connection bootstrap

Promote the prototype `WarpServerContext<TContext>` / `WarpServerModel` from prototype to real (keep them `internal`). Add the **connection bootstrap**: the server context must use the same database as `TContext`. Pull the relational connection from `TContext`'s registered `DbContextOptions<TContext>` (the `RelationalOptionsExtension` — `Connection` / `ConnectionString` / `DataSource`) and apply it to the server context's options. This mirrors how `ServiceConfiguration.ConfigureDbContextOptions` already wraps `TContext`'s options.

Register `WarpServerContext<TContext>` in `AddServerHostCore` (so it exists for both worker and service-only servers, and never for `AddWarp`-only dashboard/publisher processes). Configure its options: provider connection (from `TContext`), row-lock interceptors (`AddWarpInterceptors`), and a placeholder logger factory (Batch 6 makes it quiet).

**Risk to de-risk first in this batch:** the connection pull. Add a focused test like the model prototype — assert the server context opens against the same DB as `TContext` (resolve both, compare `Database.GetConnectionString()` / round-trip a row). Mirrors the model-bootstrap de-risk.

Checkpoint: NoDb + the new connection test.

## Batch 2 — `IWarpSqlQueries` parameter retarget + provider wiring

Change every `IWarpSqlQueries<TContext>` method's context parameter from `TContext` to `DbContext` (both `PostgresWarpSqlQueries` and `SqlServerWarpSqlQueries`). The raw SQL needs only the connection + the resolved table names; names come from the injected `WarpJobTableNames` (built from `TContext`'s model, which the server context mirrors), so passing `WarpServerContext<TContext>` is safe. No name divergence (proven in the prototype).

Confirm the provider extensions (`UsePostgreSql`/`UseSqlServer`) still register `IWarpSqlQueries<TContext>`, `IWarpLockProvider`, and the notification transport once. The lock provider operates on the server context's connection for server-task locks — verify it takes a connection/`DbContext`, not `TContext` specifically.

Checkpoint: NoDb (provider unit tests), then one PG + one SQL Server class.

## Batch 3 — Retarget the server tasks

In each `IServerTask` implementation (`Heartbeat`, `ServerCleanup`, `ExpirationCleanup`, `StaleJobRecovery`, `CounterAggregator`, `RecurringJobScheduler`, `ScheduledJobActivation`, `Orchestrator`, `MessageRouter`, `NotificationListenerTask`): change the injected `TContext` field/parameter to `WarpServerContext<TContext>`. Bodies (`_context.Set<Job>()`, etc.) are unchanged since it's still a `DbContext`. `IWarpSqlQueries<TContext>` and `ServerTaskSignals<TContext>` injections are unchanged (still keyed on `TContext`); calls pass the server context now that the parameter is `DbContext`.

`ServerTaskHost<TContext>` / `ServerTaskLoop<TContext>` resolve `IServerTask` from a scope — unchanged; the tasks pull the server context from the scope.

Checkpoint: PG + SQL Server `Integration` fixture classes for each task family (orchestration, routing, scheduling, recovery, cleanup, heartbeat).

## Batch 4 — Retarget the worker hosts

`WarpWorkerService.GetAndProcessJob`: `workerContext` resolves `WarpServerContext<TContext>` instead of `TContext` (claim, processing-log, completion). **The handler scope stays `TContext`** (`handlerScope` → `GetRequiredService<TContext>()` for the handler + outbox at `:182`). Same for `WarpDispatcher`/`WarpDispatcherWorker`. **Worker hot path (§0.2/§6.1): no new logic — only the resolved context type changes.**

Checkpoint: PG + SQL Server end-to-end `Integration` tests (publish → execute → complete, plus retry/cancel) — the critical correctness gate that the publish-on-`TContext` / execute-on-server-context split round-trips.

## Batch 5 — Retarget the background-service host

`BackgroundServiceHost<TContext>` and the write-side services (`BackgroundServiceStateService`, `BackgroundServiceLeaseCoordinator`, `BackgroundServiceLogStore`) move to `WarpServerContext<TContext>`. `IBackgroundServiceQueryService` (dashboard read, registered in `AddWarp`) **stays on `TContext`** — it must resolve in `AddWarp`-only processes.

Checkpoint: PG + SQL Server background-service fixtures (PerServer + Singleton lease failover).

## Batch 6 — Quiet logging (the payoff)

Give the server context's options a dedicated `ILoggerFactory` that drops/demotes `Microsoft.EntityFrameworkCore.Database.Command` (likely all `Microsoft.EntityFrameworkCore.*`). Expose an opt-in escape hatch — `opt.EnableServerCommandLogging()` — for debugging. Default: off (quiet).

Checkpoint: a test that captures the app `ILoggerFactory` output, runs a server-context query, and asserts **no** EF command log reaches the app logger — while a `TContext` query still does.

## Batch 7 — Tests + fixtures

- Promote `WarpServerContextPrototypeTests` to a real model-mapping test; add a **DB round-trip** test (write a `Job` via `TContext`, read it via `WarpServerContext<TContext>` on PG + SQL Server — proves identical physical mapping end-to-end).
- Update fixtures / `WarpTestServer` to register the server context (it now boots in every integration server).
- Full suite is the gate — the retarget touches the whole server side.

Checkpoint: NoDb, then full PG, then full SQL Server.

## Batch 8 — Docs + rules

- New "Logging" doc section: server DB work is on a separate quiet context; how to re-enable via `EnableServerCommandLogging()`; that app-context command logging is unaffected.
- `architecture.md`: document the two-context model (publish/handler/dashboard on `TContext`; autonomous server loops on `WarpServerContext<TContext>`), names mirrored from `TContext`, `ExcludeFromMigrations`, migration ownership unchanged.
- `releases.md`: 3.0 entry.

## Final

- Behavioral diff vs spec (spec-drift check): publish path, handler outbox, addon stores, migration ownership all unchanged; only server-internal execution context + its logging changed.
- Compliance + architecture + test reviews.
- Confirm worker hot path untouched (§0.2/§6.1) — only the resolved context type changed, no new orchestration.
- Full both-DB suites green.

## Open items carried from the spec

- **Connection bootstrap** (Batch 1) — the remaining unproven piece, analogous to the model bootstrap; de-risk it first in Batch 1.
- **Lock provider parameterization** (Batch 2) — confirm it operates on a connection/`DbContext`, not `TContext` specifically.
- **Notification transport** (Batch 3) — `NotificationListenerTask` uses a provider-native connection for LISTEN/NOTIFY; confirm the EF-touching parts move to the server context and the raw transport connection is independent.
- **Versioning** — 3.0 (no public consumer API change; internal blast radius).
