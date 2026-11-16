using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;

namespace Warp.Core.Metrics;

/// <summary>
/// The one tier-bounded <see cref="MetricPoint"/> range filter, shared by the dashboard read
/// (<c>MetricQueryService</c>, on the host's <c>TContext</c>) and the SLO evaluator (on the server context, §2.14).
/// Each caller passes its own context's set and adds its own grouping, so neither crosses contexts.
/// </summary>
internal static class MetricPointQueries
{
    /// <summary>
    /// The rows of <paramref name="instrument"/> (and <paramref name="application"/> when given) in EVERY tier that can
    /// hold a row overlapping <c>[from, to)</c>. Fine rows only roll to hourly after <c>FineResolutionRetention</c>
    /// and hourly rows to daily after <c>HourlyStatisticsRetention</c>, so any single tier misses the newest data;
    /// rollup deletes a source in the transaction that sums it into its parent, so a value lives in exactly one tier
    /// and nothing is counted twice. Each tier's lower bound is the start of the bucket containing <paramref name="from"/>
    /// at that tier, so a coarse row straddling the start of the range is still read.
    /// </summary>
    public static IQueryable<MetricPoint> InRange(
        IQueryable<MetricPoint> points,
        string instrument,
        DateTime from,
        DateTime to,
        string? application,
        int fineMinutes)
    {
        var fineFrom = MetricTiers.BucketStart(MetricTier.Fine, from, fineMinutes);
        var hourlyFrom = MetricTiers.BucketStart(MetricTier.Hourly, from, fineMinutes);
        var dailyFrom = MetricTiers.BucketStart(MetricTier.Daily, from, fineMinutes);

        var inRange = points
            .Where(x => x.Instrument == instrument)
            .Where(x => x.BucketStart < to)
            .Where(x =>
                (x.Tier == MetricTier.Fine && x.BucketStart >= fineFrom)
                || (x.Tier == MetricTier.Hourly && x.BucketStart >= hourlyFrom)
                || (x.Tier == MetricTier.Daily && x.BucketStart >= dailyFrom));

        if (application is null)
        {
            return inRange;
        }

        // Stored applications went through the collector's sanitiser; filter by the same form so a name over 64
        // characters, or one holding ',' or '=', still matches its own rows.
        var stored = NormalizeApplication(application);

        return inRange.Where(x => x.Application == stored);
    }

    /// <summary>
    /// The metrics health board's one batched range filter (#326): the rows of every one of <paramref name="instruments"/>
    /// that can overlap <c>[from, to)</c>, the same window one day earlier, or the same window seven days earlier — the
    /// <see cref="InRange"/> tier bounds applied to each of the three windows and OR'ed, so a board of N tiles is one
    /// statement over three bounded ranges, never a scan of the days in between. Rows of every application come back
    /// (each tile filters its own); a row in two overlapping windows comes back once.
    /// </summary>
    public static IQueryable<MetricPoint> InBoardWindows(
        IQueryable<MetricPoint> points,
        IReadOnlyCollection<string> instruments,
        DateTime from,
        DateTime to,
        int fineMinutes)
    {
        var names = instruments.ToList();
        var dayFrom = from.AddDays(-1);
        var dayTo = to.AddDays(-1);
        var weekFrom = from.AddDays(-7);
        var weekTo = to.AddDays(-7);
        var fine0 = MetricTiers.BucketStart(MetricTier.Fine, from, fineMinutes);
        var hourly0 = MetricTiers.BucketStart(MetricTier.Hourly, from, fineMinutes);
        var daily0 = MetricTiers.BucketStart(MetricTier.Daily, from, fineMinutes);
        var fine1 = MetricTiers.BucketStart(MetricTier.Fine, dayFrom, fineMinutes);
        var hourly1 = MetricTiers.BucketStart(MetricTier.Hourly, dayFrom, fineMinutes);
        var daily1 = MetricTiers.BucketStart(MetricTier.Daily, dayFrom, fineMinutes);
        var fine7 = MetricTiers.BucketStart(MetricTier.Fine, weekFrom, fineMinutes);
        var hourly7 = MetricTiers.BucketStart(MetricTier.Hourly, weekFrom, fineMinutes);
        var daily7 = MetricTiers.BucketStart(MetricTier.Daily, weekFrom, fineMinutes);

        return points
            .Where(x => names.Contains(x.Instrument))
            .Where(x =>
                (x.BucketStart < to
                    && ((x.Tier == MetricTier.Fine && x.BucketStart >= fine0)
                        || (x.Tier == MetricTier.Hourly && x.BucketStart >= hourly0)
                        || (x.Tier == MetricTier.Daily && x.BucketStart >= daily0)))
                || (x.BucketStart < dayTo
                    && ((x.Tier == MetricTier.Fine && x.BucketStart >= fine1)
                        || (x.Tier == MetricTier.Hourly && x.BucketStart >= hourly1)
                        || (x.Tier == MetricTier.Daily && x.BucketStart >= daily1)))
                || (x.BucketStart < weekTo
                    && ((x.Tier == MetricTier.Fine && x.BucketStart >= fine7)
                        || (x.Tier == MetricTier.Hourly && x.BucketStart >= hourly7)
                        || (x.Tier == MetricTier.Daily && x.BucketStart >= daily7))));
    }

