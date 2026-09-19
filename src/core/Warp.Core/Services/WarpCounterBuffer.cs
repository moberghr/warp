using System.Collections.Concurrent;

namespace Warp.Core.Services;

/// <summary>
/// Process-wide accumulator for <c>Counter</c> increments, so the hot path sums in memory instead of
/// writing a row per increment.
/// <para>
/// A finalizing job emits roughly twenty counter rows (per-type and per-handler jobstat totals,
/// durations and latency buckets, the state total, the reason breakdown, queue wait). For a uniform
/// workload those are the SAME twenty keys every time, so 100k jobs wrote two million rows that
/// <c>CounterAggregator</c> then folded back down to about twenty <c>Statistic</c> rows. Measured at
/// 100k jobs, that traffic — the inserts, the aggregator's reads, and the deletes — was 37% of all
/// database execution time.
/// </para>
/// <para>
/// <b>Durability:</b> increments live only in memory until the next flush, so an ungraceful process
/// exit loses whatever has not been flushed. This is a deliberate trade — counters are the write
/// -optimised side of the metrics fold (§6.2), diagnostics rather than an audit trail, and the same
/// stance the recording pipelines already take (§8.19). Anything that must survive a crash belongs
/// in a real row, not a counter. A graceful shutdown flushes.
/// </para>
/// <para>
/// Keys are bounded by construction: every one is built by a <c>*Keys</c> helper from a job type,
/// handler, queue, outcome token or duration bucket, all of which are already capped. The buffer
/// does not add a cardinality guard of its own because it cannot see anything the key builders did
/// not already bound.
/// </para>
/// </summary>
public sealed class WarpCounterBuffer
{
    private readonly ConcurrentDictionary<string, long> _pending = new(StringComparer.Ordinal);

    /// <summary>Distinct keys currently buffered. Diagnostics only.</summary>
    public int PendingKeyCount => _pending.Count;

    public void Add(string key, long value)
    {
        _pending.AddOrUpdate(key, value, (_, existing) => existing + value);
    }

    public void AddRange(IEnumerable<KeyValuePair<string, long>> increments)
    {
        foreach (var (key, value) in increments)
        {
            Add(key, value);
        }
    }

    /// <summary>
    /// Takes everything buffered so far, leaving concurrent increments for the next drain.
    /// <para>
    /// Drains key-by-key with <c>TryRemove</c>, which is the only shape that conserves increments.
    /// Swapping the dictionary does NOT: <see cref="Add"/> reads the field and then calls
    /// <c>AddOrUpdate</c> on the instance it read, so a swap landing between those two steps sends the
    /// increment into a dictionary that has already been materialised, and it is lost with no trace.
    /// Measured at 8.4% of increments dropped under eight concurrent writers.
    /// </para>
    /// <para>
    /// <c>TryRemove</c> is atomic with respect to <c>AddOrUpdate</c> on the same key, so a racing
    /// increment either lands before the removal (and is taken in this batch) or re-creates the key
    /// after it (and is taken in the next). Never neither.
    /// </para>
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, long>> Drain()
    {
        if (_pending.IsEmpty)
        {
            return [];
        }

        var taken = new List<KeyValuePair<string, long>>();

        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var value) && value != 0)
            {
                taken.Add(new KeyValuePair<string, long>(key, value));
            }
        }

        return taken;
    }
}
