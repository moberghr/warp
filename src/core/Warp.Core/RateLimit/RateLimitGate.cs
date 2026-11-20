using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Warp.Core.RateLimit;

/// <summary>
/// Per-process memory of rate-limit rejections, so a key already known to be exhausted is refused
/// without the advisory lock, the override lookup and the bucket read, and so throttled Wait-mode jobs
/// are handed distinct future slots instead of all being rescheduled onto the same instant.
/// <para>
/// <b>Why caching a rejection is safe across servers:</b> every evaluation computes the instant the key
/// can next admit, given the bucket as it stood. Other processes can only CONSUME capacity, never return
/// it, so that instant is a lower bound on every server — before it, the authoritative check would refuse
/// too. The cache therefore never refuses a job the database would have admitted. The two things that can
/// reopen a key early (an admin raising or removing its override) are bounded by
/// <see cref="MaxClosedAge"/>, and cleared immediately on the process that made the change.
/// </para>
/// <para>
/// <b>Slots</b> are reservations taken from the cluster-wide cursor on the bucket row
/// (<c>RateLimitBucket.NextSlotUtc</c>), leased in small blocks so most throttled jobs on a hot key
/// never touch the database. A lease covers at most <see cref="LeaseHorizon"/> of the key's capacity,
/// so slots a process leased but never used cost the cluster at most that much delay.
/// </para>
/// </summary>
internal sealed class RateLimitGate
{
    internal static readonly TimeSpan MaxClosedAge = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan LeaseHorizon = TimeSpan.FromSeconds(5);

    internal const int MaxLeaseSize = 20;

    // Opportunistic sweep threshold. Keys can be per-tenant and therefore unbounded, so entries that are
    // no longer closed and hold no usable slot are dropped once the map grows past this.
    private const int SweepThreshold = 1024;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _maxLeaseSize;

    public RateLimitGate()
        : this(MaxLeaseSize)
    {
    }

    // The DI constructor: the worker's activation cadence, when this process has a worker.
    public RateLimitGate(IEnumerable<IScheduledActivationCadence> cadence)
        : this(MaxLeaseSize, cadence.Select(x => x.Interval).DefaultIfEmpty(TimeSpan.Zero).Max())
    {
    }

    // A gate that leases <paramref name="maxLeaseSize"/> slots at most. The behaviour's public
    // constructor builds a private gate per instance with a lease of one — an instance-scoped gate
    // cannot hand leftover slots to the next job, so a larger lease would only waste them.
    internal RateLimitGate(int maxLeaseSize, TimeSpan activationInterval = default)
    {
        _maxLeaseSize = Math.Max(1, maxLeaseSize);
        ActivationInterval = activationInterval > TimeSpan.Zero ? activationInterval : TimeSpan.Zero;
    }

    // Slots closer together than this are released by one activation tick anyway. Zero when unknown.
    internal TimeSpan ActivationInterval { get; }

    internal int Count => _entries.Count;

    /// <summary>
    /// Refuses <paramref name="key"/> from memory when it is known to be exhausted at <paramref name="now"/>.
    /// For <see cref="RateLimitMode.Wait"/> a refusal also needs a slot to reschedule onto; when none is
    /// left the caller falls through to the authoritative path, which leases more.
    /// </summary>
    public bool TryRefuse(string key, RateLimitPolicy policy, RateLimitMode mode, DateTime now, out CachedRefusal refusal)
    {
        refusal = default;
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        lock (entry)
        {
            // A job whose declared shape differs from the one the rejection was recorded under is not
            // covered by it — let the authoritative path decide.
            if (entry.Policy != policy || now >= entry.ClosedUntil)
            {
                return false;
            }

            if (mode != RateLimitMode.Wait)
            {
                refusal = new CachedRefusal(entry.EffectiveCount, entry.EffectiveWindowSeconds, entry.NextAvailable);

                return true;
            }

            if (!TryTakeSlot(entry, entry.NextAvailable, out var slot))
            {
                return false;
            }

            refusal = new CachedRefusal(entry.EffectiveCount, entry.EffectiveWindowSeconds, slot);

            return true;
        }
    }

