# Spec: Warp server context — isolate server-internal DB work from the user's `DbContext`

## Problem

Warp shares the user's `DbContext` (`TContext`) for *everything* it does against the database: the publish-path outbox, the worker fetch/complete, the handler's work, and all the autonomous server loops (`Heartbeat`, `MessageRouter`, `Orchestrator`, `ScheduledJobActivation`, `RecurringJobScheduler`, `StaleJobRecovery`, `CounterAggregator`, `ExpirationCleanup`, `ServerCleanup`, the background-service host).

Those autonomous loops poll continuously (`Heartbeat` ~3s, worker fetch tight-polls without DB push, the rest on their intervals). Every one of those queries is logged by EF Core under `Microsoft.EntityFrameworkCore.Database.Command` — the **same category as the user's own application queries**, on the **same context**. So a user who wants command logging for their own code gets flooded with Warp's polling traffic and has no clean way to separate the two: category filtering can't (one category), per-context EF config can't (one context), and SQL/schema-text filtering is fragile.

The root cause is that server-internal work runs on a context Warp doesn't own. But it **doesn't need to** — see below.

## Key facts (established during design)

- **The worker already isolates its context from the handler's.** `WarpWorkerService.GetAndProcessJob` creates a worker scope for the claim/processing-log/completion (`workerContext`, `WarpWorkerService.cs:56`) and a *separate* handler scope for the handler + pipeline behaviors (`handlerScope`, `:147`). The handler's outbox commits on `handlerContext` (`:184`); the worker's completion commits on `workerContext` in its own transaction (`:228`). They are already distinct context instances and distinct transactions — **there is no fetch↔handler or completion↔outbox atomicity to preserve.**
- **The outbox is the only place sharing is mandatory.** `Publisher`/`BatchPublisher` stage the job row (and a `Created` `JobLog`) on the user's `TContext` so it commits in the same transaction as the user's business entities. This must stay on `TContext`.
- **Addon pipeline behaviors commit in the handler scope.** `RateLimitStore`, `CircuitBreakerStore`, `ConcurrencyStore`, `SagaStore` persist live state inside the handler's pipeline (§5.8) — i.e. on `TContext`. They cannot move to a server context.
- **Dashboard / publisher-only processes call `AddWarp` but not `AddWarpServer`.** A server context only exists where `AddWarpServer` ran, so anything resolvable in an `AddWarp`-only process (dashboard query services, `IPublisher`) must stay on `TContext`.

## Solution

Introduce a Warp-owned **server context** used for the autonomous server-internal DB work, configured with its own quiet `ILoggerFactory`. The user's `TContext` keeps the outbox, the handler path, addon state, and dashboard reads — everything user- or handler-initiated, where command logging is *wanted*.

The dividing line is exactly the noise line: **autonomous Warp loops → server context (quiet); user/handler/dashboard-initiated → `TContext` (normal logging).**

### Mapping: pull resolved names from `TContext`'s model

The server context must map every Warp table to the *identical* physical table/column/schema names as `TContext`, or its LINQ generates SQL against columns that don't exist. The user may apply a naming convention (`UseSnakeCaseNamingConvention()`) and a custom schema on their context.

We do **not** replay the convention. Instead we pull the already-resolved names from `TContext`'s built model — the same metadata `WarpJobTableNames` reads today for the raw provider SQL (`WarpJobTableNames.cs:188-189`: `entity.GetSchema()`, `entity.GetTableName()`). Because those names are post-convention, the server context needs no convention plugin.

Two candidate strategies (prototype both, pick in implementation):

1. **Copy resolved names (preferred).** Server context applies the Warp model, then pins `ToTable(resolvedTable, resolvedSchema)` + `HasColumnName(...)` from `TContext`'s model and marks every entity `ExcludeFromMigrations()`. Server context's surface is Warp entities only; "never migrate the server context" becomes structural, not a documented footgun.
2. **Share the model (`UseModel`).** Build the server context with `optionsBuilder.UseModel(tcontextModel)`. Identical mapping for free, no copying — but the server context's model then also carries the user's app entities (harmless, unused) and the warp tables are *not* `ExcludeFromMigrations` (so running migrations against the server context would duplicate; must be prevented by convention).

### Migration ownership: unchanged

`TContext` remains the schema owner (as today, via `ApplyWarpModel` / the customizer). The server context is a **runtime-only mirror** — `ExcludeFromMigrations` (strategy 1) or never-migrated (strategy 2). Pain point #2 and the existing migration story are **untouched**; this is purely additive on the runtime side.

### Logging suppression: the payoff

The server context's options use a dedicated `ILoggerFactory` that drops/demotes `Microsoft.EntityFrameworkCore.Database.Command` (and likely the rest of `Microsoft.EntityFrameworkCore.*`). Server polling noise disappears at the source with **zero consumer configuration**. (Optionally expose `opt.EnableServerCommandLogging()` for debugging.)

