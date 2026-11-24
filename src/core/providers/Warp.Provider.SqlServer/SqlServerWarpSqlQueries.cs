using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;

namespace Warp.Provider.SqlServer;

/// <summary>
/// SQL Server-specific SQL queries using <c>WITH (ROWLOCK, UPDLOCK, READPAST)</c> directly in
/// the FROM clause (not via a regex-rewriting interceptor). SQL templates are built once from
/// the EF model and cached on this singleton.
/// <para>
/// SQL Server doesn't support array parameters, so multi-valued filters (queue list, dead
/// server ids) are expanded into comma-separated strings and split via <c>STRING_SPLIT</c>
/// inside the query. Per-call cost is still just parameter binding + a small constant for
/// <c>string.Join</c>.
/// </para>
/// </summary>
public sealed class SqlServerWarpSqlQueries<TContext> : IWarpSqlQueries<TContext>
    where TContext : DbContext
{
    private readonly WarpJobTableNames _n;

    private readonly string _claimEnqueuedJobsSql;
    private readonly string _claimEnqueuedJobsFromSql;
    private readonly string _claimEnqueuedMessagesSql;
    private readonly string _completeJobsSql;
    private readonly string _renewRunningJobsSql;
    private readonly string _lockStaleProcessingJobsSql;
    private readonly string _lockJobByIdWaitSql;
    private readonly string _lockAllServersSql;
    private readonly string _heartbeatSql;
    private readonly string _activateScheduledJobsSql;
    private readonly string _lockLeaseByServiceNameSql;
    private readonly string _lockDefinitionByServiceNameSql;

    // Built lazily from the resolved model on first use: WarpJobTableNames does not carry the
    // RateLimitBucket names (adapter rate limiting is an opt-in, cold path), so the SQL is derived
    // from context.Model once and cached. A benign double-compute under a race is harmless.
    private string? _lockRateLimitBucketSql;

    public SqlServerWarpSqlQueries(WarpJobTableNames names)
    {
        _n = names;

        var table = QualifiedTable();
        var serverTable = QualifiedServerTable();

        // Atomic claim via UPDATE with OUTPUT. The candidates CTE picks up to N rows in
        // schedule order with ROWLOCK+UPDLOCK+READPAST so concurrent workers skip each
        // other's locked rows. The UPDATE mutates the same rows and OUTPUT INSERTED.* streams
        // them back — caller gets tracked Job entities and sees post-update state. Pause is
        // enforced in C# via PauseStateHolder before the worker calls into this query — pause
        // has heartbeat-cadence latency, see PauseStateHolder for details.
        //
        // The queue predicate is a SCALAR equality and one statement is issued per queue, in
        // lockstep with the PostgreSQL provider — see the long note on
        // PostgresWarpSqlQueries._claimEnqueuedJobsSql for the measurement that motivated it
        // (a multi-value queue predicate cannot supply ORDER BY from the claim index, so a stale
        // row estimate sends the planner to a Sort that must drain the whole enqueued range:
        // 62.1 ms and 52,088 buffers against 0.101 ms and 43 for the scalar form on PostgreSQL).
        // The mechanism is a cost-model one, not a PostgreSQL quirk, and SQL Server picks plans
        // from estimates the same way; the shapes are kept identical so the two providers cannot
        // drift. The SQL Server numbers themselves are NOT measured — see docs/perf-results.md.
        _claimEnqueuedJobsSql = $@"
            WITH candidates AS (
                SELECT TOP ({{3}}) *
                FROM {table} WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE [{_n.Kind}] = {(int)JobKind.Job}
                  AND [{_n.CurrentState}] = {(int)State.Enqueued}
                  AND [{_n.Queue}] = {{2}}
                ORDER BY [{_n.ScheduleTime}]
            )
            UPDATE candidates
            SET [{_n.CurrentState}] = {(int)State.Processing},
                [{_n.CurrentWorkerId}] = {{0}},
                [{_n.LastKeepAlive}] = {{1}}
            OUTPUT INSERTED.*";

        // The same claim starting at a watermark (rule 6.16): the index range scan begins at that
        // schedule time, past the dead entries earlier claims left at the head of the queue.
        _claimEnqueuedJobsFromSql = $@"
            WITH candidates AS (
                SELECT TOP ({{3}}) *
                FROM {table} WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE [{_n.Kind}] = {(int)JobKind.Job}
                  AND [{_n.CurrentState}] = {(int)State.Enqueued}
                  AND [{_n.Queue}] = {{2}}
                  AND [{_n.ScheduleTime}] >= {{4}}
                ORDER BY [{_n.ScheduleTime}]
            )
            UPDATE candidates
            SET [{_n.CurrentState}] = {(int)State.Processing},
                [{_n.CurrentWorkerId}] = {{0}},
                [{_n.LastKeepAlive}] = {{1}}
            OUTPUT INSERTED.*";

        // Atomic batch-claim for the message router. Mirrors _claimEnqueuedJobsSql shape: a CTE
        // picks up to N eligible message rows under ROWLOCK+UPDLOCK+READPAST, the UPDATE flips
        // them to Processing in one round-trip and OUTPUT INSERTED.* streams the post-update
        // rows back as tracked entities. Replaces the older one-at-a-time LockNextEnqueuedMessage
        // — that path required per-message commit (otherwise the next select would re-fetch the
        // same uncommitted row), so it could not scale beyond ~commit-RTT messages per second.
        _claimEnqueuedMessagesSql = $@"
            WITH candidates AS (
                SELECT TOP ({{0}}) *
                FROM {table} WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE [{_n.Kind}] = {(int)JobKind.Message}
                  AND [{_n.CurrentState}] = {(int)State.Enqueued}
                ORDER BY [{_n.Queue}], [{_n.ScheduleTime}]
            )
            UPDATE candidates
            SET [{_n.CurrentState}] = {(int)State.Processing}
            OUTPUT INSERTED.*";

        // The batched completion (rule 6.13): every finished job of a flush and its log rows in one
        // batch of two set-based statements, fed by two JSON documents (SQL Server has no array
        // parameters). The log rows are inserted only for jobs the update found, so a job whose row was
        // deleted while it ran takes its log rows with it and cannot fail the insert on the foreign key.
        // OPENJSON needs compatibility level 130, which SQL Server 2016 and Azure SQL have by default.
        //
        // LOOP JOIN with the JSON rows on the outer side, and ROWLOCK, are load-bearing: they make the
        // update one primary-key seek per job. Left to choose, SQL Server scanned the job table to join
        // it, taking locks on rows that belong to other workers' claims and flushes, and concurrent
        // flushes deadlocked (the batched-completion tests timed out on it).
        var log = _n.JobLog ?? throw new InvalidOperationException("The JobLog table names were not resolved from the model.");
        var logTable = string.IsNullOrEmpty(log.Schema) ? $"[{log.Table}]" : $"[{log.Schema}].[{log.Table}]";
        _completeJobsSql = $@"
            DECLARE @done TABLE (id uniqueidentifier NOT NULL PRIMARY KEY);

            UPDATE t
            SET [{_n.CurrentState}] = c.s,
                [{_n.CancellationMode}] = c.c,
                [{_n.CurrentWorkerId}] = c.w,
                [{_n.LastKeepAlive}] = c.k,
                [{_n.ExpireAt}] = c.e,
                [{_n.HandlerType}] = c.h,
                [{_n.ScheduleTime}] = c.d,
                [{_n.Metadata}] = c.m
            OUTPUT INSERTED.[{_n.Id}] INTO @done
            FROM OPENJSON(@jobs) WITH (
                i uniqueidentifier, s int, c int, w uniqueidentifier, k datetime2, e datetime2,
                h nvarchar(max), d datetime2, m nvarchar(max)) AS c
            INNER LOOP JOIN {table} AS t WITH (ROWLOCK) ON t.[{_n.Id}] = c.i;

            INSERT INTO {logTable} (
                [{log.Id}], [{log.JobId}], [{log.EventType}], [{log.Timestamp}], [{log.Level}], [{log.Message}],
                [{log.Exception}], [{log.DurationMs}], [{log.WorkerId}], [{log.Name}], [{log.Value}])
            SELECT l.i, l.j, l.e, l.t, l.l, l.m, l.x, l.d, l.w, l.n, l.v
            FROM OPENJSON(@logs) WITH (
                i uniqueidentifier, j uniqueidentifier, e nvarchar(max), t datetime2, l nvarchar(max), m nvarchar(max),
                x nvarchar(max), d float, w uniqueidentifier, n nvarchar(100), v smallint) AS l
            WHERE l.j IN (SELECT id FROM @done)
            OPTION (RECOMPILE);

            SELECT id FROM @done;";

        // One keep-alive for every job a worker group is running (rule 6.14). Only rows still Processing
        // are touched, so a renewal that races a job's completion writes nothing to the finished row.
        // LOOP JOIN with the JSON rows outermost and ROWLOCK for the same reason as the completion: one
        // primary-key seek per job, never a scan that locks other workers' rows.
        _renewRunningJobsSql = $@"
            UPDATE t
            SET [{_n.LastKeepAlive}] = @now,
                [{_n.CurrentWorkerId}] = beat.w
            OUTPUT INSERTED.[{_n.Id}] AS [Id], INSERTED.[{_n.CancellationMode}] AS [Mode]
            FROM OPENJSON(@beats) WITH (i uniqueidentifier, w uniqueidentifier) AS beat
            INNER LOOP JOIN {table} AS t WITH (ROWLOCK) ON t.[{_n.Id}] = beat.i
            WHERE t.[{_n.CurrentState}] = {(int)State.Processing};";

        _lockStaleProcessingJobsSql = $@"
            SELECT *
            FROM {table} WITH (ROWLOCK, UPDLOCK, READPAST)
            WHERE [{_n.Kind}] = {(int)JobKind.Job}
              AND [{_n.CurrentState}] = {(int)State.Processing}
              AND [{_n.LastKeepAlive}] IS NOT NULL
              AND [{_n.LastKeepAlive}] < {{0}}";

        _lockJobByIdWaitSql = $@"
            SELECT *
            FROM {table} WITH (ROWLOCK, UPDLOCK)
            WHERE [{_n.Id}] = {{0}}";

        // XLOCK (not UPDLOCK): ServerCleanup deletes a stale server's BackgroundServiceInstance /
        // BackgroundServiceLease children and then the server in one transaction. UPDLOCK is
        // compatible with the shared lock a child INSERT's FK check takes on the parent row, so a
        // stale-but-alive server could insert a fresh instance row referencing the server
        // mid-cleanup and orphan the FK (error 547). An exclusive XLOCK conflicts with that
        // FK-check lock, so such inserts block until cleanup commits.
        _lockAllServersSql = $@"
            SELECT *
            FROM {serverTable} WITH (ROWLOCK, XLOCK)";

        // Table variable + chained SELECT folds the heartbeat UPDATE, the server paused_at
        // read, the worker_group pause-state read, the BG-service instance heartbeat bump,
        // and the BG-service lease renewal into ONE round-trip. The UPDATE OUTPUTs the
        // post-update server row into @h; the chained SELECT joins worker_group for this
        // server. Two extra statements:
        //   • UPDATE instance — refreshes last_heartbeat_at for all instances on this server
        //   • UPDATE lease / OUTPUT INTO @renewed — extends held leases and captures names
        // The TTL is baked in at construction from WarpJobTableNames.LeaseTtlSeconds. The BG
        // tables are always present in the model (§2.13), so the extra UPDATEs run
        // unconditionally; deployments with no registered WarpBackgroundService simply have
        // empty Instance/Lease tables, and the UPDATEs are no-ops.
        // Cardinality: 0 rows if the server doesn't exist; max(1, # groups) rows otherwise
        // (LEFT JOIN keeps the server row even with zero groups, group_id NULL).
        var groupTable = QualifiedWorkerGroupTable();
        var instanceTable = QualifiedBackgroundServiceInstanceTable();
        var leaseTable = QualifiedBackgroundServiceLeaseTable();
        var ttlSeconds = _n.LeaseTtlSeconds;

        _heartbeatSql = $@"
            DECLARE @h TABLE (server_id uniqueidentifier, paused_at datetime2);
            UPDATE {serverTable}
            SET [{_n.ServerLastHeartbeatTime}] = {{1}},
                [{_n.ServerMemoryWorkingSetBytes}] = ISNULL({{2}}, [{_n.ServerMemoryWorkingSetBytes}]),
                [{_n.ServerCpuUsagePercent}] = ISNULL({{3}}, [{_n.ServerCpuUsagePercent}])
            OUTPUT INSERTED.[{_n.ServerId}], INSERTED.[{_n.ServerPausedAt}] INTO @h
            WHERE [{_n.ServerId}] = {{0}};
            UPDATE {instanceTable}
            SET [{_n.BackgroundServiceInstanceLastHeartbeatAt}] = {{1}}
            WHERE [{_n.BackgroundServiceInstanceServerId}] = {{0}};
            DECLARE @renewed TABLE (service_name nvarchar(256));
            UPDATE {leaseTable}
            SET [{_n.BackgroundServiceLeaseExpiresAt}] = DATEADD(SECOND, {ttlSeconds}, {{1}})
            OUTPUT INSERTED.[{_n.BackgroundServiceLeaseServiceName}] INTO @renewed
            WHERE [{_n.BackgroundServiceLeaseHolderServerId}] = {{0}}
              AND [{_n.BackgroundServiceLeaseExpiresAt}] > {{1}};
            SELECT h.paused_at AS server_paused_at,
                   w.[{_n.WorkerGroupId}] AS group_id,
                   w.[{_n.WorkerGroupPausedAt}] AS group_paused_at
            FROM @h h
            LEFT JOIN {groupTable} w ON w.[{_n.WorkerGroupServerId}] = h.server_id;
            SELECT service_name FROM @renewed;";

        // Atomic activation: UPDATE ... OUTPUT INSERTED.(id, queue, schedule_time) flips due rows
        // AND streams back per-row identity + queue + schedule_time. Caller fires one
        // JobEnqueued notification per distinct queue AND writes one "Enqueued" JobLog row per
        // id (atomic with this UPDATE via the ambient xact-lock transaction).
        _activateScheduledJobsSql = $@"
            UPDATE {table}
            SET [{_n.CurrentState}] = {(int)State.Enqueued}
            OUTPUT INSERTED.[{_n.Id}], INSERTED.[{_n.Queue}], INSERTED.[{_n.ScheduleTime}]
            WHERE [{_n.CurrentState}] = {(int)State.Scheduled}
              AND [{_n.ScheduleTime}] <= {{0}}";

        // BackgroundService row-lock queries. WITH (UPDLOCK, ROWLOCK) so concurrent callers
        // block and serialise, eliminating the TOCTOU window between SELECT and the caller's
        // INSERT/UPDATE (§1.4).
        _lockLeaseByServiceNameSql = $@"
                SELECT *
                FROM {leaseTable} WITH (UPDLOCK, ROWLOCK)
                WHERE [{_n.BackgroundServiceLeaseServiceName}] = {{0}}";

        var defTable = QualifiedBackgroundServiceDefinitionTable();
        _lockDefinitionByServiceNameSql = $@"
                SELECT *
                FROM {defTable} WITH (UPDLOCK, ROWLOCK)
                WHERE [{_n.BackgroundServiceDefinitionName}] = {{0}}";
    }

    /// <summary>
    /// Claims up to <paramref name="limit"/> enqueued jobs, one statement per queue. Mirrors
    /// <c>PostgresWarpSqlQueries.ClaimEnqueuedJobsAsync</c> exactly — see its remarks for why the
    /// queue predicate is scalar, and for the ordering, collation and per-statement-transaction
    /// consequences, all of which apply identically here.
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
        CancellationToken ct)
    {
        // Raw ADO.NET: multi-statement batch (DECLARE @h + UPDATE OUTPUT INTO + SELECT JOIN
        // [+ UPDATE instance + UPDATE lease OUTPUT INTO @renewed + SELECT @renewed]) produces a
        // composite result that doesn't match any single EF entity. When the BackgroundServices
        // addon is registered, the batch includes extra statements and a second result set for
        // the renewed lease names (TTL baked in at construction from WarpJobTableNames.LeaseTtlSeconds).
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
            cmd.CommandText = _heartbeatSql
                .Replace("{0}", "@p0", StringComparison.Ordinal)
                .Replace("{1}", "@p1", StringComparison.Ordinal)
                .Replace("{2}", "@p2", StringComparison.Ordinal)
                .Replace("{3}", "@p3", StringComparison.Ordinal);
            AddParameter(cmd, "@p0", serverId);
            AddParameter(cmd, "@p1", now);
            AddParameter(cmd, "@p2", (object?)memoryBytes ?? DBNull.Value);
            AddParameter(cmd, "@p3", (object?)cpuPercent ?? DBNull.Value);

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            // First result set: the server + worker-group pause state.
            DateTime? serverPausedAt = null;
            var groupPaused = new Dictionary<Guid, bool>();
            var anyRow = false;
            while (await reader.ReadAsync(ct))
            {
                anyRow = true;
                if (!await reader.IsDBNullAsync(0, ct))
                {
                    serverPausedAt = reader.GetDateTime(0);
                }

                if (!await reader.IsDBNullAsync(1, ct))
                {
                    var groupId = reader.GetGuid(1);
                    var groupIsPaused = !await reader.IsDBNullAsync(2, ct);
                    groupPaused[groupId] = groupIsPaused;
                }
            }

            // Second result set: renewed lease service names. Always emitted now that BG
            // tables are part of base Warp (§2.13).
            var renewedLeases = new List<string>();
            if (await reader.NextResultAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    if (!await reader.IsDBNullAsync(0, ct))
                    {
                        renewedLeases.Add(reader.GetString(0));
                    }
                }
            }

            return anyRow ? new HeartbeatResult(serverPausedAt, groupPaused, renewedLeases) : null;
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
        // but the SQL only OUTPUTs three columns. Issuing the command directly keeps the path
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

            // SQL Server requires explicit Transaction binding when the connection is in a
            // pending local transaction. Npgsql is lenient but we set it for both providers
            // for consistency.
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

    public async Task<(bool LockHeld, T? Result)> RunUnderTransactionLockAsync<T>(
        DbContext context,
        string lockKey,
        Func<DbContext, CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var tx = await context.Database.BeginTransactionAsync(ct);

        // sp_getapplock with LockOwner Transaction auto-releases on COMMIT/ROLLBACK; LockTimeout
        // 0 means try-then-fail rather than block (matches PG's try_advisory). Return value
        // semantics: 0 granted; 1 granted-after-wait; negative codes mean not acquired
        // (-1 timeout, -2 cancelled, -3 deadlock, -999 error).
        var conn = context.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        cmd.CommandText = @"
            DECLARE @rc int;
            EXEC @rc = sp_getapplock @Resource = @p0,
                                      @LockMode = N'Exclusive',
                                      @LockOwner = N'Transaction',
                                      @LockTimeout = 0;
            SELECT @rc;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@p0";
        p.Value = lockKey;
        cmd.Parameters.Add(p);
        var raw = await cmd.ExecuteScalarAsync(ct);
        var rc = raw is int i ? i : Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);

        if (rc < 0)
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

    public async Task<IReadOnlyCollection<Guid>> CompleteJobsAsync(DbContext context, IReadOnlyList<Job> jobs, IReadOnlyList<JobLog> logs, CancellationToken ct)
    {
        if (jobs.Count == 0)
        {
            return [];
        }

        var conn = context.Database.GetDbConnection();
        var openedHere = conn.State != ConnectionState.Open;
        if (openedHere)
        {
            await conn.OpenAsync(ct);
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = _completeJobsSql;
            AddJson(cmd, "@jobs", WriteJson(jobs, WriteJob));
            AddJson(cmd, "@logs", WriteJson(logs, WriteLog));

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
        IReadOnlyList<Guid> jobIds,
        IReadOnlyList<Guid> workerIds,
        DateTime now,
        CancellationToken ct)
    {
        if (jobIds.Count == 0)
        {
            return [];
        }

        var beats = jobIds.Select((x, i) => (Job: x, Worker: workerIds[i])).ToList();
        var json = WriteJson(beats, static (writer, beat) =>
        {
            writer.WriteString("i", beat.Job);
            writer.WriteString("w", beat.Worker);
        });

        var renewed = await context.Database
            .SqlQueryRaw<RenewedJob>(
                _renewRunningJobsSql,
                new SqlParameter("@beats", SqlDbType.NVarChar, -1) { Value = json },
                new SqlParameter("@now", SqlDbType.DateTime2) { Value = now })
            .ToListAsync(ct);

        return [.. renewed.Where(x => x.Mode != (int)CancellationMode.None).Select(x => x.Id)];
    }

    // One row of the keep-alive statement's result.
    internal sealed class RenewedJob
    {
        public Guid Id { get; set; }

        public int Mode { get; set; }
    }

    private static void AddJson(System.Data.Common.DbCommand cmd, string name, string json)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.String;
        parameter.Size = -1;
        parameter.Value = json;
        cmd.Parameters.Add(parameter);
    }

    // One short key per column: these documents are read once by the statement above and by nothing else.
    private static string WriteJson<T>(IReadOnlyList<T> rows, Action<Utf8JsonWriter, T> writeRow)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writeRow(writer, row);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void WriteJob(Utf8JsonWriter writer, Job job)
    {
        writer.WriteString("i", job.Id);
        writer.WriteNumber("s", (int)job.CurrentState);
        writer.WriteNumber("c", (int)job.CancellationMode);
        WriteNullable(writer, "w", job.CurrentWorkerId);
        WriteNullable(writer, "k", job.LastKeepAlive);
        WriteNullable(writer, "e", job.ExpireAt);
        writer.WriteString("h", job.HandlerType);
        WriteNullable(writer, "d", job.ScheduleTime);
        writer.WriteString("m", job.Metadata);
    }

    private static void WriteLog(Utf8JsonWriter writer, JobLog log)
    {
        writer.WriteString("i", log.Id);
        writer.WriteString("j", log.JobId);
        writer.WriteString("e", log.EventType);
        WriteNullable(writer, "t", log.Timestamp);
        writer.WriteString("l", log.Level);
        writer.WriteString("m", log.Message);
        writer.WriteString("x", log.Exception);
        if (log.DurationMs is { } duration)
        {
            writer.WriteNumber("d", duration);
        }

        WriteNullable(writer, "w", log.WorkerId);
        writer.WriteString("n", log.Name);
        if (log.Value is { } value)
        {
            writer.WriteNumber("v", value);
        }
    }

    // An absent key reads as NULL in OPENJSON ... WITH, so a null is simply not written.
    private static void WriteNullable(Utf8JsonWriter writer, string key, Guid? value)
    {
        if (value is { } id)
        {
            writer.WriteString(key, id);
        }
    }

    // Warp's DateTime columns hold UTC (rule 5.12). Written without an offset, so datetime2 takes the
    // value as it stands.
    private static void WriteNullable(Utf8JsonWriter writer, string key, DateTime? value)
    {
        if (value is { } instant)
        {
            writer.WriteString(key, instant.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private string QualifiedTable()
    {
        return string.IsNullOrEmpty(_n.Schema)
            ? $"[{_n.Table}]"
            : $"[{_n.Schema}].[{_n.Table}]";
    }

    private string QualifiedServerTable()
    {
        return string.IsNullOrEmpty(_n.ServerSchema)
            ? $"[{_n.ServerTable}]"
            : $"[{_n.ServerSchema}].[{_n.ServerTable}]";
    }

    private string QualifiedWorkerGroupTable()
    {
        return string.IsNullOrEmpty(_n.WorkerGroupSchema)
            ? $"[{_n.WorkerGroupTable}]"
            : $"[{_n.WorkerGroupSchema}].[{_n.WorkerGroupTable}]";
    }

    private string QualifiedBackgroundServiceInstanceTable()
    {
        return string.IsNullOrEmpty(_n.BackgroundServiceSchema)
            ? $"[{_n.BackgroundServiceInstanceTable}]"
            : $"[{_n.BackgroundServiceSchema}].[{_n.BackgroundServiceInstanceTable}]";
    }

    private string QualifiedBackgroundServiceLeaseTable()
    {
        return string.IsNullOrEmpty(_n.BackgroundServiceSchema)
            ? $"[{_n.BackgroundServiceLeaseTable}]"
            : $"[{_n.BackgroundServiceSchema}].[{_n.BackgroundServiceLeaseTable}]";
    }

    private string QualifiedBackgroundServiceDefinitionTable()
    {
        return string.IsNullOrEmpty(_n.BackgroundServiceSchema)
            ? $"[{_n.BackgroundServiceDefinitionTable}]"
            : $"[{_n.BackgroundServiceSchema}].[{_n.BackgroundServiceDefinitionTable}]";
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
            ? $"[{table}]"
            : $"[{schema}].[{table}]";

        return $"SELECT * FROM {qualified} WITH (UPDLOCK, ROWLOCK) WHERE [{nameColumn}] = {{0}}";
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

        // The inner SELECT picks the OLDEST rows, but UPDATE ... OUTPUT gives them back in no
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
