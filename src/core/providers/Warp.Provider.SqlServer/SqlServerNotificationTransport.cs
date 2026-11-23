using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Warp.Core.Logging;
using Warp.Core.Notifications;

namespace Warp.Provider.SqlServer;

/// <summary>
/// SQL Server Service Broker transport, delivering every notification to EVERY listening process, like
/// PostgreSQL's LISTEN/NOTIFY. Sets up <c>ENABLE_BROKER</c> + message type / contract / the sending
/// service idempotently on first use. Each listening process creates a queue and service of its own
/// (<c>{channel}/queue/{id}</c>, <c>{channel}/service/{id}</c>) and drops them when it stops;
/// <see cref="PublishAsync"/> sends one message to every such service in one batch. A Service Broker queue
/// hands each message to ONE receiver, so the single shared queue this used to have woke one random
/// process per notification, and a wake that landed on a process without workers (a publisher or
/// dashboard host) was lost. Queues left by a process that died without dropping them are removed by the
/// other listeners once their oldest message is older than <see cref="StaleQueueAge"/>: a live listener
/// drains its queue continuously, so only a dead one's backs up. <see cref="ListenAsync"/> holds a
/// dedicated long-lived connection and loops <c>WAITFOR (RECEIVE ...)</c> with a 30s timeout heartbeat,
/// ending each conversation it receives in the same batch.
/// </summary>
public sealed class SqlServerNotificationTransport : IWarpNotificationTransport
{
    // Stale queues are swept when a listener starts and then every this many 30 s heartbeats (about 5 min).
    private const int SweepEveryHeartbeats = 10;

    private readonly string _connectionString;
    private readonly string _channelName;
    private readonly string _messageTypeValue;
    private readonly string _contractValue;
    private readonly string _queueValue;
    private readonly string _serviceValue;
    private readonly string _listenerServicePrefix;
    private readonly string _ownQueueValue;
    private readonly string _ownServiceValue;
    private readonly ILogger<SqlServerNotificationTransport>? _logger;

    // Lazy<Task> gives us the once-only "setup-or-fail-permanently" semantics for free:
    // first awaiter runs the setup, others wait; if it faults, every subsequent await throws
    // the cached exception without reopening any connections.
    private readonly Lazy<Task> _setup;
    private readonly TaskCompletionSource _listenerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ListenerReady => _listenerReady.Task;

