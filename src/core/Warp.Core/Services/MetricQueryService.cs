using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Models;
using Warp.Core.Slo;
using BucketMap = System.Collections.Generic.SortedDictionary<System.DateTime, Warp.Core.Services.MetricAccumulator>;
using ValueBuckets = System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.SortedDictionary<System.DateTime, Warp.Core.Services.MetricAccumulator>>;

namespace Warp.Core.Services;

/// <summary>
/// <see cref="IMetricQueryService"/> over the user's <typeparamref name="TContext"/>. Reads the
/// <see cref="MetricInstrument"/> registry for the list, and <see cref="MetricPoint"/> rows for a series: one
/// <c>AsNoTracking</c> statement on (instrument, every overlapping tier, bucket range[, application]) whose partial rows are summed
/// by key (in SQL for the scalar aggregates; in memory for histogram bucket CSVs, which SQL cannot add). Tag
/// filtering (the SLO evaluator's <see cref="MetricFilter"/>, so the page and an objective agree), breakdown,
/// percentile walks and compare overlays run in memory on that set. Registered by <c>AddWarp</c> itself so any
/// dashboard host resolves it.
/// </summary>
public sealed class MetricQueryService<TContext> : IMetricQueryService
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly IOptions<WarpConfiguration> _options;
    private readonly TimeProvider _timeProvider;

    public MetricQueryService(TContext context, IOptions<WarpConfiguration> options)
        : this(context, options, TimeProvider.System)
    {
    }

    public MetricQueryService(TContext context, IOptions<WarpConfiguration> options, TimeProvider timeProvider)
        : this((DbContext)context, options, timeProvider)
    {
    }

    // The Warp context (rule 2.14): AddWarp passes IWarpContext.Context, which is not a TContext.
    internal MetricQueryService(DbContext context, IOptions<WarpConfiguration> options, TimeProvider timeProvider)
    {
        _context = context;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<MetricInstrumentModel[]> ListInstruments(CancellationToken ct)
    {
        var rows = await _context.Set<MetricInstrument>()
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x =>
                new
                {
                    x.Name,
                    x.Meter,
                    x.Kind,
                    x.Unit,
                    x.Description,
                    x.Bounds,
                    x.TagKeys,
                    x.FirstSeenAt,
                    x.LastSeenAt,
                    x.CollapsedAt,
                    x.CollapsedTags,
                })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(x =>
                new MetricInstrumentModel
                {
                    Name = x.Name,
                    Meter = x.Meter,
                    Kind = x.Kind,
                    Unit = x.Unit,
                    Description = x.Description,
                    Bounds = MetricSeriesMath.ParseBounds(x.Bounds) ?? [],
                    TagKeys = MetricSeriesMath.SplitCsv(x.TagKeys),
                    FirstSeenAt = x.FirstSeenAt,
                    LastSeenAt = x.LastSeenAt,
                    CollapsedAt = x.CollapsedAt,
                    CollapsedTags = MetricSeriesMath.SplitCsv(x.CollapsedTags),
                }),
        ];
    }

    public async Task<MetricKind?> GetInstrumentKindAsync(string name, CancellationToken ct)
        => await _context.Set<MetricInstrument>()
            .AsNoTracking()
            .Where(x => x.Name == name)
            .Select(x => (MetricKind?)x.Kind)
            .FirstOrDefaultAsync(ct);

    public async Task<MetricSeriesModel?> GetSeries(string instrument, MetricSeriesQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var from = MetricSeriesMath.AsUtc(query.From);
        var to = MetricSeriesMath.AsUtc(query.To);
        if (from >= to)
        {
            throw new MetricQueryValidationException("The range must have From before To.", nameof(query));
        }

        // The service enforces its own limits rather than trusting every caller to: the dashboard parser applies the
        // same constants, and any other caller of this public service gets the same bound on the rows one read scans.
        if (to - from > TimeSpan.FromDays(IMetricQueryService.MaxRangeDays))
        {
            throw new MetricQueryValidationException($"The range must not exceed {IMetricQueryService.MaxRangeDays} days.", nameof(query));
        }

        if (query.Filters.Count > IMetricQueryService.MaxFilters)
        {
            throw new MetricQueryValidationException($"At most {IMetricQueryService.MaxFilters} filters are allowed.", nameof(query));
        }

        if (query.Percentile is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Percentile, "The percentile must be between 1 and 99.");
        }

        // Defence in depth behind the endpoint's own check: a compare overlay reads [from - 7d, ...) and the tier
        // floor takes up to one more day, so a range this close to DateTime's ends would underflow (or overflow).
        if (from < IMetricQueryService.EarliestFrom || to > IMetricQueryService.LatestTo)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "The range is too close to the ends of the calendar to read.");
        }

        if (!MetricFilter.TryCreate(query.Filters, out var filter, out var filterError))
        {
            throw new MetricQueryValidationException(filterError ?? "The filter is malformed.", nameof(query));
        }

        var info = await _context.Set<MetricInstrument>()
            .AsNoTracking()
            .Where(x => x.Name == instrument)
            .Select(x =>
                new
                {
                    x.Name,
                    x.Kind,
                    x.Unit,
                })
            .FirstOrDefaultAsync(ct);
        if (info is null)
        {
            return null;
        }

        var cutoff = CompleteCutoff(MetricSeriesMath.TierForLength(to - from));
        var primary = await ReadLinesAsync(info.Name, info.Kind, from, to, query, filter, null, null, TimeSpan.Zero, cutoff, ct);
        var lines = new List<MetricSeriesLine>(primary.Lines);
        var headlines = new List<MetricHeadlineModel> { primary.Headline };

        // An overlay keeps the primary's breakdown values (the rest folded into "(rest)") so its lines pair
        // with the current ones instead of competing for their own top 10.
        var keep = query.Breakdown is null
            ? null
            : primary.Lines
                .Select(x => x.Name)
                .Where(x => !string.Equals(x, MetricSeriesMath.RestName, StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);

        foreach (var offset in query.Compare.Distinct().Order())
        {
            var span = MetricCompareOffsets.Span(offset);
            var shifted = await ReadLinesAsync(info.Name, info.Kind, from - span, to - span, query, filter, keep, MetricCompareOffsets.Label(offset), span, cutoff, ct);
            lines.AddRange(shifted.Lines);
            headlines.Add(shifted.Headline);
        }

        return new MetricSeriesModel
        {
            Instrument = info.Name,
            Kind = info.Kind,
            Unit = info.Unit,
            From = from,
            To = to,
            Resolution = primary.Tier,
            CompleteUntil = new DateTime(Math.Clamp(cutoff.Ticks, from.Ticks, to.Ticks), DateTimeKind.Utc),
            Series = lines,
            Headlines = headlines,
        };
    }

    // The exact statement a series read issues: every tier overlapping [from, to) (MetricPointQueries.InRange, shared
    // with the SLO evaluator), summed per the read shape. Also what the SC9 measurement EXPLAINs.
    internal IQueryable<MetricPointRow> PointRows(string instrument, DateTime from, DateTime to, string? application, MetricRead read)
    {
        var points = MetricPointQueries.InRange(
            _context.Set<MetricPoint>().AsNoTracking(),
            instrument,
            from,
            to,
            application,
            FineMinutes);

        // Bucket CSVs cannot be summed in SQL, so only a histogram read that needs a percentile brings its partial rows
        // back as they are, to be merged element-wise in memory. Every other read sums in SQL: per
        // (tier, bucket, tags, bounds) when tags matter, per (tier, bucket) alone when nothing looks at them.
        if (read == MetricRead.SeriesWithBuckets)
        {
            return points
                .Select(x =>
                    new MetricPointRow
                    {
                        Tier = x.Tier,
                        BucketStart = x.BucketStart,
                        Tags = x.Tags,
                        Bounds = x.Bounds,
                        Version = x.Version,
                        Count = x.Count,
                        Sum = x.Sum,
                        Min = x.Min,
                        Max = x.Max,
                        Buckets = x.Buckets,
                    });
        }

        return MetricPointQueries.Summed(points, byTags: read != MetricRead.Total, byVersion: read != MetricRead.Total);
    }

    // The cheapest read that answers the query: bucket CSVs only for a histogram percentile, per-series rows only when
    // a filter or a breakdown looks at the tags, otherwise one summed row per (tier, bucket).
    private static MetricRead ReadFor(MetricKind kind, MetricSeriesQuery query)
    {
        if (kind == MetricKind.Histogram && query.Percentile is not null)
        {
            return MetricRead.SeriesWithBuckets;
        }

        return query.Breakdown is null && query.Filters.Count == 0 ? MetricRead.Total : MetricRead.Series;
    }

    private int FineMinutes => Math.Max(1, _options.Value.FineResolutionMinutes);

    // The start of the first display bucket that is still filling. A bucket is complete once its end, plus one flush
    // interval for the processes that have not written their last measurements yet, has passed: so the bucket holding
    // (now - flush interval) and everything after it are incomplete, and every bucket before it is complete.
    private DateTime CompleteCutoff(MetricTier tier)
    {
        var flush = _options.Value.MetricFlushInterval > TimeSpan.Zero ? _options.Value.MetricFlushInterval : TimeSpan.Zero;

        return MetricTiers.BucketStart(tier, _timeProvider.GetUtcNow().UtcDateTime - flush, FineMinutes);
    }

    private async Task<(MetricTier Tier, List<MetricSeriesLine> Lines, MetricHeadlineModel Headline)> ReadLinesAsync(
        string instrument,
        MetricKind kind,
        DateTime from,
        DateTime to,
        MetricSeriesQuery query,
        MetricFilter filter,
        HashSet<string>? keep,
        string? compare,
        TimeSpan shift,
        DateTime cutoff,
        CancellationToken ct)
    {
        // The display resolution follows the range length; the read covers every overlapping tier and re-bins.
        var tier = MetricSeriesMath.TierForLength(to - from);
        var fineMinutes = FineMinutes;
        var rows = await PointRows(instrument, from, to, query.Application, ReadFor(kind, query)).ToListAsync(ct);

        var tagCache = new Dictionary<(string Tags, string? Version), Dictionary<string, string>>();
        var boundsCache = new Dictionary<string, double[]?>(StringComparer.Ordinal);
        var byValue = new Dictionary<string, BucketMap>(StringComparer.Ordinal);
        var corrupt = 0;

        foreach (var row in rows)
        {
            if (!tagCache.TryGetValue((row.Tags, row.Version), out var tags))
            {
                // The version is a pseudo-tag, so the one filter matcher and the one breakdown path serve it too. A row
                // without one leaves it absent, which both read as {none}, exactly like a missing tag.
                tags = MetricFilter.ParseCanonicalTags(row.Tags);
                if (row.Version is not null)
                {
                    tags[MetricSeriesQuery.VersionKey] = row.Version;
                }

                tagCache[(row.Tags, row.Version)] = tags;
            }

            if (!filter.Matches(tags))
            {
                continue;
            }

            var name = query.Breakdown is null ? MetricSeriesMath.TotalName : MetricSeriesMath.BreakdownValue(tags, query.Breakdown);
            if (!byValue.TryGetValue(name, out var buckets))
            {
                buckets = [];
                byValue[name] = buckets;
            }

            // A row finer than the display bucket floors into it; a coarser one keeps its own bucket.
            var bucket = row.Tier >= tier ? row.BucketStart : MetricTiers.BucketStart(tier, row.BucketStart, fineMinutes);
            if (!buckets.TryGetValue(bucket, out var accumulator))
            {
                accumulator = new MetricAccumulator();
                buckets[bucket] = accumulator;
            }

            if (!accumulator.Add(row, boundsCache))
            {
                corrupt++;
            }
        }

        if (corrupt > 0)
        {
            // A corrupt or mismatched bucket CSV only loses that row's percentile contribution; counted, never a value.
            MetricCardinalityGuard.Count(instrument, MetricCardinalityGuard.ReasonError);
        }

        if (query.Breakdown is null && byValue.Count == 0)
        {
            byValue[MetricSeriesMath.TotalName] = [];
        }

        var grouped = query.Breakdown is null ? [.. byValue] : GroupRest(byValue, kind, keep);
        var lines = grouped.ConvertAll(x => BuildLine(x.Key, x.Value, kind, query.Percentile, compare, tier, shift, cutoff, fineMinutes));

        return (tier, lines, Headline(byValue.Values, kind, compare, shift, cutoff));
    }

    // Every series of the range (before the top 10 split, so (rest) counts), complete buckets only: a bucket counts when
    // it starts before the cutoff once shifted onto the current range, so an overlay drops the buckets aligned with the
    // current range's still-filling one rather than comparing a full bucket against a partial one.
    private static MetricHeadlineModel Headline(IEnumerable<BucketMap> series, MetricKind kind, string? compare, TimeSpan shift, DateTime cutoff)
    {
        long count = 0;
        double sum = 0;
        foreach (var buckets in series)
        {
            foreach (var (bucket, accumulator) in buckets)
            {
                if (bucket + shift < cutoff)
                {
                    count += accumulator.Count;
                    sum += accumulator.Sum;
                }
            }
        }

        return new MetricHeadlineModel
        {
            Compare = compare,
            Value = count > 0 ? HeadlineValue(kind, count, sum) : null,
            Count = count,
        };
    }

    // Ranking: the top 10 breakdown values by the absolute headline value over the range stay, ties broken by
    // ordinal value, and the remainder sums into the rest line. The returned order IS the ranking (the UI keeps it).
    // With a keep-set (an overlay) the primary's values stay instead, so both ranges split the same way.
    private static List<ValueBuckets> GroupRest(Dictionary<string, BucketMap> byValue, MetricKind kind, HashSet<string>? keep)
    {
        List<ValueBuckets> ranked =
        [
            .. byValue
                .OrderByDescending(x => Math.Abs(RangeHeadline(kind, x.Value)))
                .ThenBy(x => x.Key, StringComparer.Ordinal),
        ];

        List<ValueBuckets> kept = keep is null
            ? [.. ranked.Take(MetricSeriesMath.MaxBreakdownValues)]
            : [.. ranked.Where(x => keep.Contains(x.Key))];
        var keptKeys = kept
            .Select(x => x.Key)
            .ToHashSet(StringComparer.Ordinal);
        List<ValueBuckets> rest = [.. ranked.Where(x => !keptKeys.Contains(x.Key))];

        if (rest.Count == 0)
        {
            return kept;
        }

        var merged = new BucketMap();
        foreach (var entry in rest)
        {
            foreach (var (bucket, accumulator) in entry.Value)
            {
                if (!merged.TryGetValue(bucket, out var target))
                {
                    target = new MetricAccumulator();
                    merged[bucket] = target;
                }

                target.Merge(accumulator);
            }
        }

        kept.Add(new ValueBuckets(MetricSeriesMath.RestName, merged));

        return kept;
    }

    private static MetricSeriesLine BuildLine(
        string name,
        BucketMap buckets,
        MetricKind kind,
        int? percentile,
        string? compare,
        MetricTier tier,
        TimeSpan shift,
        DateTime cutoff,
        int fineMinutes)
    {
        var tracksExtremes = kind is MetricKind.Histogram or MetricKind.Gauge;
        var points = new List<MetricPointModel>(buckets.Count);
        foreach (var (bucket, accumulator) in buckets)
        {
            points.Add(new MetricPointModel
            {
                Timestamp = bucket + shift,
                Value = HeadlineValue(kind, accumulator),
                Count = accumulator.Count,
                Sum = accumulator.Sum,
                Min = tracksExtremes ? accumulator.Min : null,
                Max = tracksExtremes ? accumulator.Max : null,
                Percentile = kind == MetricKind.Histogram && percentile is not null ? accumulator.Percentile(percentile.Value) : null,
                Incomplete = compare is null && bucket >= cutoff,
                BucketSeconds = MetricSeriesMath.BucketSeconds((MetricTier)Math.Max((int)tier, (int)accumulator.Coarsest), fineMinutes),
            });
        }

        // The coarser of the display tier and the coarsest stored tier read: a 30-day read of fine rows is still
        // binned to days, and a 6-hour read holding an hourly row is only hour-accurate.
        var resolution = buckets.Count == 0 ? tier : (MetricTier)Math.Max((int)tier, (int)buckets.Values.Max(x => x.Coarsest));

        return new MetricSeriesLine
        {
            Name = name,
            Compare = compare,
            Resolution = resolution,
            Points = points,
        };
    }

    // The headline over the whole range, for ranking: the summed sum (counter, up-down counter), the summed count
    // (histogram), the range average (gauge: sum over count across every bucket).
    private static double RangeHeadline(MetricKind kind, BucketMap buckets)
    {
        long count = 0;
        double sum = 0;
        foreach (var accumulator in buckets.Values)
        {
            count += accumulator.Count;
            sum += accumulator.Sum;
        }

        return HeadlineValue(kind, count, sum);
    }

    private static double HeadlineValue(MetricKind kind, MetricAccumulator accumulator)
        => HeadlineValue(kind, accumulator.Count, accumulator.Sum);

    private static double HeadlineValue(MetricKind kind, long count, double sum)
    {
        if (kind == MetricKind.Gauge)
        {
            return count > 0 ? sum / count : 0;
        }

        return MetricPointQueries.EventQuantity(kind, count, sum);
    }
}

