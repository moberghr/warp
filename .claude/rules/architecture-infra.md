---
paths:
  - "src/core/Warp.Core/Notifications/**"
  - "src/core/Warp.Core/Locking/**"
  - "src/core/Warp.Core/IWarp{Lock,Semaphore}Provider.cs"
  - "src/core/Warp.Core/{DatabaseClock,LegacyServerPresence,ApplicationHeartbeatHost,WarpDatabaseConnection}.cs"
  - "src/core/Warp.Worker/{DatabasePushServiceConfiguration,DispatcherRegistry,WarpServerRegistration}.cs"
  - "src/core/Warp.Worker/Services/*{Heartbeat,ClockSkew,ServerCleanup,StaleJob}*.cs"
  - "src/core/providers/**"
  - "src/core/Warp.Dashboard/**"
  - "src/ui/src/lib/realtimeFeed*.ts"
  - "src/tests/Warp.Tests/{Notifications,DashboardPush}/**"
  - "src/tests/Warp.Tests/Features/Locking/**"
  - "src/tests/Warp.Tests/**/*{Clock,Heartbeat}*.cs"
---

# Architecture — push, lock pool, clock (§2.9, §2.10, §2.18, §2.20)

> Loaded only when touching the paths above. Rules only — rationale, history, measurements and full rot-guard lists live in `.claude/references/design-notes/<section>-*.md`; **read the note before changing a section's area**.

## DB Push

- **§2.9** **DB push** is opt-in: `opt.UseDatabasePush()` (Postgres LISTEN/NOTIFY, SQL Server Service Broker) via `IWarpNotificationTransportFactory` — **call the provider first, push second**.
  - **Every listening process receives every notification on both providers.** On SQL Server each listening process has its own queue and service (`{channel}/queue/{id}`, `{channel}/service/{id}`) and `PublishAsync` sends to every `{channel}/service/` service; dead processes' queues are swept; the listener ends each conversation it receives; setup is serialized with `sp_getapplock`. Do not go back to one shared queue.
  - **A stopping SQL Server listener wakes its own queue** (`SendWakeAsync`, a body-less `{channel}/msg`): SQL Server 2025 ignores a cancel reaching a starting `WAITFOR (RECEIVE)`. Listener and relay skip body-less messages.
  - **Rolling-deploy bridge (REMOVE in the release after 8.x, with `LegacyTaskLock`):** while 7.7's shared `{channel}/service` exists, `PublishAsync` also sends to it and every 8.0 listener runs `RelayLegacyQueueAsync`, re-sending 7.7's messages to the per-process services — never back to the shared one.
  - **Transports must not throw from `PublishAsync`** (log + `WarpTelemetry.NotificationPublishFailures`). Missed notifications are caught by drain-on-reconnect.
  - **RECEIVE:** `NotificationListenerTask<TContext>` lives in `Warp.Core.Notifications` and works in any `AddWarp` process; it takes no server-only deps. It wakes bare workers through `ServerTaskSignals.SignalJobEnqueued` and dispatchers through the Core `IDispatcherWake` seam (`IEnumerable<IDispatcherWake>`, empty without `AddWarpServer`). In-process, every `WarpWorker` subscribes to `ServerTaskSignal.JobEnqueued`, independent of `UseDatabasePush`.
  - **SEND (#322):** every committed `TContext` save is announced by `EnqueueNotificationInterceptor<TContext>` (on `TContext` only; server tasks dispatch their own from `OnCommittedAsync`). It wakes this process inline and hands notifications to `EnqueueAnnouncements`; `EnqueueNotificationRelay` (a §2.19 `DrainService`) publishes them off the saving thread, one per (kind, queue) per batch. **The interceptor holds no transport** (that deadlocked startup) and **the worker sends nothing inline** — do not reintroduce either. Single-worker `JobFinalized` goes through the same relay (`FinalizedParentsPayload.Merge`). Explicit call sites use `EnqueueNotifyingSave`.
  - Guards: `SqlServerNotificationTransportTests`, `DatabasePushIntegrationTests`, `EnqueueNotificationRelayTests`.
- **§2.10** **Realtime dashboard push** is opt-in: `opt.AddDashboardPush()` registers `WarpDashboardHub` (SignalR, `${RoutePrefix}/api/hub`) and `DashboardBroadcaster<TContext>`, the third consumer of the signal pipe. Broadcasts are spaced `MinBroadcastInterval` apart (1s) and the `DashboardStatistics` snapshot is fetched once per broadcast and **only while this process's hub has a connected client** (`DashboardConnectionCounter`) — **do not drop either guard** (~20 stats queries per broadcast). Per-view data is not pushed. Multi-server fanout reuses §2.9. The frontend reads `${RoutePrefix}/api/addons` (`push` flag); without a connected hub `src/ui/src/lib/realtimeFeed.ts` polls `/status` every 1 s. A non-server dashboard host (`AddWarp` + provider + `UseDatabasePush()` + `AddDashboardPush()`) drives realtime push.

## Distributed Locking

- **§2.18** **On PostgreSQL the lock providers get their own Npgsql pool** (`ResolveLockConnectionString`: `Application Name` suffixed `:warp-locks`, `MinPoolSize` 0) — Medallion's prepared statements otherwise flip every EF connector to the seven-statement reset. The connection string is read from `TContext` through `WarpDatabaseConnection<TContext>` (never constructs Warp's interceptors, never `IWarpServerContext`; must resolve in `AddWarp`-only processes). The data-source path is not split — those hosts pass `UsePostgreSql(lockDataSource)`. Two pools RAISE peak connections (budget `peak(TContext) + peak(locks)`); ceilings are inherited, not capped. Guards: `LockConnectionPoolIsolationTests`, `LockConnectionPoolWiringTests`.

## Time

- **§2.20** **Cross-server times are the database's (#350).** Every DI-resolved `Warp.Worker` component takes its `TimeProvider` as `[FromKeyedServices(DatabaseClock.Key)]`; host-built pieces get it from their host. Only `GetUtcNow` is corrected (local clock + offset measured by the heartbeat, stored as `Server.ClockOffsetMs`); timers stay local. **Measured only while the periodic heartbeat is on** (`HealthCheckInterval != null`). Past `ClockSkewWarningThreshold` (1 s): Warning + `ClockSkewDetected` once per excursion.
  - A new server component taking a plain `TimeProvider` fails `DatabaseClockTests.Constructors_EveryDiResolvedServerComponent_TakesTheDatabaseClock`; add only host-built types to its allowlist.
  - **Mixed 7.7/8.0 clusters (REMOVE in the release after 8.x, with `LegacyTaskLock`):** while a 7.7 server row exists (`LegacyServerPresence`, starts alive), 8.0 applies NO offset and `HeartbeatAsync(..., writeLocalTime: true, ...)` writes local time. **A 7.7 row counts for as long as it EXISTS; its heartbeat is never compared.**
  - `AddWarp`-only processes: `ApplicationHeartbeatHost` writes `LastHeartbeatAt` in ONE `ExecuteUpdate` (database time unless a legacy server exists).
  - **Tests** that compare a server-stamped time with their own `DateTime.UtcNow` run with `HealthCheckInterval = null` (correction off).
  - Guards: `ClockSkewIntegrationTests`, `DatabaseClockTests`, `HeartbeatSqlQueryTests`, `ApplicationRegistryTests`, `ClusterTaskQueriesTests.ClaimClusterTasks_ServerFromBeforeTheLeaseWithAnOldHeartbeat_IsAliveUntilItsRowIsGone`.
