using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Warp.Core.Services;

namespace Warp.Core.Metrics;

/// <summary>
/// In-memory aggregates of collected measurements, keyed by (series, fine bucket start). The fine bucket is
/// <c>WarpConfiguration.FineResolutionMinutes</c> wide, aligned like every other fine-tier metric (§8.30).
/// Pure in-memory work: no I/O, no EF type — storage drains it (B3).
/// <para>
/// <b>Lossless drain.</b> <see cref="Drain"/> takes entries key by key with <c>TryRemove</c>, the shape
/// <see cref="WarpCounterBuffer"/> proved conserves increments. An aggregate is a mutable object rather than a
/// value, so one more step is needed: a recorder may have fetched the aggregate just before it was removed.
/// Recording and draining therefore both take the aggregate's lock, and a drained aggregate is sealed — a
/// recorder that finds it sealed re-fetches, landing in a fresh entry the next drain takes. Each measurement
/// is in exactly one snapshot.
/// </para>
/// </summary>
internal sealed class MetricAggregator
{
    /// <summary>The default <see cref="WarpMetricsOptions.MaxPendingBuckets"/>.</summary>
    internal const int DefaultMaxPendingBuckets = 60_000;

    private readonly ConcurrentDictionary<AggregateKey, MetricAggregate> _aggregates = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _fineMinutes;
    private readonly int _maxPendingBuckets;

    public MetricAggregator(TimeProvider timeProvider, IOptions<WarpConfiguration> configuration)
        : this(timeProvider, configuration.Value.FineResolutionMinutes)
    {
    }

    public MetricAggregator(TimeProvider timeProvider, int fineResolutionMinutes, int maxPendingBuckets = DefaultMaxPendingBuckets)
    {
        _timeProvider = timeProvider;
        _fineMinutes = fineResolutionMinutes < 1 ? 1 : fineResolutionMinutes;
        _maxPendingBuckets = maxPendingBuckets < 1 ? 1 : maxPendingBuckets;
    }

    /// <summary>Distinct (series, bucket) aggregates currently held. Diagnostics only.</summary>
    public int PendingCount => _aggregates.Count;

    /// <summary>
    /// Test seam: invoked between fetching the aggregate and recording into it, the window a concurrent drain
    /// can seal it in. Null in production, where it costs one null check.
    /// </summary>
    internal Action<MetricAggregate>? AfterFetch { get; set; }

    /// <summary>
    /// Adds one measurement to its series' aggregate for the current fine bucket. Returns false — and records
    /// nothing — when the measurement's histogram shape does not match the aggregate's (an instrument name
    /// published with other bounds); the caller reports it.
    /// </summary>
    public bool Record(MetricSeries series, double value)
    {
        var bounds = series.Instrument.Bounds;
        var bucketIndex = bounds is null ? -1 : MetricBuckets.IndexOf(bounds, value);
        var key = new AggregateKey(series, CurrentBucket());

        while (true)
        {
            var aggregate = _aggregates.GetOrAdd(key, static x => new MetricAggregate(x.Series, x.BucketStart));
            AfterFetch?.Invoke(aggregate);

            var result = aggregate.TryRecord(value, bounds?.Length ?? -1, bucketIndex);
            if (result != MetricRecordResult.Sealed)
            {
                return result == MetricRecordResult.Recorded;
            }

            // Sealed by a concurrent drain after we fetched it: the entry is gone, so the retry creates a new one.
        }
    }

    /// <summary>Takes every aggregate held so far, leaving concurrent measurements for the next drain.</summary>
    public IReadOnlyList<MetricPointSnapshot> Drain()
    {
        if (_aggregates.IsEmpty)
        {
            return [];
        }

        var taken = new List<MetricPointSnapshot>();

        // The live enumerator is safe alongside concurrent writers and needs no key copy; TryRemove still takes each
        // entry exactly once, so an aggregate is never drained twice nor lost.
        foreach (var entry in _aggregates)
        {
            if (_aggregates.TryRemove(entry.Key, out var aggregate))
            {
                var snapshot = aggregate.Seal();
                if (snapshot.Count > 0)
                {
                    taken.Add(snapshot);
                }
            }
        }

        return taken;
    }

