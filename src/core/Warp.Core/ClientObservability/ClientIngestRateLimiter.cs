using System.Collections.Concurrent;

namespace Warp.Core.ClientObservability;

/// <summary>
/// Per-caller-IP fixed-window rate limiter for the public browser ingest endpoint (§8.27) — a spam guard, not
/// a precise limiter. Deliberately IN-MEMORY: the ingest path must never touch the database (that would defeat
/// the point of a lossy diagnostics firehose), so this is not the DB-backed cluster limiter.
/// <para>
/// The tracking table is itself bounded: keyed on caller IP (attacker-controlled on a public endpoint), it
/// could otherwise grow one entry per distinct source address forever (an IP rotation → OOM). At the
/// <c>maxTrackedKeys</c> cap it first prunes windows whose minute has elapsed; if still full it fails closed
/// (rate-limits the new caller) rather than leak memory — the same "trust nothing client-controlled to be
/// bounded" stance as the cardinality guard.
/// </para>
/// </summary>
public sealed class ClientIngestRateLimiter
{
    private const int DefaultMaxTrackedKeys = 100_000;

    private readonly int _perMinute;
    private readonly int _maxTrackedKeys;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public ClientIngestRateLimiter(int perMinute, TimeProvider time, int maxTrackedKeys = DefaultMaxTrackedKeys)
    {
        _perMinute = perMinute <= 0 ? int.MaxValue : perMinute;
        _maxTrackedKeys = maxTrackedKeys <= 0 ? DefaultMaxTrackedKeys : maxTrackedKeys;
        _time = time;
    }

    /// <summary>Tries to admit <paramref name="count"/> events for <paramref name="key"/>; false once the per-minute cap is exceeded in the current window, or when the (bounded) tracking table is full of live callers.</summary>
    public bool TryAcquire(string key, int count)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        // Bound the tracking table (a public endpoint keyed on attacker-controlled IPs must not grow forever).
        // Only pays the O(n) prune at the cap; if pruning frees nothing, fail closed rather than leak memory.
        if (!_windows.ContainsKey(key) && _windows.Count >= _maxTrackedKeys)
        {
            PruneExpired(now);

            if (_windows.Count >= _maxTrackedKeys)
            {
                return false;
            }
        }

        var window = _windows.GetOrAdd(key, _ => new Window { Start = now });

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

    private void PruneExpired(DateTime now)
    {
        foreach (var pair in _windows)
        {
            if (now - pair.Value.Start >= TimeSpan.FromMinutes(1))
            {
                _windows.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class Window
    {
        public object Gate { get; } = new();

        public DateTime Start { get; set; }

        public int Count { get; set; }
    }
}
