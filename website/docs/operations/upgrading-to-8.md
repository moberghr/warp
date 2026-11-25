---
sidebar_position: 5
---

# Upgrading to 8.0

This page covers the upgrade from 7.7 to 8.0. Two things need work before you deploy: handlers that relied on the worker saving their `DbContext`, and a migration that recreates four indexes on the job table. The rest is a list of what to expect while 7.7 and 8.0 servers run side by side.

## Before you deploy

1. Find every job and message handler that changes entities or stages jobs and returns without calling `SaveChangesAsync`. Add the call and ship that on 7.7 first. See [Handlers save their own DbContext](#handlers-save-their-own-dbcontext).
2. Generate the migration and read it. Decide how you will apply the four index changes on the job table. See [The migration](#the-migration).
3. Apply the migration before any 8.0 process starts. See [Order of the deploy](#order-of-the-deploy).
4. PostgreSQL: the server must be version 13 or newer; make sure you do not run Npgsql in legacy timestamp mode, and check `max_connections`. See [PostgreSQL](#postgresql).
5. SQL Server: check the database compatibility level is 130 or higher. See [SQL Server](#sql-server).
6. SQL Server with `UseDatabasePush()`: the application's login now needs `CREATE QUEUE` and `CREATE SERVICE` at run time. Otherwise nothing to plan: 8.0 bridges to 7.7's shared Service Broker queue during the deploy. See [Rolling deploy](#rolling-deploy-77-and-80-side-by-side).
7. Check the clock difference between your application hosts and the database. See [Rolling deploy](#rolling-deploy-77-and-80-side-by-side).
8. If you implement `IWarpSqlQueries`, `IBatchPublisher` or a notifier, read [API changes](#api-changes).
9. Decide whether to keep the new default worker mode. Dispatcher mode is on unless you set `UseDispatcher = false`. See [Dispatcher mode is the default](#dispatcher-mode-is-the-default).

## Handlers save their own DbContext

**Breaking.** In 7.x, after a job or message handler returned, the worker resolved the handler scope's `DbContext` and called `SaveChangesAsync` on it. In 8.0 the worker does not resolve or save your `DbContext` at all.

A handler that changes entities, or calls `Enqueue`, `Schedule` or `Publish` on `IPublisher` / `IBatchPublisher`, and returns without saving now loses those changes and those staged jobs. The job itself still completes.

- Staged jobs that were not saved produce a Warning when the scope ends (`WarnOnUnsavedStagedJobs`, on by default): `Warp: N job(s)/message(s) were staged via IPublisher but the scope ended without SaveChangesAsync — they were discarded and will not run.`
- Plain entity changes that were not saved are dropped with no log line. Nothing detects them.

Before:

```csharp
public class ShipOrderHandler(AppDbContext db, IPublisher publisher) : IJobHandler<ShipOrder>
{
    public async Task HandleAsync(ShipOrder message, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FindAsync([message.OrderId], cancellationToken);
        order!.Status = OrderStatus.Shipped;
        await publisher.Enqueue(new SendShippingEmail(message.OrderId));

        // 7.x: the worker saved the context here. 8.0: both changes are lost.
    }
}
```

After:

```csharp
public class ShipOrderHandler(AppDbContext db, IPublisher publisher) : IJobHandler<ShipOrder>
{
    public async Task HandleAsync(ShipOrder message, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FindAsync([message.OrderId], cancellationToken);
        order!.Status = OrderStatus.Shipped;
        await publisher.Enqueue(new SendShippingEmail(message.OrderId));

        await publisher.SaveChangesAsync(cancellationToken);
    }
}
```

`publisher.SaveChangesAsync` and `db.SaveChangesAsync` save the same context, so one call covers the entity change and the staged job in one transaction.

**Fix the handlers before you deploy 8.0.** During a rolling deploy the same handler behaves differently depending on which version claims the job: a 7.7 worker saves for it, an 8.0 worker does not. The fix is safe on 7.x, where the worker's own save after the handler then finds nothing to write.

Saga handlers are not affected: the saga proxy still saves for them.

## Dispatcher mode is the default

`UseDispatcher` defaults to `true` in 8.0. Each worker group has one dispatcher that claims jobs for its free workers in one statement, and the group's completions are written in batches. Servers that set nothing switch mode with the upgrade. It cuts statements, allocations and connections per job several times over (see [Warp vs Hangfire](../performance/warp-vs-hangfire.md)), and changes three things you can observe:

- A finished job shows `Processing` until its batch is written, up to `CompletionFlushInterval` (100 ms by default).
- After a crash, jobs whose completion was still buffered run again: `StaleJobRecovery` requeues them unless they are `[NoRestart]`. Handlers must already be safe to run more than once; this widens the window.
- A job claimed ahead of a free worker (prefetch, on by default: `PrefetchCount` is `WorkerCount` unless set) is `Processing` on the server that claimed it. It waits behind that server's running jobs, even while another server has idle workers, and if the server dies before running it, it waits for crash recovery. For long-running jobs across several servers, set `PrefetchCount = 0`, globally or on that worker group.

To keep 7.x's single-worker mode:

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.UseDispatcher = false;
});
```

Servers in either mode can run side by side, and 7.7 servers in either mode work with 8.0 servers in either mode. Database push (`UseDatabasePush()`) stays opt-in: it needs a session-mode connection on PostgreSQL and Service Broker on SQL Server.

## The migration

Generate it with your normal `dotnet ef migrations add <Name>`. It contains:

| Change | Kind |
|---|---|
| `Job.RunId` | new nullable column |
| `Server.CoordinationVersion` | new nullable column |
| `Server.ClockOffsetMs` | new nullable column |
| `ServerLog.TaskName` | new nullable column (max length 200) |
| `ClusterTask` | new table, primary key `Name` |
| Four indexes on the job table | dropped and created again with a filter |

The four indexes become partial (PostgreSQL) / filtered (SQL Server):

| Index columns | Filter |
|---|---|
| `Kind`, `CurrentState`, `Queue`, `ScheduleTime` | `CurrentState` = Enqueued |
| `CurrentState`, `ScheduleTime` | `CurrentState` = Scheduled |
| `ParentJobId`, `CurrentState` | `ParentJobId IS NOT NULL` |
| `ExpireAt` | `ExpireAt IS NOT NULL` |

The columns and the table are cheap. The indexes are not. EF Core generates a `DropIndex` followed by a `CreateIndex` for each one, and runs them inside the migration's transaction. Dropping an index takes an exclusive lock on the job table, and the lock is held until the transaction commits, so the table stays locked through all four index builds. Claims, completions and publishes wait for that whole time. On a small job table this is a moment. On a large one it is an outage of the job system for as long as the builds take.

You have two options.

### Option 1: run the migration in a quiet window

Stop the servers or pick a time with little traffic, and apply the migration as generated.

### Option 2: edit the migration to build the indexes online

Remove the generated `DropIndex` / `CreateIndex` pairs for the four indexes and call the provider's helper in their place, passing the migration's `TargetModel`. The helper reads the index names, the table, the columns and the filters from that model, so they match your schema and naming convention; there is nothing to copy by hand.

```csharp
using Warp.Provider.PostgreSql; // or Warp.Provider.SqlServer

public partial class UpgradeWarpTo8 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ... the generated column and table changes stay as they are ...

        // In place of the four DropIndex / CreateIndex pairs on the job table:
        migrationBuilder.RecreateWarpJobIndexesOnline(TargetModel);
    }
}
```

**Put the helper in a migration of its own**, after the one with the column and table changes. Its statements run with `suppressTransaction: true`, outside the migration's transaction: EF Core commits everything before the first of them and records the migration as applied only at the end. If an index build fails in a migration that also adds the columns, the columns are already committed, the migration is not recorded, and running it again fails on the column that already exists. Alone in its migration, the helper can be run again as often as needed.

To split it: generate the migration, move the four `DropIndex` / `CreateIndex` pairs (in `Up` and `Down`) into a second, hand-written migration class with a later timestamp id, and replace the pairs in its `Up` with the helper call. Both migrations share the model snapshot that was generated.

**Command timeout.** Each index build is one command and is subject to the migration's command timeout (30 seconds by default), with the helper and with the generated pairs alike. On a large job table raise it for the migration run (`Command Timeout` in the connection string, `CommandTimeout(...)` in the provider options, or `context.Database.SetCommandTimeout(...)` before `Migrate()`).

**PostgreSQL.** For each index: `CREATE INDEX CONCURRENTLY` under a temporary name (`<index>_warp_new`), `DROP INDEX CONCURRENTLY` of the old one, then a rename to the old name. Neither concurrent statement blocks claims, completions or publishes. A concurrent build that fails leaves an invalid index behind; the helper drops a leftover temporary index before it builds, so running the migration again is enough. `CREATE INDEX CONCURRENTLY` cannot run inside a transaction or a function, so the helper works with `dotnet ef database update`, `Database.Migrate()`, a migration bundle, or a plain generated script run outside a transaction. It does not work in an idempotent script (`dotnet ef migrations script --idempotent` wraps each statement in a `DO` block) or with any tool that wraps the script in one transaction (`psql -1`, and the default mode of several migration runners). There, use option 1.

**SQL Server.** For each index, one `CREATE INDEX ... WITH (DROP_EXISTING = ON, ONLINE = ON)`, which replaces the index in place, filter included, while the table stays available. Online index operations exist on Enterprise (and Developer), Azure SQL Database and Azure SQL Managed Instance. On other editions the helper builds with `ONLINE = OFF`, which locks the table for each build (but not across all four, since each statement commits on its own): use a quiet window there.

Filtered indexes on SQL Server need `QUOTED_IDENTIFIER ON`, both to create them and for any session that later writes to the table. `SqlClient` and EF Core set it by default. `sqlcmd` does not: pass `-I` when you run a generated migration script with it. The same goes for anything else of yours that writes to Warp's job table with the option off, such as a SQL Agent T-SQL step: it fails with error 1934 once the indexes are filtered. A database with `PARAMETERIZATION FORCED` cannot use filtered indexes for parameterized statements; Warp's claim compares the state to a literal for that reason, but your own queries on the job table may scan.

Leave the rest of the generated migration as it is.

## Order of the deploy

Apply the migration first, for every tier.

- An 8.0 server on a database without the migration fails at startup: registering the server writes the new `Server` columns.
- An 8.0 process that only publishes starts, and then fails every publish: the `Job` insert names the new column.
- 7.7 binaries run on the 8.0 schema. Every change is additive (nullable columns, a new table, narrower indexes), so you can roll the binaries back without rolling back the migration.

### Rolling back

Leave the migration applied and redeploy 7.7. **Stop every 8.0 server before the first 7.7 server starts**, or accept a short overlap: an 8.0 server learns that a 7.7 server has appeared at its next cluster-task claim, and a task run it started before that does not hold the old lock, so the same server task can briefly run on both. After a rollback on SQL Server with `UseDatabasePush()`, the per-process queues of 8.0 processes that were killed (not stopped) stay behind, since only 8.0 listeners sweep them; drop the `{channel}/queue/{id}` queues and `{channel}/service/{id}` services by hand if you stay on 7.7.

## Rolling deploy: 7.7 and 8.0 side by side

**Server tasks.** Cluster-wide server tasks (cleanup, rollups, the orchestrator and the others) stay mutually exclusive across versions. While a 7.7 server is alive, an 8.0 server also takes the task's old lock around each run, so a task never runs on an old and a new server at the same time. During the mix a task may run about twice as often, once from each side. This bridge is removed in the release after 8.x, so do not skip 8.x when you upgrade from 7.x.

**Run fencing.** 8.0 stamps every claim with a run id (`Job.RunId`) and guards every write a run makes with it, so a run whose job was taken back and claimed again cannot write over the new run. 7.7 servers do not write or check the run id. The protection is complete only once every server is on 8.0.

**SQL Server with `UseDatabasePush()`.** 7.7 used one shared Service Broker queue (`{channel}/queue`, service `{channel}/service`) for all processes. 8.0 gives each listening process its own queue. So that the two versions still wake each other during the deploy, 8.0 bridges to the shared queue while it exists: every 8.0 publish is also sent to the shared service, and every 8.0 listener also receives from the shared queue and passes what a 7.7 process sent on to every 8.0 listener. The shared queue still hands each message to one receiver, as in 7.7, so a 7.7 process is woken by some notifications, not all, and finds the rest by polling, as it did before. After the last 7.7 process has stopped you can drop that service and queue (`DROP SERVICE [{channel}/service]; DROP QUEUE [{channel}/queue];`) to end the bridge; 8.0 never creates them. Until you drop them, every 8.0 publish opens one extra dialog and every 8.0 listener holds one extra connection for the bridge. Dropping the service also removes the conversation endpoints 7.7 left behind (it never ended them), which can take a while on a long-lived database. The bridge is removed in the release after 8.x. PostgreSQL push is not affected.

8.0 creates Service Broker objects at run time, which 7.7 did not: each listening process creates its own queue and service when it starts and drops them when it stops, and the first 8.0 process creates the sender queue and service. The application's login therefore needs `CREATE QUEUE` and `CREATE SERVICE` on the database (members of `db_ddladmin` and `db_owner` have them). On 7.7 a DBA could create the shared objects once; per-process names cannot be created ahead. Without the permissions push is disabled for that process, which logs the setup failure, and it finds work by polling, at up to `MaxPollingInterval` (5 minutes with `UseDatabasePush()`).

**Clocks.** 8.0 servers work on the database's clock (see [Clock skew between servers](crash-recovery.md#clock-skew-between-servers)); 7.7 servers use their own. While a 7.7 server is alive, 8.0 servers keep to their own clock too, as 7.7 did: they do not apply the correction (they still measure it, store it as `Server.ClockOffsetMs` and warn), and the heartbeat writes the server's own time for itself, its background-service instances and its lease renewals. A mixed cluster is therefore no worse than a pure 7.7 one. The database clock takes over once no `Server` row without `CoordinationVersion` is left (checked at startup and on every cluster-task claim). A 7.7 server counts for as long as its row exists, whatever its heartbeat says, because that heartbeat is written on the 7.7 host's clock; the row of a 7.7 server that died is removed by `ServerCleanup` after `HealthCheckTimeout`, as on 7.7. Processes without a server write the database's time on their application-instance heartbeat, or their own while a 7.7 server is alive. Still check the difference between your application hosts' clocks and the database's before the deploy (`SELECT now()` / `SELECT SYSUTCDATETIME()` against a host's UTC time): once the last 7.7 server is gone, every 8.0 server moves to the database's clock in one step, and a difference larger than `ClockSkewWarningThreshold` is reported as a warning from then on.

## PostgreSQL

**PostgreSQL 13 or newer is required.** The claim stamps each run with `gen_random_uuid()`, which is built in from version 13. An 8.0 server checks the version when it starts and refuses to start on an older one. A process that only publishes is not checked.

**Legacy timestamp mode is not supported.** Warp's time columns must be `timestamp with time zone`. 8.0 sends timestamps to the database typed as `timestamp with time zone` and compares them with the database's own clock. If you set the `Npgsql.EnableLegacyTimestampBehavior` AppContext switch, EF Core maps `DateTime` to `timestamp without time zone` and those comparisons depend on the session time zone. Remove the switch and migrate the columns before upgrading. Every 8.0 process that uses the PostgreSQL provider checks its model when it starts and refuses to start, naming the columns, while any of Warp's time columns is mapped to another type. The check reads the model only, not the database.

Plan this change on its own, before the upgrade, because it reaches beyond Warp:

- The switch is process-wide. Removing it changes how EF Core maps every `DateTime` in your own model too, so the migration that follows converts your columns as well as Warp's.
- `ALTER COLUMN ... TYPE timestamp with time zone` reads the existing values in the session's `TimeZone`. Run the conversion with `SET TIME ZONE 'UTC'` (or whatever zone the stored values are in), or every value shifts.
- Keeping the switch and only retyping Warp's columns with `HasColumnType("timestamptz")` passes the startup check but is not supported: Npgsql still sends `DateTime` parameters as `timestamp without time zone`.
- Convert before the roll, with 7.7 running without the switch. A 7.7 process that still has the switch on must not run against converted columns.

**A third connection pool.** 8.0 server work runs on a pool of its own (`Application Name` suffixed `:warp-server`), next to your `DbContext`'s pool and the lock pool (`:warp-locks`). Each pool sizes to its own demand, so peak connections per process go up. During a rolling deploy old and new pods also overlap. Check `max_connections` has room for both. See [Connection Pooling](connection-pooling.md). Hosts that register the context with their own `NpgsqlDataSource` keep the shared pool.

## SQL Server

**Compatibility level 130 or higher is required.** The batched completion and the keep-alive renewal use `OPENJSON`. An 8.0 server checks the level when it starts and refuses to start below 130, with the statement to run:

```sql
ALTER DATABASE [YourDatabase] SET COMPATIBILITY_LEVEL = 130;
```

A process that only publishes (`AddWarp` without `AddWarpServer`) is not checked. One operation outside a server still needs the level: bulk requeue from the dashboard (`IJobCommandService.BulkRequeueJobs`) locks its rows with `OPENJSON`, so a dashboard-only host on a database below 130 fails that action.

## API changes

For application code:

- **`IBatchPublisher.StartNew` and `ContinueBatchWith` have a new optional `queue` parameter.** Callers compile unchanged but must be recompiled (the method signature changed). Implementations and mocks of the interface need the new parameter.
- **`WarpEventType.ClockSkewDetected = 9` is new.** A notifier that switches over every event type needs a case for it.
- **`ServerTaskSignalInterval` is new, default 500 ms.** A signal-woken server task runs at most once per interval, so a continuation or a routed message can wait up to 500 ms before it is picked up. Lower it, or set it to `null`, to trade database load for latency.
- **`RateLimitStore<TContext>` and `CircuitBreakerStore<TContext>` are no longer public.** `IRateLimitStore` and `ICircuitBreakerStore` remain. A custom `IRateLimitManager` or `IConcurrencyLimitManager` you registered still decides a job's limit, as in 7.x.
- **Single-worker mode discards a finalization whose row was taken back.** When a job's row was reclaimed and claimed again (or deleted) while a run held it, the old run's result is dropped with an Information log line instead of being recorded as a handler failure.
- **Dispatcher workers wait for a slow completion write.** Once a worker group holds 4 × `CompletionBatchSize` unwritten completions (never fewer than 200), its workers wait for the write in flight before taking the next job. With a healthy database this is never reached.
- **Cross-process push notifications need a running host.** With `UseDatabasePush()`, notifications for published jobs are sent by a hosted service, off the saving thread. A process that publishes but never starts its hosted services (a console tool that builds a service provider and calls `IPublisher` directly) no longer sends them; other processes find its jobs by polling.

For custom providers (`IWarpSqlQueries<TContext>` implementations):

- `ClaimEnqueuedJobsAsync` has a new `ClaimWatermark? watermark` parameter (before the `CancellationToken`).
- New required members: `LockJobsByIdWaitAsync`, `CompleteJobsAsync`, `RenewRunningJobsAsync`, `ClaimClusterTasksAsync`, `RequestClusterTaskRunAsync`, `RenewClusterTaskLeaseAsync`, `CompleteClusterTaskAsync`, `RegisterClusterTasksAsync` (insert-if-absent: it must not fail on a row another server inserted first).
- The claim's result reports `ClusterTaskClaims.OtherServersAlive` (default `true`: a lone server then still releases the lease of an idle signal task's run, one write each) and, per task, `ClusterTaskState.Scheduled` (default `false`: the Orchestrator then sweeps every parent only by its stopwatch cadence, not on every scheduled run).
- `HeartbeatAsync` takes a new `bool writeLocalTime` parameter (before the cancellation token). Without it, the heartbeat now writes the database's time, not the `now` argument, to the heartbeat and lease columns; with it (a 7.7 server is alive) it writes `now`, as 7.7 did. Either way it stores the difference as `Server.ClockOffsetMs` and returns the database time in `HeartbeatResult.DatabaseUtcNow`. A provider that leaves `DatabaseUtcNow` null gets no clock correction.
- Every claim must write a fresh `Job.RunId`, and the completion and renewal statements must compare it.
- `WarpJobTableNames` has two new `required` members, `RunId` and `ServerClockOffsetMs`. Code that builds one with an object initializer must set them.
- The claim's result also reports `ClusterTaskClaims.LegacyServersAlive`: whether any `Server` row without `CoordinationVersion` exists. Return it truthfully. While it is true, 8.0 servers also take each task's old lock and keep to their own clock; a provider that always returns `false` loses task exclusivity with 7.7 servers during the roll.

For code that constructs Warp's server types itself:

- The public constructors of `Heartbeat<TContext>` and `WarpDispatcherHost<TContext>` changed.
- `DispatcherWorkerAvailability.Release()` returns `int` (the slots still claimed). Callers must be recompiled.
- Server components resolve their clock as a keyed service (`DatabaseClock.Key`), so the container must support keyed services. `Microsoft.Extensions.DependencyInjection` 8 and later does.
- A custom `IServerTask` with a `LockKey` now runs under a lease, once per tick for the cluster, and writes a `ServerLog` row only when it returns a message.

## Dashboard during the roll

The dashboard's JavaScript and CSS files have hashed names that change with each version. With 7.7 and 8.0 instances behind one load balancer, a browser can load `index.html` from one version and ask the other for its assets, which that instance does not have. Use sticky sessions for the dashboard during the roll, or expect to reload the page once the roll is done.
