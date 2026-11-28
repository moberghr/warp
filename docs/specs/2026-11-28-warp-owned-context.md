# A Warp-owned context in every `AddWarp` process (#344)

Status: approved 2026-11-28 (D1–D4 as recommended; D4: every listed service moves, the limit managers included, behaviour changes in the 8.2.0 release notes). Implemented. Target: 8.2 (minor).

## Problem

The dashboard query services, `IBackgroundServiceQueryService` and the admin managers read and write Warp's tables
through the application's `TContext`, because the only Warp-owned context (`WarpServerContext<TContext>`, §2.14) is
registered by `AddServerHostCore`, so it does not exist in `AddWarp`-only processes. Warp's work then pays for, and
depends on, the application's context: its interceptors, logging, command timeout, retry strategy and pool.

## Decision summary

1. **A sibling context, `WarpContext<TContext>` (internal), registered by `AddWarp`.** It is not the server context
   itself, because the server context makes `Job.RunId` a concurrency token (#347). On a context that serves
   `JobCommandService`'s tracked saves, that token reintroduces the bulk-requeue deadlock on SQL Server that #347
   moved off the application's context. The model build (`ApplyWarpModel` + `EntityConfigurators` + `PinWarpStorageTypes`
   + `WarpServerModel.MirrorNames` + `WarpPartialIndexes`) moves to one shared helper that both contexts call. Only the
   server context adds the RunId token.
2. **The connection comes from the same `IWarpServerContextConfigurator`.** `UsePostgreSql()` / `UseSqlServer()`
   register it inside the `AddWarp` lambda, so an `AddWarp`-only process already has it. The configurator reads the
   connection from `WarpDatabaseConnection<TContext>`, which `AddWarp` registers, not from anything a server adds.
   `IWarpServerModelNames` moves from `AddServerHostCore` to `AddWarp` (it is a `TryAdd`, so the server's call is a no-op).
3. **Services take an internal `IWarpContext` (`DbContext Context`), resolved per scope:**
   - the `WarpContext<TContext>` when a configurator is registered **and** it supports `TContext`'s provider;
   - otherwise the scoped `TContext` itself. This is today's behaviour, kept for the test shapes of #359 (EF in-memory,
     SQLite) and for a host with no provider package.
   The check is made once per process. Providers answer it through a new default interface member on the public
   configurator, `bool Supports(DbContextOptions options) => true`. The PostgreSQL configurator returns true only when the
   options carry `NpgsqlOptionsExtension`, the SQL Server one only for `SqlServerOptionsExtension`. A default interface
   member is not a breaking change.
4. **One Warp pool per process** (see "Connection pools").
5. **No public API break.** The 28 affected service classes are public, with public constructors that take `TContext`.
   Those constructors stay. Each class gets an `internal` constructor taking `DbContext`, stores the context as a
   `DbContext`, and `AddWarp` registers it through a factory that passes `IWarpContext.Context`.

## Can `AddWarp` register it without a server?

Yes. Everything the context needs is already in `AddWarp`:

| Need | Where it comes from today | `AddWarp`-only? |
|---|---|---|
| Connection | `IWarpServerContextConfigurator`, registered by `UsePostgreSql`/`UseSqlServer` (builder extension, any process) | yes |
| Connection source | `WarpDatabaseConnection<TContext>` (registered by `AddWarp`) | yes |
| Physical names | `IWarpServerModelNames` (moves to `AddWarp`; reads `TContext.Model` once, lazily) | yes after the move |
| Logging tweaks | `WarpServerConfiguration.EnableServerCommandLogging` (optional; absent means demote to Debug) | absent, so demoted |

The context is scoped, and its options are singleton (built once, as for the server context). Nothing opens a
connection or builds a model until a moved service is first used. A publisher-only process that never queries opens
nothing.

`AddWarp` must also remove the non-generic `DbContextOptions` forwarder that `AddDbContext<WarpContext<TContext>>`
appends, as `AddServerHostCore` does for the server context. Otherwise `dotnet ef` counts the context and asks for
`--context`. The forwarder removal is shared with the server-context registration.

## What moves

All registered in `AddWarp` (Core) unless noted. Each one depends on `TContext` today. Afterwards it depends on
`IWarpContext`.

**Dashboard query services:** `IJobQueryService` (`JobQueryService`), `IJobLogQueryService`, `IJobGroupQueryService`,
`IDashboardStatsService`, `IBackgroundServiceQueryService`, `IAdapterQueryService`, `IApplicationQueryService` +
`IApplicationDeployQueryService` (`ApplicationQueryService`), `IEndpointQueryService`, `IClientEventQueryService`,
`ITraceQueryService`, `IErrorGroupQueryService`, `ISloQueryService`, `IMetricQueryService`, `IMetricBoardQueryService`,
`IMetricAnnotationQueryService`, `IWebhookQueryService`, `ISagaQueryService` (registered by `AddSagas`).