/// <summary>How much of the stored rows a series read fetches.</summary>
internal enum MetricRead
{
    /// <summary>One row summed per (tier, bucket): no breakdown, no filter, no percentile.</summary>
    Total = 1,

    /// <summary>One row summed per (tier, bucket, tags, bounds).</summary>
    Series = 2,

    /// <summary>The rows as stored, bucket CSVs included: a histogram percentile.</summary>
    SeriesWithBuckets = 3,
}

/// <summary>One summed row of a series read: a point row, or the SQL-grouped sum of a key's partial rows.</summary>
internal sealed class MetricPointRow
{
    public MetricTier Tier { get; init; }

    public DateTime BucketStart { get; init; }

    public string Tags { get; init; } = string.Empty;

    public string? Bounds { get; init; }

    /// <summary>The application version of the rows summed here; null when the writer did not opt into it, or the read did not group by it.</summary>
    public string? Version { get; init; }

    public long Count { get; init; }

    public double Sum { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public string? Buckets { get; init; }
}

/// <summary>Parsing, filtering and range helpers of the series read, kept out of the generic service.</summary>
internal static class MetricSeriesMath
{
    public const int MaxBreakdownValues = 10;
    public const string TotalName = "total";
    public const string RestName = "(rest)";
    public const string NoneName = MetricSeries.None;

