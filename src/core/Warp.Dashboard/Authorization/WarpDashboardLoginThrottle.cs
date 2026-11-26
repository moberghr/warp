using System.Net;
using System.Net.Sockets;

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

    // Records an attempt for ip and username. Returns how long the caller must wait when either is locked out, or
    // null when it may proceed.
    public TimeSpan? TryBegin(string ip, string username)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var now = _time.GetUtcNow();

        // Look at both lockouts before counting anything, so an attempt refused for its username does not also
        // charge (and escalate) its IP, which behind a NAT is everyone's.
        var wait = Longer(_byIp.LockedFor(ip, now), _byUser.LockedFor(username, now));
        if (wait != null)
        {
            return wait;
        }

        // Each count re-checks its own lockout under the table's lock, so a burst of parallel attempts cannot all
        // pass the look above and then count past the limit.
        return _byIp.TryCount(ip, now) ?? _byUser.TryCount(username, now);
    }

    // The IP key. An IPv6 caller is keyed by its /64, which is what one subscriber is usually given: keying by the
    // full address would hand anyone with a /64 2^64 separate budgets. An IPv4-mapped address is keyed as IPv4.
    public static string IpKey(IPAddress? address)
    {
        if (address == null)
        {
            return string.Empty;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);

        return new IPAddress(bytes) + "/64";
    }

    // A success clears the username's count but only takes back this attempt from the IP's: wiping the IP count
    // would let anyone with one valid account reset their address's budget between guesses at other usernames.
    public void Succeeded(string ip, string username)
    {
        if (!_options.Enabled)
        {
            return;
        }

        _byIp.Refund(ip, _time.GetUtcNow());
        _byUser.Reset(username);
    }

    private static TimeSpan? Longer(TimeSpan? a, TimeSpan? b)
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
        private DateTimeOffset _noRoomUntil;

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

        // Takes back one counted attempt. If that attempt is what reached the limit, the lockout it set goes with
        // it; an earlier lockout had already expired, or the attempt would not have been counted.
        public void Refund(string key, DateTimeOffset now)
        {
            lock (_entries)
            {
                if (!_entries.TryGetValue(key, out var entry) || entry.Attempts == 0)
                {
                    return;
                }

                entry.Attempts--;
                if (entry.LockedUntil > now)
                {
                    entry.LockedUntil = now;
                }
            }
        }

        // Returns how long the key is still locked out (and does not count the attempt), or null after counting it.
        public TimeSpan? TryCount(string key, DateTimeOffset now)
        {
            lock (_entries)
            {
                if (_entries.TryGetValue(key, out var entry) && entry.LockedUntil > now)
                {
                    return entry.LockedUntil - now;
                }

                if (entry == null || IsForgotten(entry, now))
                {
                    // No room means the table is full of keys that are all locked out. The new key then goes
                    // untracked rather than refused: refusing would let an attacker who filled the table shut the
                    // login for everyone, and evicting a lockout would let them clear one. The other table (IP or
                    // username) still throttles the attempt.
                    if (entry == null && !MakeRoom(now))
                    {
                        return null;
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

                return null;
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
        // spraying new usernames. If every key is locked out there is no room (see TryCount).
        private bool MakeRoom(DateTimeOffset now)
        {
            var max = _options.MaxTrackedKeys <= 0 ? 10_000 : _options.MaxTrackedKeys;
            if (_entries.Count < max)
            {
                return true;
            }

            // A full table where every key was locked out frees nothing until the first of those lockouts ends; do
            // not pay the pass for every new caller in between.
            if (now < _noRoomUntil)
            {
                return false;
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

            if (_entries.Count < max)
            {
                return true;
            }

            _noRoomUntil = _entries.Values.Min(x => x.LockedUntil);

            return false;
        }
    }

    private sealed class Entry
    {
        public int Attempts { get; set; }

        public DateTimeOffset LastAttempt { get; set; }

        public DateTimeOffset LockedUntil { get; set; }
    }
}
