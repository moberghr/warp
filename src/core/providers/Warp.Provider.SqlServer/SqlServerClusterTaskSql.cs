using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Warp.Core.Data.Entities;

namespace Warp.Provider.SqlServer;

/// <summary>
/// The cluster-wide task statements (rule 6.20), built once from the model's resolved names, as the
/// host's naming convention and schema left them. The SQL Server twin of the PostgreSQL statements:
/// the same rules, in the same order.
/// </summary>
internal sealed class SqlServerClusterTaskSql
{
    /// <summary>Separates task names in the claim's name list: a character no task name holds.</summary>
    public const char NameSeparator = '\u001f';

    private SqlServerClusterTaskSql(string claim, string request, string renew, string complete)
    {
        Claim = claim;
        Request = request;
        Renew = renew;
        Complete = complete;
    }

    public string Claim { get; }

    public string Request { get; }

    public string Renew { get; }

    public string Complete { get; }

    public static SqlServerClusterTaskSql Build(IModel model)
    {
        var task = model.FindEntityType(typeof(ClusterTask))
            ?? throw new InvalidOperationException("ClusterTask entity not registered in the model.");
        var server = model.FindEntityType(typeof(Server))
            ?? throw new InvalidOperationException("Server entity not registered in the model.");

        var table = Qualified(task);
        var serverTable = Qualified(server);
        var name = Column(task, nameof(ClusterTask.Name));
        var interval = Column(task, nameof(ClusterTask.IntervalSeconds));
        var next = Column(task, nameof(ClusterTask.NextRunAt));
        var lease = Column(task, nameof(ClusterTask.LeaseUntil));
        var claimedBy = Column(task, nameof(ClusterTask.ClaimedBy));
        var claimedAt = Column(task, nameof(ClusterTask.ClaimedAt));
        var requested = Column(task, nameof(ClusterTask.RunRequestedAt));
        var lastRun = Column(task, nameof(ClusterTask.LastRunAt));
        var lastServer = Column(task, nameof(ClusterTask.LastServerId));
        var lastStatus = Column(task, nameof(ClusterTask.LastStatus));
        var lastMessage = Column(task, nameof(ClusterTask.LastMessage));
        var lastDuration = Column(task, nameof(ClusterTask.LastDurationMs));
        var coordination = Column(server, nameof(Server.CoordinationVersion));
        var heartbeat = Column(server, nameof(Server.LastHeartbeatTime));

        // Every time here is the database's own clock (rule 6.20), read once per statement into @now:
        // whether a lease is free never depends on two servers' clocks agreeing.
        //
        // The next run is the next multiple of the interval (from the epoch) at least half an interval
        // away, not "now + interval": tasks with the same or a multiple interval then fall due at the
        // same instant and ONE claim covers them. Half an interval is the shortest lease, longer than
        // the renewal period (a third). DATEADD takes an int, so the instant is put back together from
        // whole days and the milliseconds within the day.
        const string Now = "DECLARE @now datetime2 = SYSUTCDATETIME();";
        const string Epoch = "CAST('1970-01-01' AS datetime2)";
        var grid = $"(CASE WHEN t.{interval} < 0.05 THEN 0.05 ELSE t.{interval} END)";
        var nowSeconds = $"(CAST(DATEDIFF_BIG(MILLISECOND, {Epoch}, @now) AS float) / 1000.0)";
        var nextMs = $"CAST(ROUND((FLOOR(({nowSeconds} + {grid} / 2) / {grid}) + 1) * {grid} * 1000, 0) AS bigint)";
        var nextRun = $"DATEADD(MILLISECOND, CAST({nextMs} % 86400000 AS int), DATEADD(DAY, CAST({nextMs} / 86400000 AS int), {Epoch}))";
        var leaseUntil = $"(CASE WHEN {nextRun} > DATEADD(SECOND, 1, @now) THEN {nextRun} ELSE DATEADD(SECOND, 1, @now) END)";
        var renewedUntil = $"DATEADD(MILLISECOND, CAST((CASE WHEN t.{interval} < 1 THEN 1 ELSE t.{interval} END) * 1000 AS int), @now)";
        var legacy = $"CASE WHEN EXISTS (SELECT 1 FROM {serverTable} s WHERE s.{coordination} IS NULL AND s.{heartbeat} >= DATEADD(MILLISECOND, -@liveWindowMs, @now)) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END";
        const string Names = "SELECT value FROM STRING_SPLIT(@names, NCHAR(31))";

        // READPAST skips a row another server is claiming this instant, as SKIP LOCKED does: that
        // server has it. The report afterwards reads every named task, claimed or not.
        var claim = $@"
            {Now}
            DECLARE @claimed TABLE (task nvarchar(200) PRIMARY KEY, due datetime2);

            UPDATE t
            SET {lease} = {leaseUntil},
                {claimedBy} = @server,
                {claimedAt} = @now,
                {next} = {nextRun},
                {requested} = NULL
            OUTPUT INSERTED.{name}, INSERTED.{next} INTO @claimed
            FROM {table} t WITH (ROWLOCK, UPDLOCK, READPAST)
            WHERE t.{name} IN ({Names})
              AND t.{interval} IS NOT NULL
              AND (t.{next} <= @now OR t.{requested} IS NOT NULL)
              AND (t.{lease} IS NULL OR t.{lease} <= @now);

            SELECT t.{name},
                   CASE WHEN c.task IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END,
                   CAST(DATEDIFF_BIG(MILLISECOND, @now,
                       CASE WHEN c.task IS NOT NULL THEN c.due
                            WHEN t.{lease} IS NOT NULL AND t.{lease} > t.{next} THEN t.{lease}
                            ELSE t.{next} END) AS float) / 1000.0,
                   {legacy}
            FROM {table} t
            LEFT JOIN @claimed c ON c.task = t.{name}
            WHERE t.{name} IN ({Names})
              AND t.{interval} IS NOT NULL;";

        var request = $@"
            {Now}
            UPDATE t
            SET {lease} = {leaseUntil},
                {claimedBy} = @server,
                {claimedAt} = @now,
                {next} = {nextRun},
                {requested} = NULL
            FROM {table} t WITH (ROWLOCK)
            WHERE t.{name} = @name
              AND t.{interval} IS NOT NULL
              AND (t.{lease} IS NULL OR t.{lease} <= @now);

            IF @@ROWCOUNT > 0
            BEGIN
                SELECT 1;
            END
            ELSE
            BEGIN
                UPDATE {table} SET {requested} = @now WHERE {name} = @name;
                SELECT 0;
            END";

        // A renewal only ever extends: the grid can have set the lease further out than one interval.
        var renew = $@"
            {Now}
            UPDATE t
            SET {lease} = CASE WHEN t.{lease} > {renewedUntil} THEN t.{lease} ELSE {renewedUntil} END
            OUTPUT 1
            FROM {table} t WITH (ROWLOCK)
            WHERE t.{name} = @name
              AND t.{claimedBy} = @server;";

        var complete = $@"
            {Now}
            UPDATE t
            SET {lease} = NULL,
                {claimedBy} = NULL,
                {claimedAt} = NULL,
                {next} = CASE
                    WHEN @rerun = 1 OR t.{requested} IS NOT NULL THEN @now
                    WHEN @didWork = 1 AND t.{interval} IS NOT NULL THEN {nextRun}
                    ELSE t.{next} END,
                {requested} = NULL,
                {lastRun} = CASE WHEN @record = 1 THEN @now ELSE t.{lastRun} END,
                {lastServer} = CASE WHEN @record = 1 THEN @server ELSE t.{lastServer} END,
                {lastStatus} = CASE WHEN @record = 1 THEN @status ELSE t.{lastStatus} END,
                {lastMessage} = CASE WHEN @record = 1 THEN @message ELSE t.{lastMessage} END,
                {lastDuration} = CASE WHEN @record = 1 THEN @durationMs ELSE t.{lastDuration} END
            FROM {table} t WITH (ROWLOCK)
            WHERE t.{name} = @name
              AND t.{claimedBy} = @server;";

        return new SqlServerClusterTaskSql(claim, request, renew, complete);
    }

    private static string Qualified(IEntityType entity)
    {
        var table = entity.GetTableName()
            ?? throw new InvalidOperationException($"{entity.ClrType.Name} has no resolved table name.");
        var schema = entity.GetSchema();

        return string.IsNullOrEmpty(schema) ? $"[{table}]" : $"[{schema}].[{table}]";
    }

    private static string Column(IEntityType entity, string property)
    {
        var column = entity.FindProperty(property)?.GetColumnName()
            ?? throw new InvalidOperationException($"{entity.ClrType.Name}.{property} has no resolved column name.");

        return $"[{column}]";
    }
}
