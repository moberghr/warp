using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;

namespace Warp.Worker.BackgroundServices;

/// <summary>
/// Per-instance singleton that buffers captured log entries and flushes them to the database
/// on a timer (~1s interval, same cadence as <c>JobLogCollector</c> / <c>RunningJobMonitor</c>).
/// Enforces a level filter, a rate cap (100 entries/sec → 10s drop window), and message
/// truncation at 4096 bytes.
/// </summary>
public sealed class BackgroundServiceLogCollector : IBackgroundServiceLogCollector, IAsyncDisposable, IDisposable
{
    private const int MaxMessageBytes = 4096;
    private const string TruncationSuffix = "…[truncated]";
    private const int RateCapPerSecond = 100;
    private static readonly TimeSpan DropWindowDuration = TimeSpan.FromSeconds(10);

    private readonly string _serviceName;
    private readonly Guid _serverId;
    private readonly LogLevel _minLogLevel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _collectorLogger;

    private readonly ConcurrentQueue<BackgroundServiceLog> _queue = new();

    private readonly Lock _rateLock = new();
    private int _entriesInCurrentWindow;
    private DateTime _windowStart;
    private bool _inDropMode;
    private DateTime _dropWindowEnd;
    private int _droppedCount;

    // Flush loop
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public BackgroundServiceLogCollector(
        string serviceName,
        Guid serverId,
        LogLevel minLogLevel,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger collectorLogger)
    {
        _serviceName = serviceName;
        _serverId = serverId;
        _minLogLevel = minLogLevel;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _collectorLogger = collectorLogger;

        _windowStart = timeProvider.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// Enqueues a log entry after applying the level filter, rate cap, and message truncation.
    /// Thread-safe; may be called from any thread.
    /// </summary>
    public void Enqueue(BackgroundServiceLogSource source, LogLevel level, string message, Exception? exception)
    {
        if (level < _minLogLevel)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        lock (_rateLock)
        {
            // Advance the one-second window.
            if ((now - _windowStart).TotalSeconds >= 1.0)
            {
                _entriesInCurrentWindow = 0;
                _windowStart = now;
            }

            if (_inDropMode)
            {
                if (now < _dropWindowEnd)
                {
                    // Still in drop window — count this dropped entry and bail.
                    _droppedCount++;
                    return;
                }

                // Drop window expired — emit summary and resume normal operation.
                var dropped = _droppedCount;
                _inDropMode = false;
                _droppedCount = 0;
                _entriesInCurrentWindow = 0;
                _windowStart = now;

                _queue.Enqueue(BuildEntry(
                    source: BackgroundServiceLogSource.Lifecycle,
                    level: LogLevel.Information,
                    message: $"log capture resumed; dropped {dropped} entries during rate limit",
                    exception: null,
                    timestamp: now));
            }

            _entriesInCurrentWindow++;

            if (_entriesInCurrentWindow > RateCapPerSecond)
            {
                // Enter drop mode — emit a warning and start the drop window.
                _inDropMode = true;
                _dropWindowEnd = now.Add(DropWindowDuration);
                _droppedCount = 1; // this current entry is dropped
                _entriesInCurrentWindow = 0;

                _queue.Enqueue(BuildEntry(
                    source: BackgroundServiceLogSource.Lifecycle,
                    level: LogLevel.Warning,
                    message: "log capture rate-limited; dropping entries",
                    exception: null,
                    timestamp: now));

                return;
            }
        }

        _queue.Enqueue(BuildEntry(source, level, message, exception, now));
    }

    /// <summary>
    /// Drains the in-memory queue and inserts all pending entries in a single batched write.
    /// Opens a scope per flush to avoid DbContext lifetime issues.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var entries = Drain();
        if (entries.Count == 0)
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IBackgroundServiceLogStore>();
            await store.InsertManyAsync(entries, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _collectorLogger.LogWarning(ex, "BackgroundServiceLogCollector: flush failed for service {ServiceName}", _serviceName);
        }
    }

    /// <summary>
    /// Starts the background flush loop. Must be called exactly once before log entries are
    /// expected to persist. The loop fires <see cref="FlushAsync"/> every
    /// <paramref name="flushInterval"/>.
    /// </summary>
    public void Start(TimeSpan flushInterval)
    {
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _loopTask = Task.Run(() => RunFlushLoopAsync(flushInterval, token), token);
    }

    /// <summary>
    /// Signals the flush loop to stop, performs a final flush of any pending entries, then
    /// returns. Safe to call multiple times.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        if (_loopCts != null)
        {
            await _loopCts.CancelAsync();
        }

        if (_loopTask != null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on stop.
            }
        }

        // Final drain — best effort; ignore ct here so the last entries get saved.
        await FlushAsync(CancellationToken.None);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _loopCts?.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
    }

    private async Task RunFlushLoopAsync(TimeSpan flushInterval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(flushInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await FlushAsync(ct);
        }
    }

    private List<BackgroundServiceLog> Drain()
    {
        var list = new List<BackgroundServiceLog>();
        while (_queue.TryDequeue(out var entry))
        {
            list.Add(entry);
        }

        return list;
    }

    private BackgroundServiceLog BuildEntry(
        BackgroundServiceLogSource source,
        LogLevel level,
        string message,
        Exception? exception,
        DateTime timestamp)
    {
        var truncatedMessage = TruncateUtf8(message);
        string? exceptionType = null;
        string? exceptionMessage = null;

        if (exception != null)
        {
            exceptionType = exception.GetType().FullName;
            exceptionMessage = TruncateUtf8(exception.ToString());
        }

        return new BackgroundServiceLog
        {
            ServerId = _serverId,
            ServiceName = _serviceName,
            Timestamp = timestamp,
            Level = level,
            Source = source,
            Message = truncatedMessage,
            ExceptionType = exceptionType,
            ExceptionMessage = exceptionMessage,
        };
    }

    private static string TruncateUtf8(string value)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(value) <= MaxMessageBytes)
        {
            return value;
        }

        // Binary-search for the longest prefix whose UTF-8 encoding fits within the budget
        // (minus the truncation suffix).
        var suffixBytes = System.Text.Encoding.UTF8.GetByteCount(TruncationSuffix);
        var budget = MaxMessageBytes - suffixBytes;

        var chars = value.AsSpan();
        var lo = 0;
        var hi = chars.Length;

        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (System.Text.Encoding.UTF8.GetByteCount(chars[..mid]) <= budget)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return string.Concat(value.AsSpan(0, lo), TruncationSuffix);
    }
}
