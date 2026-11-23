using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Warp.Core.Data.Entities;

namespace Warp.Provider.PostgreSql;

/// <summary>
/// The cluster-wide task statements (rule 6.20), built once from the model's resolved names, as the
/// host's naming convention and schema left them.
/// </summary>
internal sealed class PostgresClusterTaskSql
{
    private PostgresClusterTaskSql(string claim, string request, string renew, string complete)
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

    public static PostgresClusterTaskSql Build(IModel model)
    {
        var task = model.FindEntityType(typeof(ClusterTask))
            ?? throw new InvalidOperationException("ClusterTask entity not registered in the model.");
        var server = model.FindEntityType(typeof(Server))
            ?? throw new InvalidOperationException("Server entity not registered in the model.");

        var table = Qualified(task);
        var serverTable = Qualified(server);
        var name = Column(task, nameof(ClusterTask.Name));
        var interval = Column(task, nameof(ClusterTask.IntervalSeconds));
        var limit = Column(task, nameof(ClusterTask.IdleBackoffLimitSeconds));
        var streak = Column(task, nameof(ClusterTask.IdleStreak));
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

        // The lease lasts one interval, so an idle run needs no completion write: it simply expires as
        // the task falls due again. A run claimed by a schedule moves the next run on by the interval,
        // doubled per consecutive idle run up to the task's backoff limit; one claimed by a request does
        // not back off.
        var leaseFor = $"make_interval(secs => GREATEST(t.{interval}, 1))";
        var backedOff = $"LEAST(t.{interval} * POWER(2, LEAST(t.{streak}, 20)), GREATEST(t.{interval}, COALESCE(t.{limit}, 0)))";
        var legacy = $"EXISTS (SELECT 1 FROM {serverTable} s WHERE s.{coordination} IS NULL AND s.{heartbeat} >= @liveSince)";

        var claim = $@"
            WITH due AS (
                SELECT t.{name} AS task FROM {table} t
                WHERE t.{name} = ANY(@names)
                  AND t.{interval} IS NOT NULL
                  AND (t.{next} <= @now OR t.{requested} IS NOT NULL)
                  AND (t.{lease} IS NULL OR t.{lease} <= @now)
                FOR UPDATE SKIP LOCKED
            ),
            claimed AS (
                UPDATE {table} t
                SET {lease} = @now + {leaseFor},
                    {claimedBy} = @server,
                    {claimedAt} = @now,
                    {next} = @now + make_interval(secs => CASE WHEN t.{requested} IS NOT NULL THEN t.{interval} ELSE {backedOff} END),
                    {streak} = CASE WHEN t.{requested} IS NOT NULL THEN 0 ELSE t.{streak} + 1 END,
                    {requested} = NULL
                FROM due
                WHERE t.{name} = due.task
                RETURNING t.{name} AS task, t.{next} AS due_at
            )
            SELECT c.task AS ""Name"", TRUE AS ""Claimed"", c.due_at AS ""DueAt"", {legacy} AS ""Legacy""
            FROM claimed c
            UNION ALL
            SELECT t.{name}, FALSE, GREATEST(t.{next}, COALESCE(t.{lease}, t.{next})), {legacy}
            FROM {table} t
            WHERE t.{name} = ANY(@names)
              AND t.{interval} IS NOT NULL
              AND t.{name} NOT IN (SELECT task FROM claimed)";

        var request = $@"
            WITH claimed AS (
                UPDATE {table} t
                SET {lease} = @now + {leaseFor},
                    {claimedBy} = @server,
                    {claimedAt} = @now,
                    {next} = @now + make_interval(secs => t.{interval}),
                    {streak} = 0,
                    {requested} = NULL
                WHERE t.{name} = @name
                  AND t.{interval} IS NOT NULL
                  AND (t.{lease} IS NULL OR t.{lease} <= @now)
                RETURNING t.{name}
            ),
            asked AS (
                UPDATE {table} t
                SET {requested} = @now
                WHERE t.{name} = @name
                  AND NOT EXISTS (SELECT 1 FROM claimed)
                RETURNING t.{name}
            )
            SELECT (SELECT COUNT(*) FROM claimed)::int AS ""Value""";

        var renew = $@"
            UPDATE {table} t
            SET {lease} = @now + {leaseFor}
            WHERE t.{name} = @name
              AND t.{claimedBy} = @server
            RETURNING 1 AS ""Value""";

        var complete = $@"
            UPDATE {table} t
            SET {lease} = NULL,
                {claimedBy} = NULL,
                {claimedAt} = NULL,
                {streak} = CASE WHEN @didWork THEN 0 ELSE t.{streak} END,
                {next} = CASE
                    WHEN @rerun OR t.{requested} IS NOT NULL THEN @now
                    WHEN @didWork THEN @now + make_interval(secs => COALESCE(t.{interval}, 0))
                    ELSE t.{next} END,
                {requested} = NULL,
                {lastRun} = CASE WHEN @record THEN @now ELSE t.{lastRun} END,
                {lastServer} = CASE WHEN @record THEN @server ELSE t.{lastServer} END,
                {lastStatus} = CASE WHEN @record THEN @status ELSE t.{lastStatus} END,
                {lastMessage} = CASE WHEN @record THEN @message ELSE t.{lastMessage} END,
                {lastDuration} = CASE WHEN @record THEN @durationMs ELSE t.{lastDuration} END
            WHERE t.{name} = @name
              AND t.{claimedBy} = @server";

        return new PostgresClusterTaskSql(claim, request, renew, complete);
    }

    private static string Qualified(IEntityType entity)
    {
        var table = entity.GetTableName()
            ?? throw new InvalidOperationException($"{entity.ClrType.Name} has no resolved table name.");
        var schema = entity.GetSchema();

        return string.IsNullOrEmpty(schema) ? $"\"{table}\"" : $"\"{schema}\".\"{table}\"";
    }

    private static string Column(IEntityType entity, string property)
    {
        var column = entity.FindProperty(property)?.GetColumnName()
            ?? throw new InvalidOperationException($"{entity.ClrType.Name}.{property} has no resolved column name.");

        return $"\"{column}\"";
    }
}
