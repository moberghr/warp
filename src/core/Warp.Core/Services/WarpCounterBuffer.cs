using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

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
    // A cell the drain took off the dictionary. No real total comes near it.
    private const long Retired = long.MinValue;

    // One mutable cell per key, updated with compare-and-swap. ConcurrentDictionary.AddOrUpdate takes the
    // key's bucket lock on every update, and every worker adds to the same few keys for every job: that
    // lock was 2% of a busy worker's CPU in contention alone.
    private readonly ConcurrentDictionary<string, StrongBox<long>> _pending = new(StringComparer.Ordinal);

    /// <summary>Distinct keys currently buffered (a drained key stays until a drain finds it idle). Diagnostics only.</summary>
    public int PendingKeyCount => _pending.Count;

    public void Add(string key, long value)
    {
        var spin = default(SpinWait);
        while (true)
        {
            var cell = _pending.GetOrAdd(key, static _ => new StrongBox<long>());
            var current = Volatile.Read(ref cell.Value);
            while (current != Retired)
            {
                var seen = Interlocked.CompareExchange(ref cell.Value, current + value, current);
                if (seen == current)
                {
                    return;
                }

                current = seen;
            }

            // A drain retired this cell after we looked it up and is removing it: look the key up again.
            spin.SpinOnce();
        }
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
    /// Each cell is emptied with a compare-and-swap, so a racing increment lands either before it (and is
    /// taken in this batch) or after it (and is taken in the next). Never neither. A cell found already
    /// empty, so idle since the last drain, is retired and removed so keys of past time buckets do not
    /// accumulate; an <see cref="Add"/> that still holds it sees it retired and starts again on a new cell.
    /// Swapping the whole dictionary instead does NOT conserve increments: an add that read the field
    /// before the swap lands in an instance already materialised (measured at 8.4% dropped under eight
    /// concurrent writers).
    /// </para>
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, long>> Drain()
    {
        if (_pending.IsEmpty)
        {
            return [];
        }

        var taken = new List<KeyValuePair<string, long>>();

        foreach (var (key, cell) in _pending)
        {
            var current = Volatile.Read(ref cell.Value);
            while (current != Retired)
            {
                var next = current == 0 ? Retired : 0;
                var seen = Interlocked.CompareExchange(ref cell.Value, next, current);
                if (seen != current)
                {
                    current = seen;
                    continue;
                }

                if (next == Retired)
                {
                    _pending.TryRemove(new KeyValuePair<string, StrongBox<long>>(key, cell));
                }
                else
                {
                    taken.Add(new KeyValuePair<string, long>(key, current));
                }

                break;
            }
        }

        return taken;
    }
}
