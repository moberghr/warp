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
/// other listeners once their oldest message is older than <see cref="StaleQueueAge"/> AND their owner is
/// gone: a listener holds a session lock named after its queue on its listening connection, so a queue
/// whose lock is free has no process behind it, while one that is merely paused or behind keeps its queue
/// and the wakes waiting in it. <see cref="ListenAsync"/> holds a
/// dedicated long-lived connection and loops <c>WAITFOR (RECEIVE ...)</c> with a 30s timeout heartbeat,
/// ending each conversation it receives in the same batch.
/// <para>
/// Rolling deploy from 7.7 (the bridge, rule 2.9): 7.7 sends to and listens on ONE shared service and queue
/// (<c>{channel}/service</c>, <c>{channel}/queue</c>), created by a 7.7 process when it starts. While that service
/// exists, <see cref="PublishAsync"/> also sends there so 7.7 listeners wake, and every listener runs a second
/// loop on a connection of its own that receives from the shared queue and re-sends what a 7.7 process sent to
/// every per-process service (never back to the shared one, so nothing loops). This release sends from a service
/// of its own (<c>{channel}/sender/service</c>), which is how the relay tells its own copies, already delivered,
/// from a 7.7 process's: the shared objects exist only where a 7.7 process ran, and nothing here creates them.
/// REMOVE the bridge in the release after 8.x, with <c>LegacyTaskLock</c>.
/// </para>
/// </summary>
public sealed class SqlServerNotificationTransport : IWarpNotificationTransport
{
    private readonly string _connectionString;
    private readonly string _channelName;
    private readonly string _messageTypeValue;
    private readonly string _contractValue;
    private readonly string _queueValue;
    private readonly string _serviceValue;
    private readonly string _legacyQueueValue;
    private readonly string _legacyServiceValue;
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

    /// <summary>
    /// How often a listener sweeps stale queues after its first sweep at start. Wall-clock: counted in loop
    /// iterations it ran every ten notifications under load, since each received batch ends an iteration.
    /// </summary>
    internal TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often a listener looks for 7.7's shared queue while it does not exist, and retries after the relay
    /// failed (rule 2.9 bridge). REMOVE with the bridge in the release after 8.x.
    /// </summary>
    internal TimeSpan LegacyQueueCheckInterval { get; init; } = TimeSpan.FromMinutes(1);

    // How many wakes a stopping relay sends into 7.7's shared queue at most, and how far apart (WakeLegacyRelayAsync).
    private const int MaxRelayWakes = 25;

    private static readonly TimeSpan RelayWakeInterval = TimeSpan.FromMilliseconds(200);

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

        // The sender of every notification, since a dialog needs an initiator service. Nothing listens on its
        // queue: it only receives the end-of-dialog acknowledgements. Not 7.7's shared service: the relay tells
        // what 7.7 sent from what this release sent by the initiator (the class summary), and only a 7.7 process
        // then creates the shared objects.
        _queueValue = $"{_channelName}/sender/queue";
        _serviceValue = $"{_channelName}/sender/service";

        // 7.7's shared queue and service (the rule 2.9 bridge; REMOVE in the release after 8.x).
        _legacyQueueValue = $"{_channelName}/queue";
        _legacyServiceValue = $"{_channelName}/service";

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

            // One batch, one round trip: a message to every listening process's service, and to 7.7's shared
            // service when a 7.7 process created it, so 7.7 listeners wake during a rolling deploy (the bridge,
            // REMOVE in the release after 8.x).
            var sql = $@"
                DECLARE @target NVARCHAR(256), @conv UNIQUEIDENTIFIER;
                DECLARE targets CURSOR LOCAL FAST_FORWARD FOR
                    SELECT name FROM sys.services WHERE LEFT(name, LEN(@prefix)) = @prefix OR name = @legacy;
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
            cmd.Parameters.Add("@legacy", System.Data.SqlDbType.NVarChar, 256).Value = _legacyServiceValue;
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
        // the queues of dead processes swept before it starts listening. The lock comes first and lives as
        // long as this connection: it is what tells a peer's sweep that the queue has a live owner.
        await using (var own = new SqlCommand("EXEC sp_getapplock @Resource = @queue, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;", conn))
        {
            own.Parameters.Add("@queue", System.Data.SqlDbType.NVarChar, 255).Value = _ownQueueValue;
            await own.ExecuteNonQueryAsync(ct);
        }

        await ExecuteAsync(conn, CreateOwnQueueSql(), ct);
        await SweepStaleQueuesAsync(conn, ct);
        var lastSweep = System.Diagnostics.Stopwatch.GetTimestamp();