    /// <summary>A listener queue whose oldest message has waited this long belongs to a dead process.</summary>
    internal TimeSpan StaleQueueAge { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>This process's listener service, for tests.</summary>
    internal string ListenerServiceName => _ownServiceValue;

    public SqlServerNotificationTransport(string connectionString, WarpDatabasePushConfiguration options, ILogger<SqlServerNotificationTransport>? logger = null)
    {
        _connectionString = connectionString;
        _channelName = options.ChannelName;
        _logger = logger;

        // Channel name must be alphanumeric + underscore — we interpolate it into object names
        // (message type / contract / queue / service) and into DDL, which can't use parameters.
        if (!IsSafeIdentifier(_channelName))
        {
            throw new ArgumentException(
                $"ChannelName '{_channelName}' is not a safe identifier. Use alphanumeric + underscore only.",
                nameof(options));
        }

        _messageTypeValue = $"{_channelName}/msg";
        _contractValue = $"{_channelName}/contract";

        // The shared service is the SENDER of every notification, since a dialog needs an initiator service.
        // Nothing listens on its queue any more: it only receives the end-of-dialog acknowledgements.
        _queueValue = $"{_channelName}/queue";
        _serviceValue = $"{_channelName}/service";

        // This process's own listener queue and service.
        var id = Guid.NewGuid().ToString("N");
        _listenerServicePrefix = $"{_channelName}/service/";
        _ownQueueValue = $"{_channelName}/queue/{id}";
        _ownServiceValue = _listenerServicePrefix + id;

        _setup = new Lazy<Task>(RunSetup);
    }

    public async Task PublishAsync(NotificationKind kind, string? queue, CancellationToken ct)
    {
        try
        {
            // WaitAsync so a caller can give up on a slow/stalled broker setup without
            // losing the cached task — the next caller re-waits with their own token.
            await _setup.Value.WaitAsync(ct);

            var payload = Encoding.UTF8.GetBytes(Encode(kind, queue));

            // One batch, one round trip: a message to every listening process's service.
            var sql = $@"
                DECLARE @target NVARCHAR(256), @conv UNIQUEIDENTIFIER;
                DECLARE targets CURSOR LOCAL FAST_FORWARD FOR
                    SELECT name FROM sys.services WHERE LEFT(name, LEN(@prefix)) = @prefix;
                OPEN targets;
                FETCH NEXT FROM targets INTO @target;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    BEGIN DIALOG @conv
                        FROM SERVICE [{_serviceValue}]
                        TO SERVICE @target
                        ON CONTRACT [{_contractValue}]
                        WITH ENCRYPTION = OFF, LIFETIME = 60;
                    SEND ON CONVERSATION @conv
                        MESSAGE TYPE [{_messageTypeValue}] (@payload);
                    END CONVERSATION @conv;
                    FETCH NEXT FROM targets INTO @target;
                END
                CLOSE targets;
                DEALLOCATE targets;";

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@payload", System.Data.SqlDbType.VarBinary, payload.Length).Value = payload;
            cmd.Parameters.Add("@prefix", System.Data.SqlDbType.NVarChar, 256).Value = _listenerServicePrefix;
            await cmd.ExecuteNonQueryAsync(ct);
            WarpTelemetry.NotificationsPublished.Add(1, new KeyValuePair<string, object?>("transport", "sqlserver"), new KeyValuePair<string, object?>("kind", kind.ToString()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BrokerSetupFailedException)
        {
            // Permanent failure already logged at setup time; increment counter but don't spam logs.
            WarpTelemetry.NotificationPublishFailures.Add(1, new KeyValuePair<string, object?>("transport", "sqlserver"), new KeyValuePair<string, object?>("kind", kind.ToString()), new KeyValuePair<string, object?>("reason", "broker_setup_failed"));
        }
        catch (Exception ex)
        {
            // Transport contract: PublishAsync must not throw. A missed notification only delays
            // pickup until the next listener reconnect-drain or subsequent notification.
            WarpTelemetry.NotificationPublishFailures.Add(1, new KeyValuePair<string, object?>("transport", "sqlserver"), new KeyValuePair<string, object?>("kind", kind.ToString()));
            _logger?.LogWarning(ex, "SqlServerNotificationTransport.PublishAsync failed (kind={Kind}, queue={Queue})", kind, queue);
        }
    }

    public async IAsyncEnumerable<Notification> ListenAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await _setup.Value.WaitAsync(ct);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // This process's own queue and service, so every publish reaches it (see the class summary), and
        // the queues of dead processes swept before it starts listening.
        await ExecuteAsync(conn, CreateOwnQueueSql(), ct);
        await SweepStaleQueuesAsync(conn, ct);

        // Service Broker setup is the slow part and this process's queue now exists. From this point a
        // publish reaches it and the WAITFOR loop is the listener. Tests use this signal to gate the first
        // publish so it can't be dropped by a host-startup vs. listener-startup race. It latches once, and
        // reconnects rely on the listener task's drain-on-reconnect to catch up.
        _listenerReady.TrySetResult();

        try
        {
            // WAITFOR RECEIVE blocks the connection until messages arrive or the TIMEOUT fires. The 30s
            // timeout is a heartbeat so we can observe cancellation — not a poll of job state.
            // CommandTimeout is set slightly higher so the client doesn't time out before the server
            // does. Every conversation received is ended in the same batch: the sender ends its side
            // right after sending, and a receiver that never ended its own left an endpoint behind in
            // sys.conversation_endpoints for every notification.
            var sql = $@"
                DECLARE @received TABLE (handle UNIQUEIDENTIFIER, type_name NVARCHAR(256), body VARBINARY(MAX));
                WAITFOR (
                    RECEIVE TOP(10)
                        conversation_handle,
                        message_type_name,
                        message_body
                    FROM [{_ownQueueValue}]
                    INTO @received
                ), TIMEOUT 30000;

                DECLARE @handle UNIQUEIDENTIFIER;
                DECLARE ended CURSOR LOCAL FAST_FORWARD FOR SELECT DISTINCT handle FROM @received;
                OPEN ended;
                FETCH NEXT FROM ended INTO @handle;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    END CONVERSATION @handle;
                    FETCH NEXT FROM ended INTO @handle;
                END
                CLOSE ended;
                DEALLOCATE ended;

                SELECT type_name, body FROM @received;";

            var heartbeats = 0;
            while (!ct.IsCancellationRequested)
            {
                var batch = new List<Notification>();
                try
                {
                    await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var type = await reader.IsDBNullAsync(0, ct) ? null : reader.GetString(0);
                        if (!string.Equals(type, _messageTypeValue, StringComparison.Ordinal))
                        {
                            // Ignore system messages (EndDialog, Error, etc.)
                            continue;
                        }

                        if (await reader.IsDBNullAsync(1, ct))
                        {
                            continue;
                        }

                        var body = (byte[])reader[1];
                        var text = Encoding.UTF8.GetString(body);
                        if (TryDecode(text, out var notification))
                        {
                            batch.Add(notification);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    yield break;
                }

                foreach (var notification in batch)
                {
                    yield return notification;
                }

                if (++heartbeats % SweepEveryHeartbeats == 0)
                {
                    await SweepStaleQueuesAsync(conn, ct);
                }
            }
        }
        finally
        {
            await DropOwnQueueAsync();
        }
    }

    /// <summary>
    /// One-shot Service Broker setup: verify ENABLE_BROKER, then idempotent
    /// CREATE MESSAGE TYPE / CONTRACT / QUEUE / SERVICE. Cached by <see cref="_setup"/> —
    /// faulted tasks stick, so setup failure disables the transport for its lifetime.
    /// </summary>
    private async Task RunSetup()
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // Broker-enable is user responsibility when SET ENABLE_BROKER requires exclusive access
            // (ROLLBACK IMMEDIATE kicks all connections including ours). Fail fast with guidance.
            var isEnabled = await IsBrokerEnabled(conn, CancellationToken.None);
            if (!isEnabled)
            {
                throw new BrokerSetupFailedException(
                    $"Service Broker is not enabled on database '{conn.Database}'. Run: " +
                    $"ALTER DATABASE [{conn.Database}] SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE;");
            }

            // Idempotent create of each object. Wrapping DDL in EXEC() defers parsing so the
            // IF guard runs before the referenced type/contract exists in the first setup. Serialized per
            // channel by an application lock: processes starting together raced on the IF NOT EXISTS, and
            // the loser's CREATE failed with "already exists", disabling its transport for good.
            var setupSql = $@"
                EXEC sp_getapplock @Resource = N'{_channelName}/setup', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 30000;

                IF NOT EXISTS (SELECT 1 FROM sys.service_message_types WHERE name = N'{_messageTypeValue}')
                    EXEC('CREATE MESSAGE TYPE [{_messageTypeValue}] VALIDATION = NONE;');

                IF NOT EXISTS (SELECT 1 FROM sys.service_contracts WHERE name = N'{_contractValue}')
                    EXEC('CREATE CONTRACT [{_contractValue}] ([{_messageTypeValue}] SENT BY ANY);');

                IF NOT EXISTS (SELECT 1 FROM sys.service_queues WHERE name = N'{_queueValue}')
                    EXEC('CREATE QUEUE [{_queueValue}];');

                IF NOT EXISTS (SELECT 1 FROM sys.services WHERE name = N'{_serviceValue}')
                    EXEC('CREATE SERVICE [{_serviceValue}] ON QUEUE [{_queueValue}] ([{_contractValue}]);');

                EXEC sp_releaseapplock @Resource = N'{_channelName}/setup', @LockOwner = 'Session';";

            await using var cmd = new SqlCommand(setupSql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException ex)
        {
            throw new BrokerSetupFailedException(
                $"Failed to create Service Broker objects for channel '{_channelName}'. " +
                "Check that the user has permission to CREATE MESSAGE TYPE/CONTRACT/QUEUE/SERVICE.",
                ex);
        }
    }

    private static async Task ExecuteAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private string CreateOwnQueueSql() => $@"
        IF NOT EXISTS (SELECT 1 FROM sys.service_queues WHERE name = N'{_ownQueueValue}')
            EXEC('CREATE QUEUE [{_ownQueueValue}];');

        IF NOT EXISTS (SELECT 1 FROM sys.services WHERE name = N'{_ownServiceValue}')
            EXEC('CREATE SERVICE [{_ownServiceValue}] ON QUEUE [{_ownQueueValue}] ([{_contractValue}]);');";

    // Drops the listener queues (and their services) of processes that died without dropping them: a queue
    // whose oldest message has waited longer than StaleQueueAge has no listener draining it. Best effort: a
    // failure is logged and the next sweep tries again.
    private async Task SweepStaleQueuesAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sweepSql = @"
            DECLARE @service NVARCHAR(256), @queue NVARCHAR(256), @oldest DATETIME, @sql NVARCHAR(MAX);
            DECLARE candidates CURSOR LOCAL FAST_FORWARD FOR
                SELECT s.name, q.name
                FROM sys.services s
                JOIN sys.service_queues q ON q.object_id = s.service_queue_id
                WHERE LEFT(s.name, LEN(@prefix)) = @prefix AND s.name <> @own;
            OPEN candidates;
            FETCH NEXT FROM candidates INTO @service, @queue;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                SET @oldest = NULL;
                SET @sql = N'SELECT @oldest = MIN(message_enqueue_time) FROM ' + QUOTENAME(@queue) + N';';
                EXEC sp_executesql @sql, N'@oldest DATETIME OUTPUT', @oldest = @oldest OUTPUT;
                IF @oldest IS NOT NULL AND @oldest < DATEADD(SECOND, -@staleSeconds, GETUTCDATE())
                BEGIN
                    SET @sql = N'DROP SERVICE ' + QUOTENAME(@service) + N'; DROP QUEUE ' + QUOTENAME(@queue) + N';';
                    EXEC (@sql);
                END
                FETCH NEXT FROM candidates INTO @service, @queue;
            END
            CLOSE candidates;
            DEALLOCATE candidates;";

        try
        {
            await using var cmd = new SqlCommand(sweepSql, conn);
            cmd.Parameters.Add("@prefix", System.Data.SqlDbType.NVarChar, 256).Value = _listenerServicePrefix;
            cmd.Parameters.Add("@own", System.Data.SqlDbType.NVarChar, 256).Value = _ownServiceValue;
            cmd.Parameters.Add("@staleSeconds", System.Data.SqlDbType.Int).Value = (int)StaleQueueAge.TotalSeconds;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Sweeping stale Service Broker listener queues for channel {Channel} failed", _channelName);
        }
    }

    // Best effort, on a fresh connection: the listen connection may be the reason the loop ended. A queue
    // left behind is swept by another listener once it backs up.
    private async Task DropOwnQueueAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(timeout.Token);
            var dropSql = $@"
                IF EXISTS (SELECT 1 FROM sys.services WHERE name = N'{_ownServiceValue}')
                    EXEC('DROP SERVICE [{_ownServiceValue}];');

                IF EXISTS (SELECT 1 FROM sys.service_queues WHERE name = N'{_ownQueueValue}')
                    EXEC('DROP QUEUE [{_ownQueueValue}];');";
            await ExecuteAsync(conn, dropSql, timeout.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Dropping the Service Broker listener queue {Queue} failed; another listener sweeps it later", _ownQueueValue);
        }
    }