    /// <summary>Takes a slot this process already leased, when it is still usable at <paramref name="notBefore"/>.</summary>
    public bool TryTakeLeasedSlot(string key, DateTime notBefore, out DateTime slot)
    {
        slot = default;
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        lock (entry)
        {
            return TryTakeSlot(entry, notBefore, out slot);
        }
    }

    /// <summary>
    /// Records an authoritative rejection: <paramref name="key"/> cannot admit before
    /// <paramref name="nextAvailable"/>. <paramref name="leasedSlots"/> are the slots this process leased
    /// beyond the one the rejected job itself took, kept for the next throttled jobs on the key.
    /// </summary>
    public void RecordRejection(
        string key,
        RateLimitPolicy policy,
        int effectiveCount,
        int effectiveWindowSeconds,
        DateTime now,
        DateTime nextAvailable,
        IReadOnlyList<DateTime> leasedSlots)
    {
        var entry = _entries.GetOrAdd(key, static _ => new Entry());
        lock (entry)
        {
            // A rejection under a different shape (an override changed, or two contracts disagree about
            // the key's size) supersedes the old one, and its slots were spaced for the old shape.
            if (entry.Policy != policy || entry.EffectiveCount != effectiveCount || entry.EffectiveWindowSeconds != effectiveWindowSeconds)
            {
                entry.Slots.Clear();
            }

            entry.Policy = policy;
            entry.EffectiveCount = effectiveCount;
            entry.EffectiveWindowSeconds = effectiveWindowSeconds;
            entry.NextAvailable = nextAvailable;

            var cap = now + MaxClosedAge;
            entry.ClosedUntil = nextAvailable < cap ? nextAvailable : cap;

            foreach (var slot in leasedSlots)
            {
                entry.Slots.Enqueue(slot);
            }
        }

        if (_entries.Count > SweepThreshold)
        {
            Sweep(now);
        }
    }

    /// <summary>Forgets <paramref name="key"/> — an admin changed its limit, so the recorded rejection may no longer hold.</summary>
    public void Invalidate(string key) => _entries.TryRemove(key, out _);

    /// <summary>
    /// How many slots one lease takes: no more than <see cref="MaxLeaseSize"/>, no more than the key's
    /// capacity per window, and no more than <see cref="LeaseHorizon"/> worth of it — always at least one.
    /// </summary>
    public int LeaseSize(int count, TimeSpan spacing)
    {
        var withinHorizon = spacing <= TimeSpan.Zero ? count : (int)(LeaseHorizon.Ticks / spacing.Ticks);

        return Math.Max(1, Math.Min(Math.Min(_maxLeaseSize, count), withinHorizon));
    }

    private static bool TryTakeSlot(Entry entry, DateTime notBefore, out DateTime slot)
    {
        // Slots are handed out in order. One earlier than notBefore cannot admit — the key is known to be
        // exhausted until then — so rescheduling onto it would only buy another rejection; drop it.
        while (entry.Slots.TryDequeue(out slot))
        {
            if (slot >= notBefore)
            {
                return true;
            }
        }

        return false;
    }

    private void Sweep(DateTime now)
    {
        foreach (var (key, entry) in _entries)
        {
            lock (entry)
            {
                var hasUsableSlot = entry.Slots.Count > 0 && entry.Slots.Last() >= now;
                if (now >= entry.ClosedUntil && !hasUsableSlot)
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }
    }

    private sealed class Entry
    {
        public RateLimitPolicy Policy { get; set; }

        public int EffectiveCount { get; set; }

        public int EffectiveWindowSeconds { get; set; }

        public DateTime NextAvailable { get; set; }

        public DateTime ClosedUntil { get; set; }

        public Queue<DateTime> Slots { get; } = new();
    }
}

/// <summary>The rate-limit shape a job declared — what a cached rejection is only valid for.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct RateLimitPolicy(int Count, int WindowSeconds, RateLimitStyle Style);

/// <summary>A refusal decided from memory: the effective limit (for the log line) and when to reschedule.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct CachedRefusal(int EffectiveCount, int EffectiveWindowSeconds, DateTime ScheduleTo);
