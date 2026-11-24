using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Warp.Core.Data.Entities;

namespace Warp.Provider.PostgreSql;

/// <summary>
/// The cluster-wide task statements (rule 6.23), built once from the model's resolved names, as the
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

        // Every time here is the database's own now() (rule 6.23): whether a lease is free never depends on
        // two servers' clocks agreeing, as a lock never did. The lease lasts one interval, so an idle run needs
        // no completion write: it lapses as the task falls due again. Due times go back as seconds from
        // now, so a server's own clock only decides how long it sleeps.
        //
        // The next run is the next multiple of the interval (from the epoch) at least half an interval
        // away, not "now + interval": tasks with the same or a multiple interval then fall due at the same
        // instant and ONE claim statement covers them, where drifting due times cost a claim each. Half an
        // interval is the shortest lease, longer than the renewal period (a third), so a run started late
        // in a tick skips the next tick instead of losing its lease mid-run.
        var grid = $"GREATEST(t.{interval}, 0.05)";
        var nextRun = $"to_timestamp((floor((EXTRACT(EPOCH FROM now()) + {grid} / 2) / {grid}) + 1) * {grid})";
        var leaseUntil = $"GREATEST({nextRun}, now() + interval '1 second')";
        var leaseFor = $"make_interval(secs => GREATEST(t.{interval}, 1))";
        var legacy = $"EXISTS (SELECT 1 FROM {serverTable} s WHERE s.{coordination} IS NULL AND s.{heartbeat} >= now() - make_interval(secs => @liveWindow))";

        var claim = $@"
            WITH due AS (
                SELECT t.{name} AS task FROM {table} t
                WHERE t.{name} = ANY(@names)
                  AND t.{interval} IS NOT NULL
                  AND (t.{next} <= now() OR t.{requested} IS NOT NULL)
                  AND (t.{lease} IS NULL OR t.{lease} <= now())
                FOR UPDATE SKIP LOCKED
            ),
            claimed AS (
                UPDATE {table} t
                SET {lease} = {leaseUntil},
                    {claimedBy} = @server,
                    {claimedAt} = now(),
                    {next} = {nextRun},
                    {requested} = NULL
                FROM due
                WHERE t.{name} = due.task
                RETURNING t.{name} AS task, t.{next} AS due_at
            )
            SELECT c.task, TRUE, EXTRACT(EPOCH FROM c.due_at - now())::float8, {legacy}
            FROM claimed c
            UNION ALL
            SELECT t.{name}, FALSE, EXTRACT(EPOCH FROM GREATEST(t.{next}, COALESCE(t.{lease}, t.{next})) - now())::float8, {legacy}
            FROM {table} t
            WHERE t.{name} = ANY(@names)
              AND t.{interval} IS NOT NULL
              AND t.{name} NOT IN (SELECT task FROM claimed)";

        var request = $@"
            WITH claimed AS (
                UPDATE {table} t
                SET {lease} = {leaseUntil},
                    {claimedBy} = @server,
                    {claimedAt} = now(),
                    {next} = {nextRun},
                    {requested} = NULL
                WHERE t.{name} = @name
                  AND t.{interval} IS NOT NULL
                  AND (t.{lease} IS NULL OR t.{lease} <= now() OR t.{claimedBy} = @server)
                RETURNING t.{name}
            ),
            asked AS (
                UPDATE {table} t
                SET {requested} = now()
                WHERE t.{name} = @name
                  AND NOT EXISTS (SELECT 1 FROM claimed)
                RETURNING t.{name}
            )
            SELECT (SELECT COUNT(*) FROM claimed)::int";

        var renew = $@"
            UPDATE {table} t
            SET {lease} = GREATEST(t.{lease}, now() + {leaseFor})
            WHERE t.{name} = @name
              AND t.{claimedBy} = @server
            RETURNING 1";

        var complete = $@"
            UPDATE {table} t
            SET {lease} = NULL,
                {claimedBy} = NULL,
                {claimedAt} = NULL,
                {next} = CASE
                    WHEN @rerun OR t.{requested} IS NOT NULL THEN now()
                    WHEN @didWork AND t.{interval} IS NOT NULL THEN {nextRun}
                    ELSE t.{next} END,
                {requested} = NULL,
                {lastRun} = CASE WHEN @record THEN now() ELSE t.{lastRun} END,
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