    private const int FineRangeHours = 6;
    private const int HourlyRangeDays = 7;

    // Tier by range length (spec §D): up to 6 hours reads fine, up to 7 days hourly, anything longer daily.
    public static MetricTier TierForLength(TimeSpan length)
    {
        if (length <= TimeSpan.FromHours(FineRangeHours))
        {
            return MetricTier.Fine;
        }

        return length <= TimeSpan.FromDays(HourlyRangeDays) ? MetricTier.Hourly : MetricTier.Daily;
    }

    public static int BucketSeconds(MetricTier tier, int fineMinutes) => tier switch
    {
        MetricTier.Fine => fineMinutes * 60,
        MetricTier.Hourly => 3600,
        MetricTier.Daily => 86400,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown metric tier."),
    };

    public static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime()
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static string[] SplitCsv(string? csv) => string.IsNullOrEmpty(csv)
        ? []
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries);

    public static double[]? ParseBounds(string? csv) => MetricBuckets.TryParseBounds(csv, out var bounds) ? bounds : null;

    // "k1=v1,k2=v2" (keys sorted, '-' when empty; a repeated key keeps its last value). Tag values never contain ','
    // or '=' — the collector replaces them. The one parser of the stored canonical form.
    public static Dictionary<string, string> ParseTags(string? tags)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(tags) || string.Equals(tags, MetricSeries.NoTags, StringComparison.Ordinal))
        {
            return parsed;
        }

        foreach (var pair in tags.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                parsed[pair[..separator]] = pair[(separator + 1)..];
            }
        }

        return parsed;
    }

    public static string BreakdownValue(IReadOnlyDictionary<string, string> tags, string key)
        => tags.TryGetValue(key, out var value) ? value : NoneName;
}

