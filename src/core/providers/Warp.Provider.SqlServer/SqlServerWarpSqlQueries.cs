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
    private const string Separator = "\x1F"; // ASCII "unit separator" — safe: queue names / GUIDs won't contain it

    private readonly WarpJobTableNames _n;

    private readonly string _claimEnqueuedJobsSql;
    private readonly string _claimEnqueuedMessagesSql;
    private readonly string _lockStaleProcessingJobsSql;
    private readonly string _lockJobByIdWaitSql;
    private readonly string _lockAllServersSql;
    private readonly string _heartbeatSql;
    private readonly string _activateScheduledJobsSql;
    private readonly string _lockLeaseByServiceNameSql;
    private readonly string _lockDefinitionByServiceNameSql;

    public SqlServerWarpSqlQueries(WarpJobTableNames names)
    {
        _n = names;

        var table = QualifiedTable();
        var serverTable = QualifiedServerTable();

        // Atomic claim via UPDATE with OUTPUT. The candidates CTE picks up to N rows in
        // queue/schedule order with ROWLOCK+UPDLOCK+READPAST so concurrent workers skip each
        // other's locked rows. The UPDATE mutates the same rows and OUTPUT INSERTED.* streams
        // them back — caller gets tracked Job entities and sees post-update state. Pause is
        // enforced in C# via PauseStateHolder before the worker calls into this query — pause
        // has heartbeat-cadence latency, see PauseStateHolder for details.
        _claimEnqueuedJobsSql = $@"
            WITH candidates AS (
                SELECT TOP ({{3}}) *
                FROM {table} WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE [{_n.Kind}] = {(int)JobKind.Job}
                  AND [{_n.CurrentState}] = {(int)State.Enqueued}
                  AND [{_n.Queue}] IN (SELECT value FROM STRING_SPLIT({{2}}, N'{Separator}'))
                ORDER BY [{_n.Queue}], [{_n.ScheduleTime}]
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

        _lockAllServersSql = $@"
            SELECT *
            FROM {serverTable} WITH (ROWLOCK, UPDLOCK)";

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

    public async Task<List<Job>> ClaimEnqueuedJobsAsync(
        TContext context,
        string[] queues,
        Guid workerId,
        DateTime now,
        int limit,
        CancellationToken ct)
    {
        var queueCsv = string.Join(Separator, queues);
        return await context.Set<Job>()
            .FromSqlRaw(_claimEnqueuedJobsSql, workerId, now, queueCsv, limit)
            .ToListAsync(ct);
    }

    public async Task<List<Job>> ClaimEnqueuedMessagesAsync(TContext context, int limit, CancellationToken ct)
    {
        return await context.Set<Job>()
            .FromSqlRaw(_claimEnqueuedMessagesSql, limit)
            .ToListAsync(ct);
    }

    public async Task<List<Job>> LockStaleProcessingJobsAsync(
        TContext context,
        DateTime cutoff,
        CancellationToken ct)
    {
        return await context.Set<Job>()
            .FromSqlRaw(_lockStaleProcessingJobsSql, cutoff)
            .ToListAsync(ct);
    }

    public async Task<Job?> LockJobByIdWaitAsync(TContext context, Guid jobId, CancellationToken ct)
    {
        return await context.Set<Job>()
            .FromSqlRaw(_lockJobByIdWaitSql, jobId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<Server>> LockAllServersAsync(TContext context, CancellationToken ct)
    {
        return await context.Set<Server>()
            .FromSqlRaw(_lockAllServersSql)
            .ToListAsync(ct);
    }

    public async Task<HeartbeatResult?> HeartbeatAsync(
        TContext context,
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
        TContext context,
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
        TContext context,
        string lockKey,
        Func<TContext, CancellationToken, Task<T>> work,
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
        TContext context,
        string serviceName,
        CancellationToken ct)
    {
        return await context.Set<BackgroundServiceLease>()
            .FromSqlRaw(_lockLeaseByServiceNameSql, serviceName)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<BackgroundServiceDefinition?> LockDefinitionByServiceNameAsync(
        TContext context,
        string serviceName,
        CancellationToken ct)
    {
        return await context.Set<BackgroundServiceDefinition>()
            .FromSqlRaw(_lockDefinitionByServiceNameSql, serviceName)
            .FirstOrDefaultAsync(ct);
    }
}
