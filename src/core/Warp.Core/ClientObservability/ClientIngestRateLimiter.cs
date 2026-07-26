using System.Collections.Concurrent;

namespace Warp.Core.ClientObservability;

/// <summary>
/// Per-ingest-key fixed-window rate limiter for the public browser ingest endpoint (§8.27) — a spam guard, not
/// a precise limiter. Deliberately IN-MEMORY: the ingest path must never touch the database (that would defeat
/// the point of a lossy diagnostics firehose), so this is not the DB-backed cluster limiter. Per-process caps
/// are acceptable for abuse protection.
/// </summary>
public sealed class ClientIngestRateLimiter
{
    private readonly int _perMinute;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public ClientIngestRateLimiter(int perMinute, TimeProvider time)
    {
        _perMinute = perMinute <= 0 ? int.MaxValue : perMinute;
        _time = time;
    }

    /// <summary>Tries to admit <paramref name="count"/> events for <paramref name="key"/>; false once the per-minute cap is exceeded in the current window.</summary>
    public bool TryAcquire(string key, int count)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var window = _windows.GetOrAdd(key, _ => new Window());

        lock (window.Gate)
        {
            if (now - window.Start >= TimeSpan.FromMinutes(1))
            {
                window.Start = now;
                window.Count = 0;
            }

            if (window.Count + count > _perMinute)
            {
                return false;
            }

            window.Count += count;

            return true;
        }
    }

    private sealed class Window
    {
        public object Gate { get; } = new();

        public DateTime Start { get; set; }

        public int Count { get; set; }
    }
}