    /// <summary>
    /// Re-adds drained snapshots additively (count, sum and bucket counts summed; min and max widened). Storage
    /// calls it when a write fails, so the next drain writes them again.
    /// <para>
    /// Bounded: a snapshot whose (series, bucket) is not already pending is only re-added while fewer than
    /// <c>MaxPendingBuckets</c> aggregates are held. Snapshots are merged newest bucket first, so when the cap is
    /// reached it is the OLDEST buckets that are refused. Returns the refused snapshots, which the caller counts as
    /// dropped; a write that keeps failing therefore bounds memory instead of growing it for as long as it fails.
    /// </para>
    /// </summary>
    public IReadOnlyList<MetricPointSnapshot> MergeBack(IEnumerable<MetricPointSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        List<MetricPointSnapshot>? dropped = null;
        foreach (var snapshot in snapshots.OrderByDescending(x => x.BucketStart))
        {
            var key = new AggregateKey(snapshot.Series, snapshot.BucketStart);
            if (!_aggregates.ContainsKey(key) && _aggregates.Count >= _maxPendingBuckets)
            {
                dropped ??= [];
                dropped.Add(snapshot);

                continue;
            }

            while (true)
            {
                var aggregate = _aggregates.GetOrAdd(key, static x => new MetricAggregate(x.Series, x.BucketStart));
                if (aggregate.TryMerge(snapshot))
                {
                    break;
                }
            }
        }

        return dropped is null ? [] : dropped;
    }

    private DateTime CurrentBucket()
        => MetricTiers.BucketStart(MetricTier.Fine, _timeProvider.GetUtcNow().UtcDateTime, _fineMinutes);

    private readonly record struct AggregateKey(MetricSeries Series, DateTime BucketStart);
}

/// <summary>One (series, fine bucket) aggregate. All mutation happens under its own lock; see <see cref="MetricAggregator"/>.</summary>
internal sealed class MetricAggregate
{
    private readonly Lock _gate = new();
    private readonly long[]? _buckets;
    private readonly int _boundsLength;
    private long _count;
    private double _sum;
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;
    private bool _sealed;

    public MetricAggregate(MetricSeries series, DateTime bucketStart)
    {
        Series = series;
        BucketStart = bucketStart;

        var bounds = series.Instrument.Bounds;
        _boundsLength = bounds?.Length ?? -1;
        _buckets = bounds is null ? null : new long[bounds.Length + 1];
    }

    public MetricSeries Series { get; }

    public DateTime BucketStart { get; }

    /// <summary>
    /// Records one measurement. <paramref name="boundsLength"/> is the measurement's own bounds length (-1 for
    /// no histogram); a mismatch with this aggregate's is refused rather than indexed out of range.
    /// </summary>
    public MetricRecordResult TryRecord(double value, int boundsLength, int bucketIndex)
    {
        if (boundsLength != _boundsLength)
        {
            return MetricRecordResult.ShapeMismatch;
        }

        lock (_gate)
        {
            if (_sealed)
            {
                return MetricRecordResult.Sealed;
            }

            _count++;
            _sum += value;
            _min = Math.Min(_min, value);
            _max = Math.Max(_max, value);

            if (_buckets is not null && bucketIndex >= 0)
            {
                _buckets[bucketIndex]++;
            }

            return MetricRecordResult.Recorded;
        }
    }

    public bool TryMerge(MetricPointSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_sealed)
            {
                return false;
            }

            _count += snapshot.Count;
            _sum += snapshot.Sum;
            _min = Math.Min(_min, snapshot.Min);
            _max = Math.Max(_max, snapshot.Max);

            if (_buckets is not null && snapshot.Buckets is not null)
            {
                var length = Math.Min(_buckets.Length, snapshot.Buckets.Length);
                for (var i = 0; i < length; i++)
                {
                    _buckets[i] += snapshot.Buckets[i];
                }
            }

            return true;
        }
    }

    public MetricPointSnapshot Seal()
    {
        lock (_gate)
        {
            _sealed = true;

            return new MetricPointSnapshot(
                Series,
                BucketStart,
                _count,
                _sum,
                _count == 0 ? 0 : _min,
                _count == 0 ? 0 : _max,
                _buckets is null ? null : [.. _buckets]);
        }
    }
}

/// <summary>The outcome of <see cref="MetricAggregate.TryRecord"/>.</summary>
internal enum MetricRecordResult
{
    Recorded = 1,

    /// <summary>A drain sealed the aggregate; the recorder re-fetches.</summary>
    Sealed = 2,

    /// <summary>The measurement's histogram bounds differ from the aggregate's; it is dropped and reported.</summary>
    ShapeMismatch = 3,
}

/// <summary>
/// A drained (series, fine bucket) aggregate: count, sum, min, max and — for a histogram — one count per bound
/// plus the overflow bucket, against <c>Series.Instrument.Bounds</c>.
/// </summary>
internal sealed record MetricPointSnapshot(
    MetricSeries Series,
    DateTime BucketStart,
    long Count,
    double Sum,
    double Min,
    double Max,
    long[]? Buckets)
{
    public double[]? Bounds => Series.Instrument.Bounds;
}