**Dashboard command and admin services:** `IJobCommandService` (also takes `IWarpSqlQueries<TContext>`, whose methods
already take `DbContext`), `IRecurringJobService`, `IServerCommandService`, `IErrorGroupCommandService`,
`ISloCommandService`, `IMetricBoardCommandService`, `IMetricAnnotationCommandService`, `IWebhookCommandService`,
`ISagaCommandService` (session lock unchanged), `IConcurrencyLimitManager` (`ConcurrencyLimitManager`, `AddConcurrency`),
`IRateLimitManager` (`RateLimitManager` behind `GateInvalidatingRateLimitManager`, `AddRateLimit`).

`IDeployVerdictService` has no query of its own and is unchanged.

**Stays on `TContext`:**
- The outbox: `IPublisher`/`Publisher`, `IBatchPublisher`, `IPublisherFactory`, `IRecurringJobPublisher` (the
  application calls it in its own startup or transaction).
- `IWebhookDispatcher` (stages a delivery in the caller's transaction).
- `ISagaStore` (handler scope).
- `ExecuteWebhookDeliveryHandler` (a job handler).

**Not in this change (proposed follow-up):** the autonomous writers that also run in `AddWarp`-only processes:
`AdapterCallFlusher`, `EndpointCallFlusher`, `ClientEventFlusher`, `ContextMetricPointSink`,
`ApplicationHeartbeatHost`, `DroppedRecordReporter`. The same mechanism applies to them, but their tests observe writes
through `TContext` and they are not dashboard or admin services. See decision D1.

Enqueue wake-ups: the Warp context does not carry `EnqueueNotificationInterceptor<TContext>` (#322).
`JobCommandService` and `RecurringJobService` already signal explicitly (`EnqueueNotifyingSave`, `ServerTaskSignals`,
transport). Phase 2 verifies each enqueue path and adds a test that a requeue and a recurring trigger issued from the
dashboard wake a worker.

## Connection pools (§6.22)

- **SQL Server:** the configurator uses `TContext`'s connection string unchanged, so the Warp context shares the
  application's pool. Nothing changes.
- **PostgreSQL, host `NpgsqlDataSource`:** a data source cannot be forked, so the Warp context shares it. Nothing changes.
- **PostgreSQL, connection string:** the Warp context uses the `:warp-server` pool (`No Reset On Close`,
  `MinPoolSize 0`), the same pool as the server context.
  - **Server + dashboard process:** the pool already exists. Dashboard queries move into it, and each checkout skips a
    `DISCARD ALL`.
  - **`AddWarp`-only process (dashboard or publisher):** a second pool appears, but only when a moved service first runs,
    and it holds no idle connections at rest (min 0; idle connections are pruned after Npgsql's idle lifetime).
    Dashboard connections leave the application's pool about as fast as they appear in Warp's. So the peak rises only
    by idle connections that briefly linger in both pools, not by the dashboard's concurrency.

  **Recommendation: accept it.** One rule ("Warp's own work runs on Warp's pool") is simpler than a dashboard-only
  exception. `Application Name …:warp-server` also makes Warp's dashboard traffic visible in `pg_stat_activity`.
  §6.22's invariant (no session state on these connections) holds for every moved service. The only `set_config`
  is the claim plan guard, which is transaction-local and on the server path. The connection-pooling operations
  page gets one paragraph on this.

## Testing story (#359 must not break)

- **`RemoveWarpHostedServices()` + EF in-memory (or SQLite):** the configurator's `Supports` is false for a
  non-Npgsql or non-SQL-Server `TContext` (or no configurator is registered), so `IWarpContext` is the scoped `TContext`.
  Behaviour is byte-for-byte today's: publish to in-memory, then read back through `IJobQueryService` on the same
  in-memory store. This is what the fallback is for. A separate in-memory Warp context could not see the
  application's in-memory store unless both shared one database name and root.
- **`RemoveWarpHostedServices()` with a relational `TContext` and no database:** the Warp context is lazy, so nothing
  connects at startup.
- **New NoDb tests:**
  1. `DeploymentShapeTests`: AddWarp-only with a fake configurator. Every moved service resolves, its context is
     `WarpContext`, and resolving it constructs no `TContext` (a counting `TContext` factory). With in-memory and no
     configurator, or a configurator that rejects the provider, every moved service resolves on `TContext`.
  2. The #359 shape end to end: `RemoveWarpHostedServices` + in-memory, publish, then read back through
     `IJobQueryService`, `IConcurrencyLimitManager` and `IRateLimitManager`.
  3. Design time: in an AddWarp-only process, `GetServices<DbContextOptions>()` yields only `TContext`.
  4. The `Supports` answers of both configurators (in-memory, SQLite, the other provider).
- **New DB tests (`[GenerateDatabaseTests]`, both providers):**
  1. An AddWarp-only host whose `TContext` uses snake_case naming and a command interceptor. A dashboard query, a
     concurrency-limit and rate-limit override write, and a job requeue hit the mirrored tables, and the interceptor
     records none of them.
  2. The #347 regression guard: on the Warp context, `RunId` is not a concurrency token, so a requeue saves when the
     claimed row's run id moved.
  3. Requeue and recurring trigger from an AddWarp-only host wake a worker in a server process.
- PostgreSQL: `ServerConnectionPoolTests` extended. The Warp context in an AddWarp-only process uses the derived
  `:warp-server` string, and uses the host's data source when there is one.
- Existing dashboard and admin suites run unchanged and now exercise the Warp context on both providers.

## Public API and behaviour changes (8.2)

**Public API:** additive only.
- A default interface member `IWarpServerContextConfigurator.Supports(DbContextOptions)`.
- The existing public constructors that take `TContext` stay and still work: a caller that builds a service
  itself gets today's behaviour on its own context.

**Behaviour (release notes):**
1. Dashboard query, command and admin calls run on their own connection, not the caller's `TContext`. Called from
   application code inside the application's own transaction (`BeginTransaction` or an ambient `TransactionScope`),
   they no longer take part in it and do not see the caller's uncommitted rows. On SQL Server without RCSI, a read
   of rows the caller holds uncommitted locks on waits for that transaction. Inside a `TransactionScope`, the second
   connection enlists, which escalates to a distributed transaction.
2. `TContext`'s interceptors, logging configuration, execution strategy (`EnableRetryOnFailure`) and query-splitting
   setting no longer apply to these calls. Their SQL logs at Debug, like the server's, unless
   `EnableServerCommandLogging` is set (server processes). **`TContext`'s command timeout is copied** to the Warp
   context, so a host that raised it for large dashboards keeps it. This is the one setting copied: it is a
   relational option that is safe to mirror.
3. PostgreSQL with a connection string: an `AddWarp`-only process may open the `:warp-server` pool.

## Custom `TContext` configuration

- **Naming conventions** (EFCore.NamingConventions, `ToTable`, schema): invisible. Names are read from `TContext`'s
  model once and pinned (§2.14), and §5.12 guarantees the physical types agree. This is the mechanism the server context
  already relies on.
- **Interceptors:** they no longer see Warp's dashboard and admin SQL. That is intended, and the reason for the issue.
  A host that depends on an interceptor for Warp's tables (for example row-level security keyed by a session setting)
  was already broken for server work, and is listed in the release notes.
- **Command timeout:** copied (see above). **Retry strategy:** not copied. That also fixes a latent bug:
  `JobCommandService` begins its own transactions, which a retrying execution strategy refuses.
- **`AddDbContextPool`:** unaffected. The Warp context is not pooled, and its options are built once.

## Decisions for the user

- **D1 — scope.** Move the 28 dashboard and admin services listed above in this change, and the autonomous `AddWarp`
  writers (flushers, metric sink, application heartbeat, dropped-record reporter) in a follow-up
  **(recommended)**. Or include the writers now.
- **D2 — public constructors.** Keep the public `TContext` constructors and add internal `DbContext` ones
  **(recommended, no break)**. Or change the parameter to `DbContext`, which keeps source compatibility but breaks
  binary compatibility.
- **D3 — PostgreSQL pool in `AddWarp`-only processes.** Use Warp's `:warp-server` pool everywhere
  **(recommended)**. Or keep dashboard-only processes on the application's pool.
- **D4 — behaviour notes 1–3 above** are acceptable for a minor release, documented in the 8.2 release notes and
  §2.14, with `TContext`'s command timeout copied.

## Implementation notes

- Webhook redelivery and endpoint probes: on the Warp context, Warp's own `IWebhookRedeliveryEnqueuer` (which publishes through the
  `TContext`-bound `IPublisher`, another connection) would commit the executor job outside the reset's transaction. The command
  service stages the job itself with `WebhookExecutorJobs` (moved from `Warp.Worker` to Core) on the same context and dispatches
  its wake after the commit. A host-registered enqueuer is still called.
- `ConcurrencyLimitManager` is now a factory registration; `ReplacedRegistration` recognises Warp's own by the factory instance (§8.8.1).
- Command logging: the internal `WarpCommandLogging` (registered by `AddWarpServer` from `EnableServerCommandLogging`) drives the
  Warp context's Debug demotion; an `AddWarp`-only process always demotes.

## Rules and docs (Phase 2)

- §2.14 (rule + design note): three contexts. Server context: autonomous server work. Warp context: dashboard,
  query and admin services in every `AddWarp` process, falling back to `TContext` on a provider the configurator
  doesn't support. `TContext`: the outbox and user code only.
- §6.22 design note: the Warp context shares the server pool.
- Website: connection pooling, testing apps (the fallback), and the 8.2 release notes.
