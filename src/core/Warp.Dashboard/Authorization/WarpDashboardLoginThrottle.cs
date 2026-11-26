namespace Warp.Dashboard;

// In-memory, per-process brute-force guard for the built-in login. See WarpDashboardLoginThrottleOptions.
// An attempt is counted before the validator runs, not after it fails: counting on failure would let a burst
// of parallel requests all pass the check before the first failure lands. Success clears the count again.
internal sealed class WarpDashboardLoginThrottle
{
    private readonly WarpDashboardLoginThrottleOptions _options;
    private readonly TimeProvider _time;
    private readonly KeyTable _byIp;
    private readonly KeyTable _byUser;

    public WarpDashboardLoginThrottle(WarpDashboardLoginThrottleOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
        _byIp = new KeyTable(options, StringComparer.Ordinal);
        _byUser = new KeyTable(options, StringComparer.OrdinalIgnoreCase);
    }

    // Records an attempt for ip and username. Returns how long the caller
    // must wait when either is locked out (the attempt is then not counted), or null when it may proceed.
    public TimeSpan? TryBegin(string ip, string username)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var wait = Max(_byIp.LockedFor(ip, now), _byUser.LockedFor(username, now));

        if (wait != null)
        {
            return wait;
        }

        // A table full of keys that are all still locked out has nowhere to put a new one: refuse rather than
        // forget a lockout (see MakeRoom).
        if (!_byIp.Count(ip, now) || !_byUser.Count(username, now))
        {
            return _options.LockoutDuration;
        }

        return null;
    }

    public void Succeeded(string ip, string username)
    {
        _byIp.Reset(ip);
        _byUser.Reset(username);
    }

    private static TimeSpan? Max(TimeSpan? a, TimeSpan? b)
    {
        if (a == null)
        {
            return b;
        }

        return b == null || a >= b ? a : b;
    }

    private sealed class KeyTable
    {
        private readonly WarpDashboardLoginThrottleOptions _options;
        private readonly Dictionary<string, Entry> _entries;

        public KeyTable(WarpDashboardLoginThrottleOptions options, StringComparer comparer)
        {
            _options = options;
            _entries = new Dictionary<string, Entry>(comparer);
        }

        public TimeSpan? LockedFor(string key, DateTimeOffset now)
        {
            lock (_entries)
            {
                return _entries.TryGetValue(key, out var entry) && entry.LockedUntil > now ? entry.LockedUntil - now : null;
            }
        }

        public bool Count(string key, DateTimeOffset now)
        {
            lock (_entries)
            {
                if (!_entries.TryGetValue(key, out var entry) || IsForgotten(entry, now))
                {
                    if (entry == null && !MakeRoom(now))
                    {
                        return false;
                    }

                    entry = new Entry();
                    _entries[key] = entry;
                }

                entry.Attempts++;
                entry.LastAttempt = now;

                var over = entry.Attempts - _options.FreeAttempts;
                if (over >= 0)
                {
                    entry.LockedUntil = now + Lockout(over);
                }

                return true;
            }
        }

        public void Reset(string key)
        {
            lock (_entries)
            {
                _entries.Remove(key);
            }
        }

        // Doubling from LockoutDuration, capped. The shift is bounded so a long-running attack cannot overflow it.
        private TimeSpan Lockout(int over)
        {
            var factor = 1L << Math.Min(over, 20);
            var ticks = _options.LockoutDuration.Ticks > _options.MaxLockoutDuration.Ticks / factor
                ? _options.MaxLockoutDuration.Ticks
                : _options.LockoutDuration.Ticks * factor;

            return TimeSpan.FromTicks(Math.Min(ticks, _options.MaxLockoutDuration.Ticks));
        }

        private bool IsForgotten(Entry entry, DateTimeOffset now)
            => entry.LockedUntil <= now && now - entry.LastAttempt >= _options.ForgetAfter;

        // Keys are caller-controlled (any IP, any username), so the table must not grow without bound. When it is
        // full, drop what has been forgotten anyway, then the least recently used keys that are NOT locked out,
        // down to 90% of the cap so the O(n) pass is paid once per many new keys rather than on every one. A
        // locked-out key is never evicted: evicting it would let an attacker clear a username's lockout by
        // spraying new usernames. If every key is locked out there is no room, and the new key is refused.
        private bool MakeRoom(DateTimeOffset now)
        {
            var max = _options.MaxTrackedKeys <= 0 ? 10_000 : _options.MaxTrackedKeys;
            if (_entries.Count < max)
            {
                return true;
            }

            var target = max - Math.Max(1, max / 10);
            var evictable = _entries
                .Where(x => x.Value.LockedUntil <= now)
                .OrderBy(x => IsForgotten(x.Value, now) ? 0 : 1)
                .ThenBy(x => x.Value.LastAttempt)
                .Select(x => x.Key)
                .Take(_entries.Count - target)
                .ToList();

            foreach (var key in evictable)
            {
                _entries.Remove(key);
            }

            return _entries.Count < max;
        }
    }

    private sealed class Entry
    {
        public int Attempts { get; set; }

        public DateTimeOffset LastAttempt { get; set; }

        public DateTimeOffset LockedUntil { get; set; }
    }
}
