using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Warp.Core.Logging;
using Warp.Core.Notifications;

namespace Warp.Provider.SqlServer;

/// <summary>
/// SQL Server Service Broker transport. Sets up <c>ENABLE_BROKER</c> + message type / contract /
/// queue / service idempotently on first use. <see cref="PublishAsync"/> opens a short-lived
/// connection and does <c>BEGIN DIALOG</c>+<c>SEND</c>+<c>END CONVERSATION</c> to self;
/// <see cref="ListenAsync"/> holds a dedicated long-lived connection and loops
/// <c>WAITFOR (RECEIVE ...)</c> with a 30s timeout heartbeat.
/// </summary>
public sealed class SqlServerNotificationTransport : IWarpNotificationTransport
{
    private readonly string _connectionString;
    private readonly string _channelName;
    private readonly string _messageTypeValue;
    private readonly string _contractValue;
    private readonly string _queueValue;
    private readonly string _serviceValue;
    private readonly ILogger<SqlServerNotificationTransport>? _logger;

    // Lazy<Task> gives us the once-only "setup-or-fail-permanently" semantics for free:
    // first awaiter runs the setup, others wait; if it faults, every subsequent await throws
    // the cached exception without reopening any connections.
    private readonly Lazy<Task> _setup;
    private readonly TaskCompletionSource _listenerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ListenerReady => _listenerReady.Task;

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
        _queueValue = $"{_channelName}/queue";
        _serviceValue = $"{_channelName}/service";

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
            var sql = $@"
                DECLARE @conv UNIQUEIDENTIFIER;
                BEGIN DIALOG @conv
                    FROM SERVICE [{_serviceValue}]
                    TO SERVICE '{_serviceValue}'
                    ON CONTRACT [{_contractValue}]
                    WITH ENCRYPTION = OFF;
                SEND ON CONVERSATION @conv
                    MESSAGE TYPE [{_messageTypeValue}] (@payload);
                END CONVERSATION @conv;";

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@payload", System.Data.SqlDbType.VarBinary, payload.Length).Value = payload;
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

        // Service Broker setup is the slow part and the listen connection is now open — from
        // this point the WAITFOR loop is the listener. Tests use this signal to gate the first
        // publish so it can't be dropped by a host-startup vs. listener-startup race. Latches
        // once; reconnects rely on the listener task's drain-on-reconnect to catch up.
        _listenerReady.TrySetResult();

        // WAITFOR RECEIVE blocks the connection until messages arrive or the TIMEOUT fires.
        // The 30s timeout is a heartbeat so we can observe cancellation — not a poll of job state.
        // CommandTimeout is set slightly higher so the client doesn't time out before the server does.
        var sql = $@"
            WAITFOR (
                RECEIVE TOP(10)
                    message_type_name,
                    message_body
                FROM [{_queueValue}]
            ), TIMEOUT 30000;";

        while (!ct.IsCancellationRequested)
        {
            var batch = new List<Notification>();
            try
            {
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
                cmd.Parameters.Clear();
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
            // IF guard runs before the referenced type/contract exists in the first setup.
            var setupSql = $@"
                IF NOT EXISTS (SELECT 1 FROM sys.service_message_types WHERE name = N'{_messageTypeValue}')
                    EXEC('CREATE MESSAGE TYPE [{_messageTypeValue}] VALIDATION = NONE;');

                IF NOT EXISTS (SELECT 1 FROM sys.service_contracts WHERE name = N'{_contractValue}')
                    EXEC('CREATE CONTRACT [{_contractValue}] ([{_messageTypeValue}] SENT BY ANY);');

                IF NOT EXISTS (SELECT 1 FROM sys.service_queues WHERE name = N'{_queueValue}')
                    EXEC('CREATE QUEUE [{_queueValue}];');

                IF NOT EXISTS (SELECT 1 FROM sys.services WHERE name = N'{_serviceValue}')
                    EXEC('CREATE SERVICE [{_serviceValue}] ON QUEUE [{_queueValue}] ([{_contractValue}]);');";

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
    internal static string Encode(NotificationKind kind, string? queue)
    {
        return kind switch
        {
            NotificationKind.JobEnqueued => "J:" + (queue ?? "default"),
            NotificationKind.MessageEnqueued => "M:",
            NotificationKind.JobFinalized => "F:",
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
                notification = new Notification(NotificationKind.JobFinalized, null);
                return true;
            default:
                return false;
        }
    }
}