    /// <summary>
    /// The board's SQL-side sum of partial rows: per (instrument, application, tier, bucket) — plus tags when
    /// <paramref name="byTags"/> — so a whole board comes back in one grouped read. Versions and bounds are not kept apart
    /// (the board reads neither).
    /// </summary>
    public static IQueryable<MetricBoardRow> SummedForBoard(IQueryable<MetricPoint> points, bool byTags)
    {
        if (!byTags)
        {
            return points
                .GroupBy(x =>
                    new
                    {
                        x.Instrument,
                        x.Application,
                        x.Tier,
                        x.BucketStart,
                    })
                .Select(g =>
                    new MetricBoardRow
                    {
                        Instrument = g.Key.Instrument,
                        Application = g.Key.Application,
                        Tier = g.Key.Tier,
                        BucketStart = g.Key.BucketStart,
                        Tags = MetricSeries.NoTags,
                        Count = g.Sum(x => x.Count),
                        Sum = g.Sum(x => x.Sum),
                        Min = g.Min(x => x.Min),
                        Max = g.Max(x => x.Max),
                    });
        }

        return points
            .GroupBy(x =>
                new
                {
                    x.Instrument,
                    x.Application,
                    x.Tier,
                    x.BucketStart,
                    x.Tags,
                })
            .Select(g =>
                new MetricBoardRow
                {
                    Instrument = g.Key.Instrument,
                    Application = g.Key.Application,
                    Tier = g.Key.Tier,
                    BucketStart = g.Key.BucketStart,
                    Tags = g.Key.Tags,
                    Count = g.Sum(x => x.Count),
                    Sum = g.Sum(x => x.Sum),
                    Min = g.Min(x => x.Min),
                    Max = g.Max(x => x.Max),
                });
    }

    /// <summary>
    /// The SQL-side sum of <paramref name="points"/>' partial rows: per (tier, bucket, tags, bounds) when
    /// <paramref name="byTags"/>, per (tier, bucket) alone otherwise (no tags or bounds, as nothing reads them). The one
    /// grouped projection, shared by the dashboard read and the SLO evaluator; each runs it on its own context (§2.14).
    /// <paramref name="byVersion"/> also keeps application versions apart (the dashboard's tag-level reads, where the
    /// version is a pseudo-tag); the SLO evaluator does not look at versions and leaves it off.
    /// </summary>
    public static IQueryable<MetricPointRow> Summed(IQueryable<MetricPoint> points, bool byTags, bool byVersion = false)
    {
        if (byTags && byVersion)
        {
            return points
                .GroupBy(x =>
                    new
                    {
                        x.Tier,
                        x.BucketStart,
                        x.Tags,
                        x.Bounds,
                        x.Version,
                    })
                .Select(g =>
                    new MetricPointRow
                    {
                        Tier = g.Key.Tier,
                        BucketStart = g.Key.BucketStart,
                        Tags = g.Key.Tags,
                        Bounds = g.Key.Bounds,
                        Version = g.Key.Version,
                        Count = g.Sum(x => x.Count),
                        Sum = g.Sum(x => x.Sum),
                        Min = g.Min(x => x.Min),
                        Max = g.Max(x => x.Max),
                        Buckets = null,
                    });
        }

        if (!byTags)
        {
            return points
                .GroupBy(x =>
                    new
                    {
                        x.Tier,
                        x.BucketStart,
                    })
                .Select(g =>
                    new MetricPointRow
                    {
                        Tier = g.Key.Tier,
                        BucketStart = g.Key.BucketStart,
                        Tags = string.Empty,
                        Bounds = null,
                        Count = g.Sum(x => x.Count),
                        Sum = g.Sum(x => x.Sum),
                        Min = g.Min(x => x.Min),
                        Max = g.Max(x => x.Max),
                        Buckets = null,
                    });
        }

        return points
            .GroupBy(x =>
                new
                {
                    x.Tier,
                    x.BucketStart,
                    x.Tags,
                    x.Bounds,
                })
            .Select(g =>
                new MetricPointRow
                {
                    Tier = g.Key.Tier,
                    BucketStart = g.Key.BucketStart,
                    Tags = g.Key.Tags,
                    Bounds = g.Key.Bounds,
                    Count = g.Sum(x => x.Count),
                    Sum = g.Sum(x => x.Sum),
                    Min = g.Min(x => x.Min),
                    Max = g.Max(x => x.Max),
                    Buckets = null,
                });
    }

    /// <summary>
    /// How many events a row's aggregate stands for: its <c>Count</c> for a histogram (one per recorded measurement),
    /// its <c>Sum</c> for a counter or up-down counter. Shared by the series headline and the SLO evaluator.
    /// </summary>
    public static double EventQuantity(MetricKind kind, long count, double sum)
        => kind == MetricKind.Histogram ? count : sum;

    /// <summary>
    /// The form the collector stores an application in (<see cref="MetricSeries.Sanitize"/>: trimmed, ',' and '=' to
    /// '-', control characters stripped, capped at 64; empty to null). Every read that filters <c>metric_point</c> by
    /// application uses it.
    /// </summary>
    public static string? NormalizeApplication(string? application) => Normalize(application);

    /// <summary>The form the collector stores a version in: the same sanitiser as <see cref="NormalizeApplication"/>.</summary>
    public static string? NormalizeVersion(string? version) => Normalize(version);

    private static string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[MetricSeries.MaxValueLength];
        var length = MetricSeries.Sanitize(value, buffer);

        return length == 0 ? null : buffer[..length].ToString();
    }
}
