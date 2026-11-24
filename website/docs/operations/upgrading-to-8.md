---
sidebar_position: 5
---

# Upgrading to 8.0

This page covers the upgrade from 7.7 to 8.0. Two things need work before you deploy: handlers that relied on the worker saving their `DbContext`, and a migration that recreates four indexes on the job table. The rest is a list of what to expect while 7.7 and 8.0 servers run side by side.

## Before you deploy

1. Find every job and message handler that changes entities or stages jobs and returns without calling `SaveChangesAsync`. Add the call and ship that on 7.7 first. See [Handlers save their own DbContext](#handlers-save-their-own-dbcontext).
2. Generate the migration and read it. Decide how you will apply the four index changes on the job table. See [The migration](#the-migration).
3. Apply the migration before any 8.0 process starts. See [Order of the deploy](#order-of-the-deploy).
4. PostgreSQL: make sure you do not run Npgsql in legacy timestamp mode, and check `max_connections`. See [PostgreSQL](#postgresql).
5. SQL Server: check the database compatibility level is 130 or higher. See [SQL Server](#sql-server).
6. SQL Server with `UseDatabasePush()`: plan to upgrade every process together, or lower the polling intervals for the deploy. See [Rolling deploy](#rolling-deploy-77-and-80-side-by-side).
7. Check the clock difference between your application hosts and the database.
8. If you implement `IWarpSqlQueries`, `IBatchPublisher` or a notifier, read [API changes](#api-changes).

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

Remove the generated `DropIndex` / `CreateIndex` pairs for the four indexes and replace them with SQL that does not hold the table lock. Take the index names, the table name, the column list and the filter text from the `CreateIndex` calls in **your** generated migration. The names below are placeholders; yours depend on your schema and naming convention.

Statements with `suppressTransaction: true` run outside the migration's transaction. If one fails, the steps before it stay applied. Putting the index work in a migration of its own makes that easier to recover from.

**PostgreSQL.** Build the new index under a temporary name, drop the old one, rename. `CREATE INDEX CONCURRENTLY` cannot run in a transaction, and each statement needs its own `Sql` call:

```csharp
// Repeat for each of the four indexes.
// <...> are placeholders: use the names, columns and filter from your generated migration.
migrationBuilder.Sql(
    """CREATE INDEX CONCURRENTLY "<index_name>_new" ON <schema>.<job_table> (<columns>) WHERE <filter>;""",
    suppressTransaction: true);

migrationBuilder.Sql(
    """DROP INDEX CONCURRENTLY <schema>."<index_name>";""",
    suppressTransaction: true);

migrationBuilder.Sql(
    """ALTER INDEX <schema>."<index_name>_new" RENAME TO "<index_name>";""",
    suppressTransaction: true);
```

A concurrent build that fails leaves an invalid index behind. Drop it before you run the migration again.

**SQL Server.** `DROP_EXISTING` replaces the index in one statement, filter included. `ONLINE = ON` keeps the table available during the build, on editions that support online index operations (Enterprise, Azure SQL Database). On other editions leave `ONLINE = ON` out and use a quiet window.

```csharp
// Repeat for each of the four indexes.
// <...> are placeholders: use the names, columns and filter from your generated migration.
migrationBuilder.Sql(
    """
    CREATE INDEX [<index_name>] ON [<schema>].[<job_table>] (<columns>)
    WHERE <filter>
    WITH (DROP_EXISTING = ON, ONLINE = ON);
    """,
    suppressTransaction: true);
```

Filtered indexes on SQL Server need `QUOTED_IDENTIFIER ON`, both to create them and for any session that later writes to the table. `SqlClient` and EF Core set it by default. `sqlcmd` does not: pass `-I` when you run a generated migration script with it.

Leave the rest of the generated migration as it is.

## Order of the deploy

Apply the migration first, for every tier.

- An 8.0 server on a database without the migration fails at startup: registering the server writes the new `Server` columns.
- An 8.0 process that only publishes starts, and then fails every publish: the `Job` insert names the new column.
- 7.7 binaries run on the 8.0 schema. Every change is additive (nullable columns, a new table, narrower indexes), so you can roll the binaries back without rolling back the migration.

## Rolling deploy: 7.7 and 8.0 side by side

**Server tasks.** Cluster-wide server tasks (cleanup, rollups, the orchestrator and the others) stay mutually exclusive across versions. While a 7.7 server is alive, an 8.0 server also takes the task's old lock around each run, so a task never runs on an old and a new server at the same time. During the mix a task may run about twice as often, once from each side. This bridge is removed in the release after 8.x, so do not skip 8.x when you upgrade from 7.x.

**Run fencing.** 8.0 stamps every claim with a run id (`Job.RunId`) and guards every write a run makes with it, so a run whose job was taken back and claimed again cannot write over the new run. 7.7 servers do not write or check the run id. The protection is complete only once every server is on 8.0.

**SQL Server with `UseDatabasePush()`.** 7.7 used one shared Service Broker queue for all processes. 8.0 gives each listening process its own queue. 7.7 and 8.0 processes therefore do not wake each other. Until the last 7.7 process is gone, work enqueued by the other version is found by polling, and `UseDatabasePush()` raises the polling intervals: `MaxPollingInterval` to 5 minutes, `OrchestrationInterval` to 1 minute and `MessageRoutingInterval` to 30 seconds. Either upgrade all processes together, or set those intervals back to short values for the deploy (set them after `UseDatabasePush()` in the lambda; an explicit value wins). PostgreSQL push is not affected.

**Clocks.** 8.0 servers work on the database's clock (see [Clock skew between servers](crash-recovery.md#clock-skew-between-servers)). 7.7 servers, and processes without a server on either version, use their own. Check the difference between your application hosts' clocks and the database's before the deploy. An 8.0 server logs a Warning and raises a `ClockSkewDetected` notification when its own clock is more than 1 second off. During the mix, timestamps written by 7.7 servers are read by 8.0 servers against the database clock and the other way round, so a large difference can make a live server or a held singleton background-service lease look expired, and the lease can change hands.

## PostgreSQL

**Legacy timestamp mode is not supported.** Warp's time columns must be `timestamp with time zone`. 8.0 sends timestamps to the database typed as `timestamp with time zone` and compares them with the database's own clock. If you set the `Npgsql.EnableLegacyTimestampBehavior` AppContext switch, EF Core maps `DateTime` to `timestamp without time zone` and those comparisons depend on the session time zone. Remove the switch and migrate the columns before upgrading.

**A third connection pool.** 8.0 server work runs on a pool of its own (`Application Name` suffixed `:warp-server`), next to your `DbContext`'s pool and the lock pool (`:warp-locks`). Each pool sizes to its own demand, so peak connections per process go up. During a rolling deploy old and new pods also overlap. Check `max_connections` has room for both. See [Connection Pooling](connection-pooling.md). Hosts that register the context with their own `NpgsqlDataSource` keep the shared pool.

## SQL Server

**Compatibility level 130 or higher is required.** The batched completion and the keep-alive renewal use `OPENJSON`. An 8.0 server checks the level when it starts and refuses to start below 130, with the statement to run:

```sql
ALTER DATABASE [YourDatabase] SET COMPATIBILITY_LEVEL = 130;
```

A process that only publishes (`AddWarp` without `AddWarpServer`) is not checked.

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
- New required members: `LockJobsByIdWaitAsync`, `CompleteJobsAsync`, `RenewRunningJobsAsync`, `ClaimClusterTasksAsync`, `RequestClusterTaskRunAsync`, `RenewClusterTaskLeaseAsync`, `CompleteClusterTaskAsync`.
- `HeartbeatAsync` now writes the database's time, not the `now` argument, to the heartbeat and lease columns, stores the difference as `Server.ClockOffsetMs`, and returns the database time in `HeartbeatResult.DatabaseUtcNow`. A provider that leaves `DatabaseUtcNow` null gets no clock correction.
- Every claim must write a fresh `Job.RunId`, and the completion and renewal statements must compare it.

## Dashboard during the roll

The dashboard's JavaScript and CSS files have hashed names that change with each version. With 7.7 and 8.0 instances behind one load balancer, a browser can load `index.html` from one version and ask the other for its assets, which that instance does not have. Use sticky sessions for the dashboard during the roll, or expect to reload the page once the roll is done.