### Retargeting

Everything that runs an autonomous server loop moves from `TContext` to the server context:

- Worker: `WarpWorkerService` (claim/processing-log/completion), `WarpDispatcher` / `WarpDispatcherWorker`.
- Server tasks: `Heartbeat`, `ServerCleanup`, `ExpirationCleanup`, `StaleJobRecovery`, `CounterAggregator`, `RecurringJobScheduler`, `ScheduledJobActivation`, `Orchestrator`, `MessageRouter`, `NotificationListenerTask`.
- Background-service host + lease/state/log services.
- `IWarpSqlQueries<…>`, the row-lock interceptors, and the lock/notification-transport plumbing these use.

Stays on `TContext`:

- `Publisher` / `BatchPublisher` (outbox).
- Handler execution + addon pipeline stores (`RateLimitStore`, `CircuitBreakerStore`, `ConcurrencyStore`, `SagaStore`).
- Dashboard query/command services and `IBackgroundServiceQueryService` (resolvable in `AddWarp`-only processes).

Server reads/writes of `Job` rows (router, orchestrator, activation, fetch) go through the server context; the publisher writes them through `TContext`. Both map the same physical table; consistency is at the DB layer (row locks / transactions), exactly as the worker/handler split already works today.

## Open design decisions (resolve before coding)

1. **Bootstrap — sourcing `TContext`'s model when building the server context.** ✅ **VALIDATED by prototype** (`WarpServerContext`, `WarpServerModel`, `WarpServerContextPrototypeTests`). `WarpServerContext<TContext>` takes `(DbContextOptions, IServiceProvider)` — `AddDbContext` activates the extra `IServiceProvider` ctor param fine — and its `OnModelCreating` resolves `TContext` in a child scope to read `.Model` (once, since the model is cached). Confirmed: a `TContext` with `UseSnakeCaseNamingConvention()` produces `current_state`, and the server context mirrors that exact name with **no convention plugin of its own** (strategy 1: `ToTable` + `HasColumnName` from the resolved names). No circular dependency. One gotcha: `ExcludeFromMigrations` is a **design-time-model** property — assert/read it via `GetService<IDesignTimeModel>().Model`, not the runtime model. **Strategy 1 chosen.**
2. **Generic shape.** Does the server context need to be generic over `TContext` (`WarpServerContext<TContext>`) so it can reach `TContext`'s model, or is it non-generic with the name-map injected? This determines how much `<TContext>` churn the server-side services take.
3. **Connection + interceptors.** The provider extensions (`UsePostgreSql` / `UseSqlServer`) currently configure `IWarpSqlQueries<TContext>` and the lock provider against `TContext`. They must also configure the server context's connection (same connection string / `NpgsqlDataSource`) and row-lock interceptors.
4. **`ServerTaskSignals<TContext>`.** In-memory signal bus keyed by context type for DI isolation. Decide whether it stays on `TContext` (publishers fire it) or moves — publishers (on `TContext`) and the worker/router/orchestrator (on the server context) must share the *same* signal instance, so the key must be chosen carefully.
5. **Test harness.** `TestContext` and the `[GenerateDatabaseTests]` source-gen build contexts directly. Decide how fixtures construct the server context (and whether tests run it against the same physical tables).
6. **Versioning.** No public consumer API changes (users still call `AddWarp` / `AddWarpServer` unchanged; the server context is Warp-internal), so this *could* be a minor — but the internal blast radius argues for **3.0**.

## Outcome (implemented 3.0)

Shipped via `IWarpServerContext` (abstraction; components extract `DbContext Context` once) over an internal `WarpServerContext<TContext>`. Server tasks + background-service host moved to it; quiet logging via `ConfigureWarnings` demoting `CommandExecuted` to `Debug` (opt-out: `EnableServerCommandLogging`). Validated: NoDb 572 / PostgreSQL 760 / SQL Server 754.

**Deviation from plan — the worker stays on `TContext`.** Batch 4 (moving the worker's fetch/execute context to the server context) was reverted: a held server-context connection across job execution caused a ~5s graceful-cancellation latency regression (contention with `DeleteJob`'s `FOR UPDATE` row lock), reliably reproduced but not fully root-caused. Per §0.2 (worker hot path is sacred) and "don't ship refactors you can't explain", the worker/dispatcher were kept on `TContext`. Consequence: worker-fetch SQL still logs; `UseDatabasePush()` remains the lever for worker-poll noise. The constant server-wide polling (heartbeat, server tasks, background services) — the dominant idle noise — is silenced.

## Non-goals

- Changing the publish-path outbox or the handler context.
- Moving addon state off `TContext`.
- Changing migration ownership or the `ApplyWarpModel` story (#2).
- Splitting the Warp model across contexts (all Warp tables live on both — simpler, and keeps the above non-goals intact).