/// <summary>
/// The merged aggregate of every row that lands in one output point. Histogram counts are kept per bounds
/// ladder: rows whose ladders differ never add element-wise, and the percentile reads the ladder that saw the
/// most measurements.
/// </summary>
internal sealed class MetricAccumulator
{
    private Dictionary<string, (double[] Bounds, long[] Counts)>? _ladders;

    /// <summary>The coarsest storage tier any merged row came from.</summary>
    public MetricTier Coarsest { get; private set; } = MetricTier.Fine;

    public long Count { get; private set; }

    public double Sum { get; private set; }

    public double? Min { get; private set; }

    public double? Max { get; private set; }

    /// <summary>
    /// Adds one row. False when the row carried bucket detail that could not be used (a corrupt bounds or bucket CSV,
    /// or counts that do not match the ladder): its count, sum and extremes are still added, only its percentile
    /// contribution is skipped, and the caller counts it.
    /// </summary>
    public bool Add(MetricPointRow row, Dictionary<string, double[]?> boundsCache)
    {
        Coarsest = Coarsest >= row.Tier ? Coarsest : row.Tier;
        Count += row.Count;
        Sum += row.Sum;
        Min = MetricBuckets.MinOf(Min, row.Min);
        Max = MetricBuckets.MaxOf(Max, row.Max);

        if (row.Bounds is null || row.Buckets is null)
        {
            return true;
        }

        if (!boundsCache.TryGetValue(row.Bounds, out var bounds))
        {
            bounds = MetricSeriesMath.ParseBounds(row.Bounds);
            boundsCache[row.Bounds] = bounds;
        }

        if (bounds is null || !MetricBuckets.TryParseCounts(row.Buckets, bounds.Length + 1, out var counts))
        {
            return false;
        }

        AddLadder(row.Bounds, bounds, counts);

        return true;
    }

