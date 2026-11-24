using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Metrics;
using Warp.Core.Services;

namespace Warp.Worker.Services;

/// <summary>
/// Compacts and downsamples the custom-metric <see cref="MetricPoint"/> rows the per-process <c>MetricFlusher</c>
/// writes. Each tick runs three passes in order, sharing one budget of source rows:
/// <list type="number">
/// <item><b>Compact</b> — the partial fine rows of a closed bucket (<c>BucketStart + FineResolutionMinutes +
/// 2 × MetricFlushInterval &lt; now</c>) become one <c>Compacted</c> row per series: summed count, sum and bucket
/// counts, min of mins, max of maxes.</item>
/// <item><b>Fine → hourly</b> — compacted fine rows older than <c>FineResolutionRetention</c> are added into their
/// hourly row.</item>
/// <item><b>Hourly → daily</b> — hourly rows older than <c>HourlyStatisticsRetention</c> are added into their daily
/// row, then daily rows older than <c>DailyStatisticsRetention</c> are pruned (never when it is null).</item>
/// </list>
/// <para>
/// The series key is (Instrument, Application, Version, Tags, BucketStart, Bounds), so rows with different bucket
/// bounds never merge, and neither do rows of different application versions (a host that opted into
/// <c>ByVersion()</c>; Version is null otherwise, which keys exactly as before). Keys are matched in memory with ordinal comparison and every write targets rows by primary key, so
/// a case-insensitive database collation can only widen a candidate fetch, never merge or drop a row.
/// </para>
/// <para>
/// Every pass saves its sums before it deletes its sources, by id, inside the task host's lock transaction (§2.3),
/// so a crash mid-tick rolls back whole and nothing is lost or counted twice. Each pass re-reads the database after
/// the previous one saved, so a row one pass updated is read with its new value by the next — the same row is never
/// updated from a stale snapshot and then deleted.
/// </para>
/// <para>
/// A row with a malformed or mismatched <c>Buckets</c> CSV never stops a tick: its count, sum, min and max are rolled
/// up, its bucket detail is dropped, and it is counted on <c>warp.metrics.collapsed{reason=error}</c> with a Warning
/// naming only the row id and instrument. The same holds when a summed CSV would not fit the column.
/// </para>
/// </summary>
public sealed class MetricRollup<TContext> : IServerTask
    where TContext : DbContext
{
    // The bound on source rows ONE tick may consume, shared by all three passes, for the reason recorded against
    // StatisticRollup's cap (§6.2.1): LocksWithTransaction defaults to true, so the whole of ExecuteAsync runs in one
    // transaction, and an unbounded pass holds it open — pinning the vacuum horizon — until the work runs out. A
    // capped pass DEFERS the remainder (sources are deleted by the ids actually summed, never by predicate), and
    // RerunImmediately drains a backlog across consecutive short ticks. Do NOT remove it to "catch up faster".
    internal const int MaxMetricRollupRowsPerTick = 20_000;

    private readonly DbContext _context;
    private readonly WarpServerConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MetricRollup<TContext>> _logger;

    public MetricRollup(
        IWarpServerContext serverContext,
        IOptions<WarpServerConfiguration> configuration,
        [FromKeyedServices(DatabaseClock.Key)] TimeProvider timeProvider,
        ILogger<MetricRollup<TContext>> logger)
    {
        _context = serverContext.Context;
        _configuration = configuration.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string Name => "RollupMetrics";

    public string? LockKey => "warp:metric-rollup";

    public TimeSpan? DefaultInterval => _configuration.MetricRollupInterval;

    // The scheduler re-runs only when ExecuteAsync returned a message, and this returns null when it rolled
    // nothing, so an idle rollup does not spin.
    public bool RerunImmediately => true;

    public bool LogOnSuccess => false;

    /// <summary>Test seam: the per-tick source-row budget. Production always uses <see cref="MaxMetricRollupRowsPerTick"/>.</summary>
    internal int MaxRowsPerTick { get; init; } = MaxMetricRollupRowsPerTick;

    /// <summary>Test seam: rows whose bucket detail was dropped as malformed by this instance.</summary>
    internal int MalformedBucketRows { get; private set; }

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var budget = MaxRowsPerTick;

        var compacted = await CompactAsync(now, budget, ct);
        budget -= compacted.Consumed;

        // Only compacted fine rows roll, and only once compaction has caught up: a partial row is never rolled into
        // its hourly row while a sibling partial of the same series is still waiting to be compacted. The shared
        // budget already implies this (compaction either drained or used the whole budget); the check keeps the
        // contract explicit.
        var toHourly = compacted.CaughtUp && budget > 0
            ? await RollFineToHourlyAsync(now, budget, ct)
            : 0;
        budget -= toHourly;

        var (toDaily, pruned) = budget > 0
            ? await RollHourlyToDailyAsync(now, budget, ct)
            : (0, 0);

        if (compacted.Consumed == 0 && toHourly == 0 && toDaily == 0 && pruned == 0)
        {
            return null;
        }

        return $"Compacted {compacted.Consumed}, rolled {toHourly} to hourly and {toDaily} to daily, pruned {pruned} metric point(s)";
    }

    private async Task<(int Consumed, bool CaughtUp)> CompactAsync(DateTime now, int budget, CancellationToken ct)
    {
        var closedCutoff = now
            - TimeSpan.FromMinutes(_configuration.FineResolutionMinutes)
            - (2 * _configuration.MetricFlushInterval);

        var sources = await _context.Set<MetricPoint>()
            .AsNoTracking()
            .Where(x => x.Tier == MetricTier.Fine)
            .Where(x => !x.Compacted)
            .Where(x => x.BucketStart < closedCutoff)
            .OrderBy(x => x.BucketStart)
            .ThenBy(x => x.Id)
            .Take(budget)
            .ToListAsync(ct);

        if (sources.Count == 0)
        {
            return (0, true);
        }

        var groups = sources
            .GroupBy(x => SeriesKey.Of(x, x.BucketStart))
            .ToList();

        var existing = await LoadTargetsAsync(MetricTier.Fine, groups.Select(x => x.Key), compactedOnly: true, ct);

        var flipIds = new List<long>();
        var deleteIds = new List<long>();
        var bucketSums = new BucketSums(ReportMalformed);
        foreach (var group in groups)
        {
            if (existing.TryGetValue(group.Key, out var target))
            {
                foreach (var source in group)
                {
                    Accumulate(target, source, bucketSums);
                    deleteIds.Add(source.Id);
                }

                continue;
            }

            var first = group.First();
            if (group.Skip(1).Any())
            {
                var row = NewTarget(group.Key, MetricTier.Fine);
                foreach (var source in group)
                {
                    Accumulate(row, source, bucketSums);
                    deleteIds.Add(source.Id);
                }

                _context.Set<MetricPoint>().Add(row);

                continue;
            }

            // A series whose closed bucket has a single partial row is already compact: flip it in place.
            flipIds.Add(first.Id);
        }

        bucketSums.Apply();
        await _context.SaveChangesAsync(ct);

        if (flipIds.Count > 0)
        {
            await _context.Set<MetricPoint>()
                .Where(x => flipIds.Contains(x.Id))
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Compacted, true), ct);
        }

        await DeleteByIdAsync(deleteIds, ct);

        return (sources.Count, sources.Count < budget);
    }

    private async Task<int> RollFineToHourlyAsync(DateTime now, int budget, CancellationToken ct)
    {
        var fineCutoff = now - _configuration.FineResolutionRetention;

        var sources = await _context.Set<MetricPoint>()
            .AsNoTracking()
            .Where(x => x.Tier == MetricTier.Fine)
            .Where(x => x.Compacted)
            .Where(x => x.BucketStart < fineCutoff)
            .OrderBy(x => x.BucketStart)
            .ThenBy(x => x.Id)
            .Take(budget)
            .ToListAsync(ct);

        if (sources.Count == 0)
        {
            return 0;
        }

        await RollIntoAsync(sources, MetricTier.Hourly, ct);

        return sources.Count;
    }

    private async Task<(int Rolled, int Pruned)> RollHourlyToDailyAsync(DateTime now, int budget, CancellationToken ct)
    {
        var hourlyCutoff = now - _configuration.HourlyStatisticsRetention;
        var dailyCutoff = _configuration.DailyStatisticsRetention is { } daily ? now - daily : (DateTime?)null;
        var fineMinutes = _configuration.FineResolutionMinutes;

        // Hourly and daily rows are always Compacted. Saying so lets both scans seek the (Tier, Compacted,
        // BucketStart) index by an exact prefix instead of skipping its second column.
        var sources = await _context.Set<MetricPoint>()
            .AsNoTracking()
            .Where(x => x.Tier == MetricTier.Hourly)
            .Where(x => x.Compacted)
            .Where(x => x.BucketStart < hourlyCutoff)
            .OrderBy(x => x.BucketStart)
            .ThenBy(x => x.Id)
            .Take(budget)
            .ToListAsync(ct);

        // An hourly row whose day is itself past daily retention is pruned directly rather than rolled into a daily
        // row this same pass would then delete: it is past retention either way.
        var expiredIds = dailyCutoff is { } hourlyDailyCutoff
            ? sources
                .Where(x => MetricTiers.BucketStart(MetricTier.Daily, x.BucketStart, fineMinutes) < hourlyDailyCutoff)
                .Select(x => x.Id)
                .ToHashSet()
            : [];

        var rollable = sources
            .Where(x => !expiredIds.Contains(x.Id))
            .ToList();

        if (rollable.Count > 0)
        {
            await RollIntoAsync(rollable, MetricTier.Daily, ct);
        }

        await DeleteByIdAsync([.. expiredIds], ct);

        var pruned = expiredIds.Count;
        var remaining = budget - sources.Count;
        if (dailyCutoff is { } dc && remaining > 0)
        {
            var dailyIds = await _context.Set<MetricPoint>()
                .Where(x => x.Tier == MetricTier.Daily)
                .Where(x => x.Compacted)
                .Where(x => x.BucketStart < dc)
                .OrderBy(x => x.BucketStart)
                .ThenBy(x => x.Id)
                .Select(x => x.Id)
                .Take(remaining)
                .ToListAsync(ct);

            await DeleteByIdAsync(dailyIds, ct);
            pruned += dailyIds.Count;
        }

        return (rollable.Count, pruned);
    }

    // Adds every source into its parent row of the target tier (inserting the parent when absent), saves, and only
    // then deletes exactly the sources it summed.
    private async Task RollIntoAsync(List<MetricPoint> sources, MetricTier targetTier, CancellationToken ct)
    {
        var fineMinutes = _configuration.FineResolutionMinutes;
        var groups = sources
            .GroupBy(x => SeriesKey.Of(x, MetricTiers.BucketStart(targetTier, x.BucketStart, fineMinutes)))
            .ToList();

        var existing = await LoadTargetsAsync(targetTier, groups.Select(x => x.Key), compactedOnly: false, ct);

        var bucketSums = new BucketSums(ReportMalformed);
        foreach (var group in groups)
        {
            if (!existing.TryGetValue(group.Key, out var target))
            {
                target = NewTarget(group.Key, targetTier);
                _context.Set<MetricPoint>().Add(target);
            }

            foreach (var source in group)
            {
                Accumulate(target, source, bucketSums);
            }
        }

        bucketSums.Apply();
        await _context.SaveChangesAsync(ct);

        await DeleteByIdAsync([.. sources.Select(x => x.Id)], ct);
    }

    // The database filter only narrows the candidates; the series match is ordinal and in memory, so the parent a
    // source lands in never depends on the database collation.
    private async Task<Dictionary<SeriesKey, MetricPoint>> LoadTargetsAsync(
        MetricTier tier,
        IEnumerable<SeriesKey> keys,
        bool compactedOnly,
        CancellationToken ct)
    {
        var wanted = keys.ToHashSet();
        var instruments = wanted
            .Select(x => x.Instrument)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var buckets = wanted
            .Select(x => x.BucketStart)
            .Distinct()
            .ToList();

        var query = _context.Set<MetricPoint>()
            .Where(x => x.Tier == tier)
            .Where(x => buckets.Contains(x.BucketStart))
            .Where(x => instruments.Contains(x.Instrument));

        if (compactedOnly)
        {
            query = query.Where(x => x.Compacted);
        }

        var candidates = await query
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

        var targets = new Dictionary<SeriesKey, MetricPoint>();
        foreach (var candidate in candidates)
        {
            var key = SeriesKey.Of(candidate, candidate.BucketStart);
            if (wanted.Contains(key))
            {
                targets.TryAdd(key, candidate);
            }
        }

        return targets;
    }

    private async Task DeleteByIdAsync(List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return;
        }

        await _context.Set<MetricPoint>()
            .Where(x => ids.Contains(x.Id))
            .ExecuteDeleteAsync(ct);
    }

    private static MetricPoint NewTarget(SeriesKey key, MetricTier tier) => new()
    {
        Instrument = key.Instrument,
        Application = key.Application,
        Version = key.Version,
        Tags = key.Tags,
        Tier = tier,
        BucketStart = key.BucketStart,
        Bounds = key.Bounds,
        Compacted = true,
    };

    // A fresh target starts at zero with no extremes or buckets, so the first source simply copies across. Count, sum
    // and extremes always add. Bucket detail goes through BucketSums and the shared tolerant codec: a row whose bucket
    // CSV does not parse, does not match its bounds (series with different bounds never meet here — Bounds is part of
    // the key), or whose sum would not fit the column, keeps its counts and loses only its bucket detail. It must never
    // throw: an exception here rolls back the task's lock transaction, and the same row is picked first again every
    // tick, so the rollup would stall for good.
    private static void Accumulate(MetricPoint target, MetricPoint source, BucketSums bucketSums)
    {
        target.Count += source.Count;
        target.Sum += source.Sum;
        target.Min = MetricBuckets.MinOf(target.Min, source.Min);
        target.Max = MetricBuckets.MaxOf(target.Max, source.Max);

        if (!string.IsNullOrEmpty(source.Buckets))
        {
            bucketSums.Add(target, source);
        }
    }

    // Counted on warp.metrics.collapsed{reason=error} and logged with the row id and instrument only (§1.2).
    private void ReportMalformed(MetricPoint row)
    {
        MalformedBucketRows++;
        MetricCardinalityGuard.Count(row.Instrument, MetricCardinalityGuard.ReasonError);
        _logger.LogWarning(
            "Metric point {Id} of instrument {Instrument} has bucket counts that do not parse, do not match its bounds, or would not fit once summed; its count, sum, min and max were rolled up and its bucket detail was dropped.",
            row.Id,
            row.Instrument);
    }

    private readonly record struct SeriesKey(string Instrument, string? Application, string? Version, string Tags, DateTime BucketStart, string? Bounds)
    {
        public static SeriesKey Of(MetricPoint point, DateTime bucketStart)
            => new(point.Instrument, point.Application, point.Version, point.Tags, bucketStart, point.Bounds);
    }

    // One pass's bucket detail, summed as counts per target and formatted to its CSV once, in Apply, before the pass
    // saves, rather than re-parsing and re-formatting the parent CSV for every source row.
    private sealed class BucketSums
    {
        private readonly Dictionary<MetricPoint, Pending> _pending = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int?> _expectedCounts = new(StringComparer.Ordinal);
        private readonly Action<MetricPoint> _reportMalformed;

        public BucketSums(Action<MetricPoint> reportMalformed)
        {
            _reportMalformed = reportMalformed;
        }

        // Adds a source carrying bucket detail. The target's own CSV is parsed once, on its first such source.
        public void Add(MetricPoint target, MetricPoint source)
        {
            if (!_pending.TryGetValue(target, out var pending))
            {
                pending = Load(target);
                _pending[target] = pending;
            }

            if (pending.Expected is { } expected
                && MetricBuckets.TryParseCounts(source.Buckets, expected, out var counts)
                && MetricBuckets.TryAddCounts(pending.Counts, counts, out var sums))
            {
                pending.Counts = sums;
                pending.Changed = true;

                return;
            }

            _reportMalformed(source);
        }

        // Writes each changed target's summed CSV; a target no source changed keeps its stored value untouched.
        public void Apply()
        {
            foreach (var (target, pending) in _pending)
            {
                if (pending.Changed)
                {
                    target.Buckets = pending.Counts is null ? null : MetricBuckets.FormatCounts(pending.Counts);
                }
            }
        }

        private Pending Load(MetricPoint target)
        {
            var expected = ExpectedBucketCount(target.Bounds);
            if (string.IsNullOrEmpty(target.Buckets))
            {
                return new Pending(expected, null);
            }

            if (expected is not null && MetricBuckets.TryParseCounts(target.Buckets, expected, out var counts))
            {
                return new Pending(expected, counts);
            }

            // A corrupt parent (an existing hourly or daily row) must not make every healthy source look corrupt.
            _reportMalformed(target);

            return new Pending(expected, null) { Changed = true };
        }

        // Bounds.Length + 1 (the overflow bucket), from the parsed bounds; null when the row has no usable bounds, so
        // any bucket detail it carries cannot be aligned. Cached per Bounds string.
        private int? ExpectedBucketCount(string? bounds)
        {
            if (string.IsNullOrEmpty(bounds))
            {
                return null;
            }

            if (!_expectedCounts.TryGetValue(bounds, out var expected))
            {
                expected = MetricBuckets.TryParseBounds(bounds, out var parsed) ? parsed.Length + 1 : null;
                _expectedCounts[bounds] = expected;
            }

            return expected;
        }

        private sealed class Pending
        {
            public Pending(int? expected, long[]? counts)
            {
                Expected = expected;
                Counts = counts;
            }

            public int? Expected { get; }

            public long[]? Counts { get; set; }

            public bool Changed { get; set; }
        }
    }
}
