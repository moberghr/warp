using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;

namespace Warp.Provider.PostgreSql;

/// <summary>
/// PostgreSQL-specific SQL queries that use <c>FOR UPDATE [SKIP LOCKED]</c> directly in the
/// statement (not via a regex-rewriting interceptor). SQL templates are built once from the
/// EF model and cached on this singleton — per-call cost is just parameter binding.
/// </summary>
public sealed class PostgresWarpSqlQueries<TContext> : IWarpSqlQueries<TContext>
    where TContext : DbContext
{
    // Prepended to the job claims (rule 6.21): within the claim's command, a plan that sorts is priced out.
    // set_config(..., true) is SET LOCAL without its WARNING: outside an explicit transaction block
    // PostgreSQL answers SET LOCAL with one ("can only be used in transaction blocks") while still applying
    // it, which put a line in the server log on every claim, empty polls included. In a DO block because
    // the claim's command must not return a result set before the claim's own: EF reads the first one.
    private const string ClaimPlanGuard = "DO $$BEGIN PERFORM set_config('enable_sort', 'off', true); END$$;\n";

    private readonly WarpJobTableNames _n;

    private readonly string _claimEnqueuedJobsSql;
    private readonly string _claimEnqueuedJobsFromSql;
    private readonly string _claimEnqueuedMessagesSql;
    private readonly string _completeJobsSql;
    private readonly string _renewRunningJobsSql;
    private readonly string _lockStaleProcessingJobsSql;
    private readonly string _lockJobByIdWaitSql;
    private readonly string _lockJobsByIdWaitSql;
    private readonly string _lockAllServersSql;
    private readonly string _heartbeatSql;
    private readonly string _heartbeatLocalTimeSql;
    private readonly string _activateScheduledJobsSql;
    private readonly string _lockLeaseByServiceNameSql;
    private readonly string _lockDefinitionByServiceNameSql;

    // Built lazily from the resolved model on first use: WarpJobTableNames does not carry the
    // RateLimitBucket names (adapter rate limiting is an opt-in, cold path), so the SQL is derived
    // from context.Model once and cached. A benign double-compute under a race is harmless.
    private string? _lockRateLimitBucketSql;

    // The cluster-wide task statements (rule 6.23), built from the model on first use.
    private PostgresClusterTaskSql? _clusterTaskSql;

    public PostgresWarpSqlQueries(WarpJobTableNames names)
    {
        _n = names;

        var table = QualifiedTable();
        var serverTable = QualifiedServerTable();

        // Atomic claim: subquery with FOR UPDATE SKIP LOCKED picks up to N eligible rows
        // (State=Enqueued) in queue-priority/schedule order, outer UPDATE flips to Processing
        // and RETURNING pipes the full row back. Pause is enforced in C# via PauseStateHolder
        // before the worker calls into this query — pause has heartbeat-cadence latency, see
        // PauseStateHolder for details.
        //
        // The queue predicate is a SCALAR equality, and one statement is issued per queue
        // (see ClaimEnqueuedJobsAsync). It used to be `queue = ANY(@queues)`, which is the same
        // result but collapses catastrophically the moment the planner's row estimate goes stale:
        //
        //   stale stats, 54,000 claimable rows in a 300k table, PostgreSQL 18
        //     queue = ANY(array)   Sort (external merge, 2.6 MB to disk) over the WRONG index
        //                          52,088 buffers,  62.1 ms
        //     queue = scalar       Index Cond on (Kind, CurrentState, Queue, ScheduleTime)
        //                             43 buffers,   0.101 ms      -- 615x faster, 1,211x fewer buffers
        //
        // Both spellings estimate `rows=1` there; the scalar one does not need the estimate to be
        // right. With `queue` fixed, this index supplies ORDER BY queue, schedule_time for free, so
        // there is no Sort and LIMIT stops at the first rows. A multi-value predicate cannot supply
        // that ordering as cheaply, so at `rows=1` the planner prefers a scan of
        // (Kind, CurrentState, CreateTime) plus a Sort — and a Sort must consume its ENTIRE input
        // before emitting a row, which is why LIMIT buys nothing and the claim reads the whole
        // enqueued range to return one job. It is a knife-edge: 8.44 vs 8.46 estimated cost.
        //
        // Not parameter opacity — a literal `ANY(ARRAY[...])` (69.2 ms) and `IN (...)` (72.4 ms)
        // are just as bad. Fresh statistics also fix it, which is why `ANALYZE` looks like a cure,
        // but that is a race against a backlog that swings from empty to full between autovacuum
        // ticks; the scalar form removes the race instead of running it. See docs/perf-results.md.
        //
        // The limited SELECT is an ARRAY sub-select — an InitPlan, evaluated exactly ONCE — not a
        // subquery joined in FROM. Joined in FROM, the planner was free to put the full table on the
        // outer side of a nested loop and re-run the subquery per outer row, and it did so whenever it
        // believed the table empty: reltuples = 0 with pages still allocated, which is what autovacuum
        // leaves after vacuuming a queue that drained, until the next analyze. Every re-run skips the
        // row this statement has just updated and returns the next one, so one LIMIT 1 claimed every
        // row whose id also sorted later: 2-7 at random UUIDs, all of them at ascending ids. The
        // worker runs only the first row returned, and the surplus sat in Processing until
        // StaleJobRecovery. Captured in CI at 650 multi-row claims, all under that statistics state,
        // and replayed by ClaimPlanStabilityTests. A MATERIALIZED CTE also runs once, but it still
        // walks the whole table to join against it. This form is a primary-key lookup whatever the
        // statistics say.
        //
        // Both claims start by turning enable_sort off for the transaction (rule 6.21), which lasts for this command
        // only: its statements run as one implicit transaction. After a burst into a queue the last
        // analyze saw empty, the planner expects the claim to match one row and reads the dashboard's
        // (kind, current_state, create_time) index instead: every enqueued row of the kind, filtered
        // by queue and sorted, to return the limit (CI: 48,020 rows, 22.8 ms and ~22,000 buffers per
        // claim, against 0.72 ms and ~375), on every claim until autovacuum analyzes the table. With
        // sorting priced out, the only plan left is the claim index read in schedule order. Same rows,
        // same order: the setting changes the plan, never the result.
        _claimEnqueuedJobsSql = ClaimPlanGuard + $@"
            UPDATE {table} AS t
            SET ""{_n.CurrentState}"" = {(int)State.Processing},
                ""{_n.CurrentWorkerId}"" = {{0}},
                ""{_n.LastKeepAlive}"" = {{1}},
                ""{_n.RunId}"" = gen_random_uuid()
            WHERE t.""{_n.Id}"" = ANY(ARRAY(
                SELECT ""{_n.Id}""
                FROM {table}
                WHERE ""{_n.Kind}"" = {(int)JobKind.Job}
                  AND ""{_n.CurrentState}"" = {(int)State.Enqueued}
                  AND ""{_n.Queue}"" = {{2}}
                ORDER BY ""{_n.ScheduleTime}""
                LIMIT {{3}}
                FOR UPDATE SKIP LOCKED))
            RETURNING t.*";

        // The same claim starting at a watermark (rule 6.16): the index range scan begins at that
        // schedule time, past the dead entries earlier claims left at the head of the queue.
        _claimEnqueuedJobsFromSql = ClaimPlanGuard + $@"
            UPDATE {table} AS t
            SET ""{_n.CurrentState}"" = {(int)State.Processing},
                ""{_n.CurrentWorkerId}"" = {{0}},
                ""{_n.LastKeepAlive}"" = {{1}},
                ""{_n.RunId}"" = gen_random_uuid()
            WHERE t.""{_n.Id}"" = ANY(ARRAY(
                SELECT ""{_n.Id}""
                FROM {table}
                WHERE ""{_n.Kind}"" = {(int)JobKind.Job}
                  AND ""{_n.CurrentState}"" = {(int)State.Enqueued}
                  AND ""{_n.Queue}"" = {{2}}
                  AND ""{_n.ScheduleTime}"" >= {{4}}
                ORDER BY ""{_n.ScheduleTime}""
                LIMIT {{3}}
                FOR UPDATE SKIP LOCKED))
            RETURNING t.*";

        // Atomic batch-claim for the message router. Mirrors _claimEnqueuedJobsSql shape: an
        // inner SELECT-FOR-UPDATE-SKIP-LOCKED picks up to N eligible message rows in queue /
        // schedule order, the outer UPDATE flips them to Processing in one round-trip, and
        // RETURNING streams the post-update rows back as tracked entities. Replaces the older
        // one-at-a-time LockNextEnqueuedMessage pattern — that path required per-message commit
        // (otherwise the next select would re-fetch the same uncommitted row), so it could not
        // scale beyond ~commit-RTT messages per second.
        // The limited SELECT is an ARRAY sub-select for the same reason as the job claim above: joined
        // in FROM it can be re-run per outer row and claim past its limit.
        _claimEnqueuedMessagesSql = $@"
            UPDATE {table} AS t
            SET ""{_n.CurrentState}"" = {(int)State.Processing}
            WHERE t.""{_n.Id}"" = ANY(ARRAY(
                SELECT ""{_n.Id}""
                FROM {table}
                WHERE ""{_n.Kind}"" = {(int)JobKind.Message}
                  AND ""{_n.CurrentState}"" = {(int)State.Enqueued}
                ORDER BY ""{_n.Queue}"", ""{_n.ScheduleTime}""
                LIMIT {{0}}
                FOR NO KEY UPDATE SKIP LOCKED))
            RETURNING t.*";

        // The batched completion (rule 6.13): every finished job of a flush and its log rows in ONE
        // statement. The arrays are unnested back into rows; the log rows are inserted only for jobs
        // the update found, so a job whose row was deleted while it ran takes its log rows with it
        // and cannot fail the insert on the foreign key. A row is updated only while it is still
        // Processing and still this run's (JobRunOwner): the run's own run id (#347), and renewed by its
        // worker or as its claim left it. The run id is cleared on the way out of Processing.
        // A run whose job StaleJobRecovery took back and another claim took is skipped the same way,
        // instead of finishing the new run's row.
        var log = _n.JobLog ?? throw new InvalidOperationException("The JobLog table names were not resolved from the model.");
        var logTable = string.IsNullOrEmpty(log.Schema) ? $"\"{log.Table}\"" : $"\"{log.Schema}\".\"{log.Table}\"";
        _completeJobsSql = $@"
            WITH done AS (
                UPDATE {table} AS t
                SET ""{_n.CurrentState}"" = c.state,
                    ""{_n.CancellationMode}"" = c.cancel,
                    ""{_n.CurrentWorkerId}"" = c.worker,
                    ""{_n.LastKeepAlive}"" = c.keepalive,
                    ""{_n.ExpireAt}"" = c.expire,
                    ""{_n.HandlerType}"" = c.handler,
                    ""{_n.ScheduleTime}"" = c.schedule,
                    ""{_n.Metadata}"" = c.metadata,
                    ""{_n.RunId}"" = NULL
                FROM unnest(
                        @ids, @states, @cancels, @workers, @keepalives, @expires, @handlers, @schedules, @metadatas,
                        @owners, @claimers, @claimed_keepalives, @runs)
                    AS c(id, state, cancel, worker, keepalive, expire, handler, schedule, metadata, owner, claimer, claimed_keepalive, run)
                WHERE t.""{_n.Id}"" = c.id
                  AND t.""{_n.CurrentState}"" = {(int)State.Processing}
                  AND t.""{_n.RunId}"" IS NOT DISTINCT FROM c.run
                  AND (t.""{_n.CurrentWorkerId}"" = c.owner
                       OR (t.""{_n.CurrentWorkerId}"" = c.claimer AND t.""{_n.LastKeepAlive}"" = c.claimed_keepalive))
                RETURNING t.""{_n.Id}"" AS id
            ),
            logged AS (
            INSERT INTO {logTable} (
                ""{log.Id}"", ""{log.JobId}"", ""{log.EventType}"", ""{log.Timestamp}"", ""{log.Level}"", ""{log.Message}"",
                ""{log.Exception}"", ""{log.DurationMs}"", ""{log.WorkerId}"", ""{log.Name}"", ""{log.Value}"")
            SELECT l.id, l.job_id, l.event_type, l.at, l.level, l.message, l.exception, l.duration_ms, l.worker_id, l.name, l.value
            FROM unnest(
                    @log_ids, @log_jobs, @log_events, @log_times, @log_levels, @log_messages,
                    @log_exceptions, @log_durations, @log_workers, @log_names, @log_values)
                AS l(id, job_id, event_type, at, level, message, exception, duration_ms, worker_id, name, value)
            WHERE l.job_id IN (SELECT id FROM done)
            )
            SELECT id FROM done";

        // One keep-alive for every job a worker group is running (rule 6.14). Only rows still Processing
        // are touched, so a renewal that races a job's completion writes nothing to the finished row, and
        // only while still this run's, as the completion: renewing a row another claim now holds would
        // keep a dead run's job from recovery and stamp the wrong worker on the live one.
        _renewRunningJobsSql = $@"
            UPDATE {table} AS t
            SET ""{_n.LastKeepAlive}"" = @now,
                ""{_n.CurrentWorkerId}"" = beat.worker
            FROM unnest(@ids, @workers, @claimers, @claimed_keepalives, @runs) AS beat(id, worker, claimer, claimed_keepalive, run)
            WHERE t.""{_n.Id}"" = beat.id
              AND t.""{_n.CurrentState}"" = {(int)State.Processing}
              AND t.""{_n.RunId}"" IS NOT DISTINCT FROM beat.run
              AND (t.""{_n.CurrentWorkerId}"" = beat.worker
                   OR (t.""{_n.CurrentWorkerId}"" = beat.claimer AND t.""{_n.LastKeepAlive}"" = beat.claimed_keepalive))
            RETURNING t.""{_n.Id}"" AS ""Id"", t.""{_n.CancellationMode}"" AS ""Mode""";

        _lockStaleProcessingJobsSql = $@"
            SELECT * FROM {table}
            WHERE ""{_n.Kind}"" = {(int)JobKind.Job}
              AND ""{_n.CurrentState}"" = {(int)State.Processing}
              AND ""{_n.LastKeepAlive}"" IS NOT NULL
              AND ""{_n.LastKeepAlive}"" < {{0}}
            FOR NO KEY UPDATE SKIP LOCKED";

        _lockJobByIdWaitSql = $@"
            SELECT * FROM {table}
            WHERE ""{_n.Id}"" = {{0}}
            FOR NO KEY UPDATE";

        // Row locks for a set-based write that follows (BulkRequeueJobs), taken in id order.
        _lockJobsByIdWaitSql = $@"
            SELECT count(*) FROM (
                SELECT 1 FROM {table}
                WHERE ""{_n.Id}"" = ANY(@ids)
                ORDER BY ""{_n.Id}""
                FOR NO KEY UPDATE) AS locked";

        // FOR UPDATE (not NO KEY): ServerCleanup deletes a stale server's
        // BackgroundServiceInstance / BackgroundServiceLease children and then the server in one
        // transaction. FOR NO KEY UPDATE is compatible with the FOR KEY SHARE lock a child INSERT
        // takes on its parent, so a stale-but-alive server could insert a fresh instance row
        // referencing the server mid-cleanup and orphan the FK (23503). FOR UPDATE conflicts with
        // FOR KEY SHARE, so such inserts block until cleanup commits.
        _lockAllServersSql = $@"
            SELECT * FROM {serverTable}
            FOR UPDATE";

        // CTE+LEFT JOIN folds the heartbeat UPDATE, the server paused_at read, the
        // worker_group pause-state read, the BG-service instance heartbeat bump, and the
        // BG-service lease renewal into ONE round-trip. Two extra CTE members:
        //   • instance_bump — refreshes last_heartbeat_at on all instance rows for @me
        //   • lease_renewed — extends lease_expires_at on active leases held by @me and
        //                      returns the renewed service names so the Heartbeat task can
        //                      detect lost leases (held last beat but not in this set).
        // The TTL is baked in at construction time (from WarpJobTableNames.LeaseTtlSeconds) so
        // no extra runtime parameter is needed — the interface signature stays unchanged.
        // The BG tables are always present in the model (§2.13), so the extra CTEs run
        // unconditionally; deployments with no registered WarpBackgroundService simply have
        // empty Instance/Lease tables, and the UPDATEs are no-ops.
        var instanceTable = QualifiedBackgroundServiceInstanceTable();
        var leaseTable = QualifiedBackgroundServiceLeaseTable();
        var ttlSeconds = _n.LeaseTtlSeconds;

        // Every time written is the database's own (statement_timestamp(), one value for the whole
        // statement): a server whose clock is wrong cannot make itself or its leases look alive or dead to
        // the others (#350). {{1}} is the server's own clock, used only to store how far it is off — except
        // in the variant for a cluster with a server from before 8.0 alive (rule 2.20), which writes and
        // compares {{1}} as 7.7 did. REMOVE that variant in the release after 8.x.
        string HeartbeatSql(string written) => $@"
            WITH heartbeat AS (
                UPDATE {serverTable}
                SET ""{_n.ServerLastHeartbeatTime}"" = {written},
                    ""{_n.ServerClockOffsetMs}"" = EXTRACT(EPOCH FROM (statement_timestamp() - {{1}})) * 1000,
                    ""{_n.ServerMemoryWorkingSetBytes}"" = COALESCE({{2}}, ""{_n.ServerMemoryWorkingSetBytes}""),
                    ""{_n.ServerCpuUsagePercent}"" = COALESCE({{3}}, ""{_n.ServerCpuUsagePercent}"")
                WHERE ""{_n.ServerId}"" = {{0}}
                RETURNING ""{_n.ServerId}"" AS id, ""{_n.ServerPausedAt}"" AS paused_at
            ),
            instance_bump AS (
                UPDATE {instanceTable}
                SET ""{_n.BackgroundServiceInstanceLastHeartbeatAt}"" = {written}
                WHERE ""{_n.BackgroundServiceInstanceServerId}"" = {{0}}
            ),
            lease_renewed AS (
                UPDATE {leaseTable}
                SET ""{_n.BackgroundServiceLeaseExpiresAt}"" = {written} + interval '{ttlSeconds} seconds'
                WHERE ""{_n.BackgroundServiceLeaseHolderServerId}"" = {{0}}
                  AND ""{_n.BackgroundServiceLeaseExpiresAt}"" > {written}
                RETURNING ""{_n.BackgroundServiceLeaseServiceName}"" AS service_name
            )
            SELECT h.paused_at AS server_paused_at,
                   w.""{_n.WorkerGroupId}"" AS group_id,
                   w.""{_n.WorkerGroupPausedAt}"" AS group_paused_at,
                   (SELECT array_agg(service_name) FROM lease_renewed) AS renewed_leases,
                   statement_timestamp() AS database_now
            FROM heartbeat h
            LEFT JOIN {QualifiedWorkerGroupTable()} w ON w.""{_n.WorkerGroupServerId}"" = h.id";

        _heartbeatSql = HeartbeatSql("statement_timestamp()");
        _heartbeatLocalTimeSql = HeartbeatSql("{1}");

        // Atomic activation: UPDATE ... RETURNING queue flips due rows AND streams back the
        // queue names so the caller can fire one JobEnqueued notification per distinct queue.
        // Replaces the previous SELECT-DISTINCT-then-UPDATE pattern (2 round-trips).
        // RETURNING carries Id + Queue + ScheduleTime so the caller can write a per-row
        // "Enqueued" JobLog (with the previous ScheduleTime for operator context) atomically
        // with the state change. The ambient transaction from the xact-lock path
        // (LocksWithTransaction = true) makes the UPDATE + downstream JobLog inserts commit
        // together.
        _activateScheduledJobsSql = $@"
            UPDATE {table}
            SET ""{_n.CurrentState}"" = {(int)State.Enqueued}
            WHERE ""{_n.CurrentState}"" = {(int)State.Scheduled}
              AND ""{_n.ScheduleTime}"" <= {{0}}
            RETURNING ""{_n.Id}"", ""{_n.Queue}"", ""{_n.ScheduleTime}""";

        // BackgroundService row-lock queries. FOR NO KEY UPDATE so concurrent callers block
        // and serialise, eliminating the TOCTOU window between SELECT and the caller's
        // INSERT/UPDATE (§1.4).
        _lockLeaseByServiceNameSql = $@"
                SELECT * FROM {leaseTable}
                WHERE ""{_n.BackgroundServiceLeaseServiceName}"" = {{0}}
                FOR NO KEY UPDATE";

        var defTable = QualifiedBackgroundServiceDefinitionTable();
        _lockDefinitionByServiceNameSql = $@"
                SELECT * FROM {defTable}
                WHERE ""{_n.BackgroundServiceDefinitionName}"" = {{0}}
                FOR NO KEY UPDATE";
    }

    /// <summary>
    /// Claims up to <paramref name="limit"/> enqueued jobs, one statement per queue.
    /// <para>
    /// The single-queue case — the overwhelmingly common one — issues exactly one statement, as it
    /// always did. Multiple queues are visited in order and the budget is spent down, which
    /// reproduces the `ORDER BY Queue, ScheduleTime LIMIT n` the one-statement form used to give:
    /// that ordering drains the lexicographically-first queue completely before the next one is
    /// looked at, so taking as much as possible from each queue in turn yields the same rows.
    /// That starve-the-later-queue behaviour is PRESERVED deliberately, not fixed here — a group
    /// that lists several queues already relies on it, and changing it is a separate decision with
    /// its own blast radius.
    /// </para>
    /// <para>
    /// Ordering uses <see cref="StringComparer.Ordinal"/>, where the statement it replaces used the
    /// database's collation for the `queue` column. For the ASCII queue names Warp produces the two
    /// agree; a deployment using queue names that collate differently from ordinal would see the
    /// relative priority of two queues change.
    /// </para>
    /// <para>
    /// Each statement is its own implicit transaction, where one statement was atomic across all
    /// queues before. Nothing depends on cross-queue atomicity — every claimed row is stamped
    /// Processing by the statement that claimed it — but a failure partway through leaves the
    /// already-claimed rows Processing with no worker running them, which is the crash-mid-claim
    /// case `StaleJobRecovery` already exists to repair.
    /// </para>
    /// </summary>
    public async Task<List<Job>> ClaimEnqueuedJobsAsync(
        DbContext context,
        string[] queues,
        Guid workerId,
        DateTime now,
        int limit,
        ClaimWatermark? watermark,
        CancellationToken ct)
    {
        if (queues.Length == 1)
        {
            return await ClaimFromQueueAsync(context, queues[0], workerId, now, limit, watermark, ct);
        }

        var ordered = queues.OrderBy(x => x, StringComparer.Ordinal);
        var claimed = new List<Job>(limit);

        foreach (var queue in ordered)
        {
            var remaining = limit - claimed.Count;
            if (remaining <= 0)
            {
                break;
            }

            claimed.AddRange(await ClaimFromQueueAsync(context, queue, workerId, now, remaining, watermark, ct));
        }

        return claimed;
    }

    public async Task<List<Job>> ClaimEnqueuedMessagesAsync(DbContext context, int limit, CancellationToken ct)
    {
        return await context.Set<Job>()
            .FromSqlRaw(_claimEnqueuedMessagesSql, limit)
            .ToListAsync(ct);
    }

    public async Task<List<Job>> LockStaleProcessingJobsAsync(
        DbContext context,
        DateTime cutoff,
        CancellationToken ct)
    {
        return await context.Set<Job>()
            .FromSqlRaw(_lockStaleProcessingJobsSql, cutoff)
            .ToListAsync(ct);
    }

    public async Task<Job?> LockJobByIdWaitAsync(DbContext context, Guid jobId, CancellationToken ct)
    {
        return await context.Set<Job>()
            .FromSqlRaw(_lockJobByIdWaitSql, jobId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task LockJobsByIdWaitAsync(DbContext context, IReadOnlyList<Guid> jobIds, CancellationToken ct)
    {
        if (jobIds.Count == 0)
        {
            return;
        }

        await context.Database.ExecuteSqlRawAsync(
            _lockJobsByIdWaitSql,
            [new NpgsqlParameter<Guid[]>("ids", [.. jobIds])],
            ct);
    }

    public async Task<List<Server>> LockAllServersAsync(DbContext context, CancellationToken ct)
    {
        return await context.Set<Server>()
            .FromSqlRaw(_lockAllServersSql)
            .ToListAsync(ct);
    }

    public async Task<HeartbeatResult?> HeartbeatAsync(
        DbContext context,
        Guid serverId,
        DateTime now,
        long? memoryBytes,
        double? cpuPercent,
        bool writeLocalTime,
        CancellationToken ct)
    {
        // Raw ADO.NET: the CTE+JOIN returns a flat (server_paused_at, group_id, group_paused_at
        // [, renewed_leases]) shape that doesn't match any single EF entity. Issuing the command
        // directly avoids contorting EF Core into materializing this composite result.
        // When the BackgroundServices addon is registered, the SQL also updates instance
        // last_heartbeat_at and renews held leases in the same round-trip (TTL baked in at
        // construction time from WarpJobTableNames.LeaseTtlSeconds).
        var conn = context.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await conn.OpenAsync(ct);
        }

        try
        {
            await using var cmd = conn.CreateCommand();

            cmd.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = (writeLocalTime ? _heartbeatLocalTimeSql : _heartbeatSql)
                .Replace("{0}", "@p0", StringComparison.Ordinal)
                .Replace("{1}", "@p1", StringComparison.Ordinal)
                .Replace("{2}", "@p2", StringComparison.Ordinal)
                .Replace("{3}", "@p3", StringComparison.Ordinal);
            AddParameter(cmd, "@p0", serverId);
            AddParameter(cmd, "@p1", now);
            AddParameter(cmd, "@p2", (object?)memoryBytes ?? DBNull.Value);
            AddParameter(cmd, "@p3", (object?)cpuPercent ?? DBNull.Value);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            DateTime? serverPausedAt = null;
            var groupPaused = new Dictionary<Guid, bool>();
            var renewedLeases = new List<string>();
            DateTime? databaseNow = null;
            var anyRow = false;
            while (await reader.ReadAsync(ct))
            {
                anyRow = true;
                databaseNow ??= DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc);
                if (!await reader.IsDBNullAsync(0, ct))
                {
                    serverPausedAt = reader.GetDateTime(0);
                }

                // group_id is NULL for the LEFT JOIN row when the server has no worker groups.
                if (!await reader.IsDBNullAsync(1, ct))
                {
                    var groupId = reader.GetGuid(1);
                    var groupIsPaused = !await reader.IsDBNullAsync(2, ct);
                    groupPaused[groupId] = groupIsPaused;
                }

                // Column 3 (renewed_leases array) is NULL when no leases were renewed this beat.
                if (!await reader.IsDBNullAsync(3, ct))
                {
                    // Npgsql returns a PostgreSQL array column as string[] when the element type is text.
                    var raw = reader.GetValue(3);
                    if (raw is string[] names)
                    {
                        renewedLeases.AddRange(names);
                    }
                }
            }

            return anyRow ? new HeartbeatResult(serverPausedAt, groupPaused, renewedLeases) { DatabaseUtcNow = databaseNow } : null;
        }
        finally
        {
            if (openedHere)
            {
                await conn.CloseAsync();
            }
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    public async Task<List<(Guid Id, string Queue, DateTime ScheduleTime)>> ActivateScheduledJobsAsync(
        DbContext context,
        DateTime now,
        CancellationToken ct)
    {
        // Raw ADO.NET: EF Core's FromSqlRaw<Job> would try to materialize full Job entities,
        // but the SQL only RETURNS three columns. Issuing the command directly keeps the path
        // narrow and matches how the notification transport opens connections.
        var conn = context.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await conn.OpenAsync(ct);
        }

        try
        {
            await using var cmd = conn.CreateCommand();

            // Bind to ambient transaction if any — when called from the xact-lock path the
            // connection is in a pending local transaction. Npgsql tolerates a null
            // Transaction here, but SQL Server doesn't, and we use the same shape both
            // places for consistency.
            cmd.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = _activateScheduledJobsSql.Replace("{0}", "@p0", StringComparison.Ordinal);
            var param = cmd.CreateParameter();
            param.ParameterName = "@p0";
            param.Value = now;
            cmd.Parameters.Add(param);

            var activated = new List<(Guid Id, string Queue, DateTime ScheduleTime)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                activated.Add((reader.GetGuid(0), reader.GetString(1), reader.GetDateTime(2)));
            }

            return activated;
        }
        finally
        {
            if (openedHere)
            {
                await conn.CloseAsync();
            }
        }
    }

    public async Task<IReadOnlyCollection<Guid>> CompleteJobsAsync(DbContext context, IReadOnlyList<Job> jobs, IReadOnlyList<JobRunOwner> owners, IReadOnlyList<JobLog> logs, CancellationToken ct)
    {
        if (jobs.Count == 0)
        {
            return [];
        }

        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await conn.OpenAsync(ct);
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (NpgsqlTransaction?)context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = _completeJobsSql;

            // One array per column, one element per row: the statement unnests them back into rows.
            cmd.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", [.. jobs.Select(x => x.Id)]));
            cmd.Parameters.Add(new NpgsqlParameter<int[]>("states", [.. jobs.Select(x => (int)x.CurrentState)]));
            cmd.Parameters.Add(new NpgsqlParameter<int[]>("cancels", [.. jobs.Select(x => (int)x.CancellationMode)]));
            cmd.Parameters.Add(new NpgsqlParameter<Guid?[]>("workers", [.. jobs.Select(x => x.CurrentWorkerId)]));
            cmd.Parameters.Add(Timestamps("keepalives", jobs.Select(x => x.LastKeepAlive)));
            cmd.Parameters.Add(Timestamps("expires", jobs.Select(x => x.ExpireAt)));
            cmd.Parameters.Add(new NpgsqlParameter<string?[]>("handlers", [.. jobs.Select(x => x.HandlerType)]));
            cmd.Parameters.Add(Timestamps("schedules", jobs.Select(x => (DateTime?)x.ScheduleTime)));
            cmd.Parameters.Add(new NpgsqlParameter<string?[]>("metadatas", [.. jobs.Select(x => x.Metadata)]));
            cmd.Parameters.Add(new NpgsqlParameter<Guid[]>("owners", [.. owners.Select(x => x.WorkerId)]));
            cmd.Parameters.Add(new NpgsqlParameter<Guid?[]>("claimers", [.. owners.Select(x => x.ClaimedBy)]));
            cmd.Parameters.Add(Timestamps("claimed_keepalives", owners.Select(x => x.ClaimedKeepAlive)));
            cmd.Parameters.Add(new NpgsqlParameter<Guid?[]>("runs", [.. owners.Select(x => x.RunId)]));

            cmd.Parameters.Add(new NpgsqlParameter<Guid[]>("log_ids", [.. logs.Select(x => x.Id)]));
            cmd.Parameters.Add(new NpgsqlParameter<Guid[]>("log_jobs", [.. logs.Select(x => x.JobId)]));
            cmd.Parameters.Add(new NpgsqlParameter<string[]>("log_events", [.. logs.Select(x => x.EventType)]));
            cmd.Parameters.Add(Timestamps("log_times", logs.Select(x => (DateTime?)x.Timestamp)));
            cmd.Parameters.Add(new NpgsqlParameter<string[]>("log_levels", [.. logs.Select(x => x.Level)]));
            cmd.Parameters.Add(new NpgsqlParameter<string[]>("log_messages", [.. logs.Select(x => x.Message)]));
            cmd.Parameters.Add(new NpgsqlParameter<string?[]>("log_exceptions", [.. logs.Select(x => x.Exception)]));
            cmd.Parameters.Add(new NpgsqlParameter<double?[]>("log_durations", [.. logs.Select(x => x.DurationMs)]));
            cmd.Parameters.Add(new NpgsqlParameter<Guid?[]>("log_workers", [.. logs.Select(x => x.WorkerId)]));
            cmd.Parameters.Add(new NpgsqlParameter<string?[]>("log_names", [.. logs.Select(x => x.Name)]));
            cmd.Parameters.Add(new NpgsqlParameter<short?[]>("log_values", [.. logs.Select(x => x.Value)]));

            var written = new List<Guid>(jobs.Count);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                written.Add(reader.GetGuid(0));
            }

            return written;
        }
        finally
        {
            if (openedHere)
            {
                await conn.CloseAsync();
            }
        }
    }

    // Through EF's raw-SQL query, not a bare command: it runs on the context's connection and in its
    // transaction like any other statement, and EF's command interceptors see it, which is how a test
    // makes one renewal fail.
    public async Task<IReadOnlyCollection<Guid>> RenewRunningJobsAsync(
        DbContext context,
        IReadOnlyList<JobRunOwner> runs,
        DateTime now,
        CancellationToken ct)
    {
        if (runs.Count == 0)
        {
            return [];
        }

        var renewed = await context.Database
            .SqlQueryRaw<RenewedJob>(
                _renewRunningJobsSql,
                new NpgsqlParameter<Guid[]>("ids", [.. runs.Select(x => x.JobId)]),
                new NpgsqlParameter<Guid[]>("workers", [.. runs.Select(x => x.WorkerId)]),
                new NpgsqlParameter<Guid?[]>("claimers", [.. runs.Select(x => x.ClaimedBy)]),
                Timestamps("claimed_keepalives", runs.Select(x => x.ClaimedKeepAlive)),
                new NpgsqlParameter<Guid?[]>("runs", [.. runs.Select(x => x.RunId)]),
                new NpgsqlParameter<DateTime>("now", DateTime.SpecifyKind(now, DateTimeKind.Utc)))
            .ToListAsync(ct);

        return [.. renewed.Where(x => x.Mode != (int)CancellationMode.None).Select(x => x.Id)];
    }

    public async Task<ClusterTaskClaims> ClaimClusterTasksAsync(
        DbContext context,
        IReadOnlyList<string> names,
        Guid serverId,
        TimeSpan liveWindow,
        CancellationToken ct)
    {
        if (names.Count == 0)
        {
            return new ClusterTaskClaims([], LegacyServersAlive: false);
        }

        var tasks = new List<ClusterTaskState>(names.Count);
        var legacy = false;
        var others = false;
        await ReadAsync(
            context,
            ClusterTaskSql(context).Claim,
            [
                new NpgsqlParameter<string[]>("names", [.. names]),
                new NpgsqlParameter<Guid>("server", serverId),
                new NpgsqlParameter<double>("liveWindow", liveWindow.TotalSeconds),
            ],
            x =>
            {
                tasks.Add(new ClusterTaskState(x.GetString(0), x.GetBoolean(1), TimeSpan.FromSeconds(x.GetDouble(2))) { Scheduled = x.GetBoolean(5) });
                legacy |= x.GetBoolean(3);
                others |= x.GetBoolean(4);
            },
            ct);

        return new ClusterTaskClaims(tasks, legacy) { OtherServersAlive = others };
    }

    public async Task<bool> RequestClusterTaskRunAsync(DbContext context, string name, Guid serverId, CancellationToken ct)
    {
        var claimed = false;
        await ReadAsync(
            context,
            ClusterTaskSql(context).Request,
            [
                new NpgsqlParameter<string>("name", name),
                new NpgsqlParameter<Guid>("server", serverId),
            ],
            x => claimed = x.GetInt32(0) > 0,
            ct);

        return claimed;
    }

    public async Task<bool> RenewClusterTaskLeaseAsync(DbContext context, string name, Guid serverId, CancellationToken ct)
    {
        var renewed = false;
        await ReadAsync(
            context,
            ClusterTaskSql(context).Renew,
            [
                new NpgsqlParameter<string>("name", name),
                new NpgsqlParameter<Guid>("server", serverId),
            ],
            _ => renewed = true,
            ct);

        return renewed;
    }

    public async Task<bool> CompleteClusterTaskAsync(DbContext context, ClusterTaskCompletion completion, CancellationToken ct)
    {
        var dueNow = false;
        await ReadAsync(
            context,
            ClusterTaskSql(context).Complete,
            [
                new NpgsqlParameter<string>("name", completion.Name),
                new NpgsqlParameter<Guid>("server", completion.ServerId),
                new NpgsqlParameter<bool>("didWork", completion.DidWork),
                new NpgsqlParameter<bool>("rerun", completion.Rerun),
                new NpgsqlParameter<bool>("record", completion.Record),
                new NpgsqlParameter("status", (object?)completion.Status ?? DBNull.Value) { DataTypeName = "text" },
                new NpgsqlParameter("message", (object?)completion.Message ?? DBNull.Value) { DataTypeName = "text" },
                new NpgsqlParameter<double>("durationMs", completion.DurationMs),
            ],
            x => dueNow = x.GetBoolean(0),
            ct);

        return dueNow;
    }

    public async Task RegisterClusterTasksAsync(DbContext context, IReadOnlyList<ClusterTaskRegistration> tasks, CancellationToken ct)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        await ReadAsync(
            context,
            ClusterTaskSql(context).Register,
            [
                new NpgsqlParameter<string[]>("names", [.. tasks.Select(x => x.Name)]),
                new NpgsqlParameter<double[]>("intervals", [.. tasks.Select(x => x.IntervalSeconds)]),
            ],
            _ => { },
            ct);
    }

    // Built once from the resolved model on first use, like the rate-limit lock statement.
    private PostgresClusterTaskSql ClusterTaskSql(DbContext context) => _clusterTaskSql ??= PostgresClusterTaskSql.Build(context.Model);

    // Reads a cluster task statement's rows by position on the context's connection and transaction.
    // Not SqlQueryRaw: that maps result columns through the context's naming convention, and the
    // server context (no convention) and an application context (any convention) would need different
    // aliases for the same statement.
    private static async Task ReadAsync(
        DbContext context,
        string sql,
        NpgsqlParameter[] parameters,
        Action<System.Data.Common.DbDataReader> row,
        CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            command.Parameters.AddRange(parameters);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                row(reader);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    // One row of the keep-alive statement's result.
    internal sealed class RenewedJob
    {
        public Guid Id { get; set; }

        public int Mode { get; set; }
    }

    // Warp's DateTime columns are timestamptz holding UTC (rule 5.12). The kind is stated outright: the
    // values come from the clock or back out of those columns, and Npgsql rejects an unspecified kind.
    private static NpgsqlParameter<DateTime?[]> Timestamps(string name, IEnumerable<DateTime?> values) =>
        new(name, [.. values.Select(x => x is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : (DateTime?)null)])
        {
            DataTypeName = "timestamp with time zone[]",
        };

    public async Task<(bool LockHeld, T? Result)> RunUnderTransactionLockAsync<T>(
        DbContext context,
        string lockKey,
        Func<DbContext, CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        var key = AdvisoryLockKey.Compute(lockKey);

        await using var tx = await context.Database.BeginTransactionAsync(ct);

        // pg_try_advisory_xact_lock is bound to the current transaction — auto-released on
        // COMMIT/ROLLBACK so we never need a separate pg_advisory_unlock round-trip.
        var conn = context.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        cmd.CommandText = "SELECT pg_try_advisory_xact_lock(@p0)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@p0";
        p.Value = key;
        cmd.Parameters.Add(p);
        var raw = await cmd.ExecuteScalarAsync(ct);
        var held = raw is bool b && b;

        if (!held)
        {
            await tx.RollbackAsync(ct);

            return (false, default);
        }

        try
        {
            var result = await work(context, ct);
            await tx.CommitAsync(ct);

            return (true, result);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private string QualifiedTable()
    {
        return string.IsNullOrEmpty(_n.Schema)
            ? $"\"{_n.Table}\""
            : $"\"{_n.Schema}\".\"{_n.Table}\"";
    }

    private string QualifiedServerTable()
    {
        return string.IsNullOrEmpty(_n.ServerSchema)
            ? $"\"{_n.ServerTable}\""
            : $"\"{_n.ServerSchema}\".\"{_n.ServerTable}\"";
    }

    private string QualifiedWorkerGroupTable()
    {
        return string.IsNullOrEmpty(_n.WorkerGroupSchema)
            ? $"\"{_n.WorkerGroupTable}\""
            : $"\"{_n.WorkerGroupSchema}\".\"{_n.WorkerGroupTable}\"";
    }

    private string QualifiedBackgroundServiceInstanceTable()
    {
        return string.IsNullOrEmpty(_n.BackgroundServiceSchema)
            ? $"\"{_n.BackgroundServiceInstanceTable}\""
            : $"\"{_n.BackgroundServiceSchema}\".\"{_n.BackgroundServiceInstanceTable}\"";
    }

    private string QualifiedBackgroundServiceLeaseTable()
    {
        return string.IsNullOrEmpty(_n.BackgroundServiceSchema)
            ? $"\"{_n.BackgroundServiceLeaseTable}\""
            : $"\"{_n.BackgroundServiceSchema}\".\"{_n.BackgroundServiceLeaseTable}\"";
    }

    private string QualifiedBackgroundServiceDefinitionTable()
    {
        return string.IsNullOrEmpty(_n.BackgroundServiceSchema)
            ? $"\"{_n.BackgroundServiceDefinitionTable}\""
            : $"\"{_n.BackgroundServiceSchema}\".\"{_n.BackgroundServiceDefinitionTable}\"";
    }

    public async Task<BackgroundServiceLease?> LockLeaseByServiceNameAsync(
        DbContext context,
        string serviceName,
        CancellationToken ct)
    {
        return await context.Set<BackgroundServiceLease>()
            .FromSqlRaw(_lockLeaseByServiceNameSql, serviceName)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<BackgroundServiceDefinition?> LockDefinitionByServiceNameAsync(
        DbContext context,
        string serviceName,
        CancellationToken ct)
    {
        return await context.Set<BackgroundServiceDefinition>()
            .FromSqlRaw(_lockDefinitionByServiceNameSql, serviceName)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<RateLimitBucket?> LockRateLimitBucketByKeyAsync(DbContext context, string key, CancellationToken ct)
    {
        _lockRateLimitBucketSql ??= BuildLockRateLimitBucketSql(context.Model);

        return await context.Set<RateLimitBucket>()
            .FromSqlRaw(_lockRateLimitBucketSql, key)
            .FirstOrDefaultAsync(ct);
    }

    private static string BuildLockRateLimitBucketSql(Microsoft.EntityFrameworkCore.Metadata.IModel model)
    {
        var entity = model.FindEntityType(typeof(RateLimitBucket))
            ?? throw new InvalidOperationException("RateLimitBucket entity not registered in the DbContext model.");

        var schema = entity.GetSchema();
        var table = entity.GetTableName()
            ?? throw new InvalidOperationException("RateLimitBucket entity has no resolved table name.");
        var nameColumn = entity.FindProperty(nameof(RateLimitBucket.Name))?.GetColumnName()
            ?? throw new InvalidOperationException("RateLimitBucket.Name has no resolved column name.");

        var qualified = string.IsNullOrEmpty(schema)
            ? $"\"{table}\""
            : $"\"{schema}\".\"{table}\"";

        return $"SELECT * FROM {qualified} WHERE \"{nameColumn}\" = {{0}} FOR NO KEY UPDATE";
    }

    private async Task<List<Job>> ClaimFromQueueAsync(
        DbContext context,
        string queue,
        Guid workerId,
        DateTime now,
        int limit,
        ClaimWatermark? watermark,
        CancellationToken ct)
    {
        var from = watermark?.StartFor(queue);
        var claimed = from is { } start
            ? await context.Set<Job>()
                .FromSqlRaw(_claimEnqueuedJobsFromSql, workerId, now, queue, limit, start)
                .ToListAsync(ct)
            : await context.Set<Job>()
                .FromSqlRaw(_claimEnqueuedJobsSql, workerId, now, queue, limit)
                .ToListAsync(ct);
        watermark?.Record(queue, from, limit, claimed);

        // The inner SELECT picks the OLDEST rows, but UPDATE ... RETURNING gives them back in no
        // particular order (on PostgreSQL, by id). The dispatcher hands a claimed batch to its
        // workers in list order, so without this a newer job of the batch could start before an older
        // one. Sorted here, in memory, on the rows already in hand: no statement, and nothing at all
        // for a single-row claim, which is every claim in single-worker mode.
        if (claimed.Count > 1)
        {
            claimed.Sort(static (x, y) => x.ScheduleTime.CompareTo(y.ScheduleTime));
        }

        return claimed;
    }
}