        // What a 7.7 process sends reaches this release through the relay, started once this process's queue
        // exists so a relayed wake reaches it too (the bridge, REMOVE in the release after 8.x).
        using var relayStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var relay = RelayLegacyQueueAsync(relayStop.Token);

        // Service Broker setup is the slow part and this process's queue now exists. From this point a
        // publish reaches it and the WAITFOR loop is the listener. Tests use this signal to gate the first
        // publish so it can't be dropped by a host-startup vs. listener-startup race. It latches once, and
        // reconnects rely on the listener task's drain-on-reconnect to catch up.
        _listenerReady.TrySetResult();

        // SQL Server 2025 ignores a cancel that reaches a WAITFOR (RECEIVE) as it starts until the WAITFOR times
        // out (measured: the full 30 s; 2022 stops at once), and a second cancel does not help. A wake sent into
        // this process's own queue ends that WAITFOR whatever state it is in. Sent only on stop, off the cancelling
        // thread, and awaited before the queue is dropped.
        Task? ownWake = null;
        var wakeOnStop = ct.Register(() => ownWake = Task.Run(() => SendWakeAsync(_ownServiceValue), CancellationToken.None));

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
                            // A wake (SendWakeAsync) carries no body: it only ends the WAITFOR.
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

                if (System.Diagnostics.Stopwatch.GetElapsedTime(lastSweep) >= SweepInterval)
                {
                    await SweepStaleQueuesAsync(conn, ct);
                    lastSweep = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }
        }
        finally
        {
            await relayStop.CancelAsync();
            await relay;

            // Disposing waits for a callback already running, so a wake it started is visible here. The wake must
            // land before the queue is dropped, or its send would fail on a missing service.
            await wakeOnStop.DisposeAsync();
            if (ownWake != null)
            {
                await ownWake;
            }

            await DropOwnQueueAsync();
        }
    }

    // The rule 2.9 bridge's receiving half (REMOVE in the release after 8.x): while 7.7's shared queue exists, receive
    // from it on a connection of its own and re-send what a 7.7 process sent to every listener's service. A queue
    // hands each message to one receiver, so of several 8.0 listeners draining it concurrently one takes each message
    // and re-sends it to all of them, itself included. Never throws: a failure is logged and retried later.
    private async Task RelayLegacyQueueAsync(CancellationToken ct)
    {
        // The WAITFOR on the shared queue has the SQL Server 2025 cancel problem of the listener's own (ListenAsync),
        // so a stop wakes the shared queue too, but only while this relay is waiting on it.
        var waiting = new StrongBox<int>();
        Task? wake = null;
        var wakeOnStop = ct.Register(() => wake = Task.Run(() => WakeLegacyRelayAsync(waiting), CancellationToken.None));
        SqlConnection? conn = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (conn == null)
                    {
                        conn = new SqlConnection(_connectionString);
                        await conn.OpenAsync(ct);
                    }

                    if (await LegacyQueueExistsAsync(conn, ct))
                    {
                        await RelayLegacyBatchAsync(conn, waiting, ct);
                        continue;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // The shared queue dropped mid-receive (its last 7.7 process gone and an operator cleaning up),
                    // or a lost connection: open a fresh one after the interval.
                    _logger?.LogDebug(ex, "Relaying 7.7's shared Service Broker queue for channel {Channel} failed; retrying", _channelName);
                    if (conn != null)
                    {
                        await conn.DisposeAsync();
                        conn = null;
                    }
                }

                try
                {
                    await Task.Delay(LegacyQueueCheckInterval, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        finally
        {
            await wakeOnStop.DisposeAsync();
            if (wake != null)
            {
                await wake;
            }

            if (conn != null)
            {
                await conn.DisposeAsync();
            }
        }
    }

    // The shared queue hands each message to ONE receiver, and other relays and 7.7 listeners receive from it too, so
    // one wake may end someone else's WAITFOR instead (harmless: they discard it). Wakes are sent until this relay's
    // wait has ended, at most MaxRelayWakes of them; past that the WAITFOR ends at its own timeout.
    private async Task WakeLegacyRelayAsync(StrongBox<int> waiting)
    {
        for (var attempt = 0; attempt < MaxRelayWakes && Volatile.Read(ref waiting.Value) == 1; attempt++)
        {
            await SendWakeAsync(_legacyServiceValue);
            await Task.Delay(RelayWakeInterval);
        }
    }

    // One body-less message to a service, from this release's sender service: it ends a WAITFOR (RECEIVE) on the
    // service's queue and is never a notification. The listener skips a message without a body, the relay keeps only
    // bodies a 7.7 process sent (a wake has neither), and 7.7 skips a message without a body too. A body-less message
    // of the existing type rather than a type of its own, because a new type needs a contract on 7.7's shared service.
    // Best effort and never throws: it runs from a cancellation callback.
    internal async Task SendWakeAsync(string targetService)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(timeout.Token);
            var sql = $@"
                DECLARE @conv UNIQUEIDENTIFIER;
                BEGIN DIALOG @conv
                    FROM SERVICE [{_serviceValue}]
                    TO SERVICE @target
                    ON CONTRACT [{_contractValue}]
                    WITH ENCRYPTION = OFF, LIFETIME = 60;
                SEND ON CONVERSATION @conv MESSAGE TYPE [{_messageTypeValue}];
                END CONVERSATION @conv;";
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@target", System.Data.SqlDbType.NVarChar, 256).Value = targetService;
            await cmd.ExecuteNonQueryAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Waking the Service Broker service {Service} on stop failed; its WAITFOR ends at its timeout", targetService);
        }
    }

    private async Task<bool> LegacyQueueExistsAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.service_queues WHERE name = @queue;", conn);
        cmd.Parameters.Add("@queue", System.Data.SqlDbType.NVarChar, 256).Value = _legacyQueueValue;

        return (int)(await cmd.ExecuteScalarAsync(ct))! > 0;
    }

    // One WAITFOR on the shared queue, then in the same batch: keep only what a 7.7 process sent (its conversation's
    // far service is the shared service; what this release sent there comes from its sender service and already
    // reached every listener directly, so it is dropped, never delivered twice), end every conversation received,
    // and send the kept messages to every listener's service, one conversation each. Never to the shared service.
    private async Task RelayLegacyBatchAsync(SqlConnection conn, StrongBox<int> waiting, CancellationToken ct)
    {
        var sql = $@"
            DECLARE @received TABLE (handle UNIQUEIDENTIFIER, type_name NVARCHAR(256), body VARBINARY(MAX));
            WAITFOR (
                RECEIVE TOP(10)
                    conversation_handle,
                    message_type_name,
                    message_body
                FROM [{_legacyQueueValue}]
                INTO @received
            ), TIMEOUT 30000;

            DECLARE @relay TABLE (id INT IDENTITY(1, 1), body VARBINARY(MAX));
            INSERT INTO @relay (body)
            SELECT r.body
            FROM @received r
            JOIN sys.conversation_endpoints e ON e.conversation_handle = r.handle
            WHERE r.type_name = @type AND r.body IS NOT NULL AND e.far_service = @legacy;

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

            IF EXISTS (SELECT 1 FROM @relay)
            BEGIN
                DECLARE @target NVARCHAR(256), @conv UNIQUEIDENTIFIER, @id INT, @body VARBINARY(MAX);
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
                    SET @id = 0;
                    WHILE 1 = 1
                    BEGIN
                        SELECT TOP (1) @id = id, @body = body FROM @relay WHERE id > @id ORDER BY id;
                        IF @@ROWCOUNT = 0
                            BREAK;
                        SEND ON CONVERSATION @conv
                            MESSAGE TYPE [{_messageTypeValue}] (@body);
                    END
                    END CONVERSATION @conv;
                    FETCH NEXT FROM targets INTO @target;
                END
                CLOSE targets;
                DEALLOCATE targets;
            END";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        cmd.Parameters.Add("@type", System.Data.SqlDbType.NVarChar, 256).Value = _messageTypeValue;
        cmd.Parameters.Add("@legacy", System.Data.SqlDbType.NVarChar, 256).Value = _legacyServiceValue;
        cmd.Parameters.Add("@prefix", System.Data.SqlDbType.NVarChar, 256).Value = _listenerServicePrefix;

        // Marked waiting BEFORE the token is checked, so a stop either sees the mark and wakes, or is seen here.
        Volatile.Write(ref waiting.Value, 1);
        try
        {
            ct.ThrowIfCancellationRequested();
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            Volatile.Write(ref waiting.Value, 0);
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
    // whose owner's session lock is free (APPLOCK_TEST = 1: its listening connection is gone) and whose
    // oldest message has waited longer than StaleQueueAge. Best effort: a failure is logged and the next
    // sweep tries again.
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
                    AND APPLOCK_TEST('public', @queue, 'Exclusive', 'Session') = 1
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