    private static bool IsSafeIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var first = value[0];
        if (!((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z') || first == '_'))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> IsBrokerEnabled(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT is_broker_enabled FROM sys.databases WHERE database_id = DB_ID();",
            conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool enabled && enabled;
    }

    // Payload grammar identical to Postgres: "<kind>:<queue>?". Kept in sync for test reuse.
    // After "F:" come the parents worth checking (FinalizedParentsPayload). A server from before 8.0 sends
    // and reads a bare "F:", which is still valid: it asks for a check of every parent.
    internal static string Encode(NotificationKind kind, string? queue)
    {
        return kind switch
        {
            NotificationKind.JobEnqueued => "J:" + (queue ?? "default"),
            NotificationKind.MessageEnqueued => "M:",
            NotificationKind.JobFinalized => "F:" + queue,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown notification kind"),
        };
    }

    internal static bool TryDecode(string? raw, out Notification notification)
    {
        notification = default;
        if (string.IsNullOrEmpty(raw) || raw.Length < 2 || raw[1] != ':')
        {
            return false;
        }

        switch (raw[0])
        {
            case 'J':
                var queue = raw.Length > 2 ? raw.Substring(2) : "default";
                notification = new Notification(NotificationKind.JobEnqueued, queue);
                return true;
            case 'M':
                notification = new Notification(NotificationKind.MessageEnqueued, null);
                return true;
            case 'F':
                notification = new Notification(NotificationKind.JobFinalized, raw.Length > 2 ? raw.Substring(2) : null);
                return true;
            default:
                return false;
        }
    }
}
