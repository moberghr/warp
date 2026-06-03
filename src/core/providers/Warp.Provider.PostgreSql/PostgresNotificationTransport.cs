using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Npgsql;
using Warp.Core.Logging;
using Warp.Core.Notifications;

namespace Warp.Provider.PostgreSql;

/// <summary>
/// PostgreSQL LISTEN/NOTIFY transport. <see cref="PublishAsync"/> opens a short-lived
/// (pooled) connection and issues <c>SELECT pg_notify(...)</c>; <see cref="ListenAsync(CancellationToken)"/>
/// holds a dedicated long-lived connection, issues <c>LISTEN</c>, and yields each arriving
/// notification. Reconnect is the caller's responsibility — <see cref="ListenAsync(CancellationToken)"/>
/// terminates on connection failure or cancellation, and the hosting listener task wraps
/// it in an outer reconnect loop.
/// </summary>
public sealed class PostgresNotificationTransport : IWarpNotificationTransport
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly string? _connectionString;
    private readonly string _channelName;
    private readonly ILogger<PostgresNotificationTransport>? _logger;
    private readonly TaskCompletionSource _listenerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PostgresNotificationTransport(string connectionString, WarpDatabasePushConfiguration options, ILogger<PostgresNotificationTransport>? logger = null)
    {
        _connectionString = connectionString;
        _channelName = options.ChannelName;
        _logger = logger;
    }

    // Data-source overload — preferred when callers register an NpgsqlDataSource (Aspire,
    // Managed Identity, custom SSL/password providers): connections inherit the data
    // source's auth and encryption configuration rather than being opened from a raw
    // connection string that may be missing them.
    public PostgresNotificationTransport(NpgsqlDataSource dataSource, WarpDatabasePushConfiguration options, ILogger<PostgresNotificationTransport>? logger = null)
    {
        _dataSource = dataSource;
        _channelName = options.ChannelName;
        _logger = logger;
    }

    public Task ListenerReady => _listenerReady.Task;

    public async Task PublishAsync(NotificationKind kind, string? queue, CancellationToken ct)
    {
        try
        {
            var payload = Encode(kind, queue);
            await using var conn = await OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", conn);
            cmd.Parameters.AddWithValue("channel", _channelName);
            cmd.Parameters.AddWithValue("payload", payload);
            await cmd.ExecuteNonQueryAsync(ct);
            WarpTelemetry.NotificationsPublished.Add(1, new KeyValuePair<string, object?>("transport", "postgres"), new KeyValuePair<string, object?>("kind", kind.ToString()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Transport contract: PublishAsync must not throw because the originating transaction
            // is already durable. The failure counter lets operators alert on broken push; a
            // missed notification only delays pickup until the next listener reconnect-drain.
            WarpTelemetry.NotificationPublishFailures.Add(1, new KeyValuePair<string, object?>("transport", "postgres"), new KeyValuePair<string, object?>("kind", kind.ToString()));
            _logger?.LogWarning(ex, "PostgresNotificationTransport.PublishAsync failed (kind={Kind}, queue={Queue})", kind, queue);
        }
    }

    public async IAsyncEnumerable<Notification> ListenAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);

        // Channel name can't be parameterized — only identifiers, not strings. Quote-escape it.
        var listenSql = $"LISTEN \"{_channelName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        await using (var cmd = new NpgsqlCommand(listenSql, conn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // First-LISTEN-on-the-wire signal. Latches once; reconnects rely on the listener
        // task's drain-on-reconnect to catch up, not on re-arming this TCS.
        _listenerReady.TrySetResult();

        // Npgsql fires the Notification event synchronously during WaitAsync on the same thread
        // the iterator runs on — so a plain Queue is safe, no Channel/Task.Run needed. The event
        // handler enqueues; the loop below drains after each WaitAsync returns.
        var pending = new Queue<Notification>();
        conn.Notification += OnNotification;
        void OnNotification(object? sender, NpgsqlNotificationEventArgs e)
        {
            if (TryDecode(e.Payload, out var notification))
            {
                pending.Enqueue(notification);
            }
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await conn.WaitAsync(ct);
                while (pending.TryDequeue(out var notification))
                {
                    yield return notification;
                }
            }
        }
        finally
        {
            conn.Notification -= OnNotification;
        }
    }

    private async ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        if (_dataSource is not null)
        {
            return await _dataSource.OpenConnectionAsync(ct);
        }

        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        return conn;
    }

    // Payload grammar: "<kind>:<queue>?". Kind is one char: J=JobEnqueued, M=MessageEnqueued, F=JobFinalized.
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