    public void Merge(MetricAccumulator other)
    {
        Coarsest = Coarsest >= other.Coarsest ? Coarsest : other.Coarsest;
        Count += other.Count;
        Sum += other.Sum;
        Min = MetricBuckets.MinOf(Min, other.Min);
        Max = MetricBuckets.MaxOf(Max, other.Max);

        if (other._ladders is null)
        {
            return;
        }

        foreach (var (key, ladder) in other._ladders)
        {
            AddLadder(key, ladder.Bounds, ladder.Counts);
        }
    }

    // Cumulative walk over the ascending buckets: the percentile for quantile q over N samples is the upper bound
    // of the smallest bucket whose cumulative count reaches ceil(q * N), the same walk as the endpoint and SLO
    // percentiles. The overflow bucket reports the largest finite bound.
    public double? Percentile(int percentile)
    {
        if (_ladders is null || _ladders.Count == 0)
        {
            return null;
        }

        var (bounds, counts) = _ladders.Values.MaxBy(x => x.Counts.Sum());
        var total = counts.Sum();
        if (total == 0)
        {
            return null;
        }

        var threshold = (long)Math.Ceiling(percentile / 100d * total);
        long cumulative = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            cumulative += counts[i];
            if (cumulative >= threshold)
            {
                return i < bounds.Length ? bounds[i] : bounds[^1];
            }
        }

        return bounds[^1];
    }

    private void AddLadder(string key, double[] bounds, long[] counts)
    {
        _ladders ??= new Dictionary<string, (double[] Bounds, long[] Counts)>(StringComparer.Ordinal);
        if (!_ladders.TryGetValue(key, out var existing))
        {
            _ladders[key] = (bounds, [.. counts]);

            return;
        }

        for (var i = 0; i < counts.Length; i++)
        {
            existing.Counts[i] += counts[i];
        }
    }
}
