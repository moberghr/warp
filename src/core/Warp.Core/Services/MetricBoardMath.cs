using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Slo;

namespace Warp.Core.Services;

/// <summary>One summed row of the board's batched read: one (instrument, application, tier, bucket[, tags]).</summary>
internal sealed class MetricBoardRow
{
    public string Instrument { get; init; } = string.Empty;

    public string? Application { get; init; }

    public MetricTier Tier { get; init; }

    public DateTime BucketStart { get; init; }

    public string Tags { get; init; } = MetricSeries.NoTags;

    public long Count { get; init; }

    public double Sum { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }
}

// A stored tile parsed once per read: its filter and good/bad lists in the one MetricFilter grammar.
internal sealed record MetricTileSpec(
    MetricTileKind Kind,
    MetricFilter Filter,
    string? RatioTag,
    MetricValueList Good,
    MetricValueList Bad,
    SloGaugeAggregate Aggregate)
{
    public bool NeedsTags => Kind == MetricTileKind.Ratio || !Filter.IsEmpty;

    // False for a stored tile that no longer parses (edited past validation, or version skew): the board shows NoData.
    public static bool TryCreate(MetricBoardTile tile, out MetricTileSpec spec)
    {
        spec = null!;
        if (!MetricFilter.TryParse(tile.TagFilter, out var filter, out _)
            || !MetricValueList.TryParse(tile.GoodValues, out var good, out _)
            || !MetricValueList.TryParse(tile.BadValues, out var bad, out _))
        {
            return false;
        }

        if (tile.Kind == MetricTileKind.Ratio && (string.IsNullOrEmpty(tile.RatioTag) || good.Patterns.Count == 0 || bad.Patterns.Count == 0))
        {
            return false;
        }

        spec = new MetricTileSpec(tile.Kind, filter, tile.RatioTag, good, bad, tile.GaugeAggregate ?? SloGaugeAggregate.Average);

        return true;
    }
}

/// <summary>
/// Pure maths of the metrics health board (#326) — no I/O, unit-tested directly. A window's value prorates every stored
/// bucket by its overlap with the window (clipped at now: <see cref="MetricWindows"/>, shared with the SLO evaluator), so
/// a "since last deploy" window starting mid-hour compares fairly with an hourly baseline. Status comes from
/// <see cref="SloMath"/>: a threshold is <see cref="SloMath.EvaluateBound"/>, a deviation
/// <see cref="SloMath.EvaluateDeviation"/> (the <c>MetricVolumeFloor</c> / baseline <c>MetricRatio</c> maths), both
/// classified by <see cref="SloMath.Classify"/> after the <see cref="SloMath.IsNoData"/> check.
/// </summary>
internal static class MetricBoardMath
{
    /// <summary>
    /// The tile's value over <c>[from, to)</c>, or null when there is nothing to show: a value tile whose instrument has
    /// no row in the window (traffic with zero matches is 0, so "5xx" reads 0, not empty), a gauge with no sample, a
    /// ratio with no good or bad event, or a negative event total (an up-down counter driven below zero) on a ratio.
    /// </summary>
    public static double? WindowValue(
        MetricTileSpec spec,
        MetricKind kind,
        IEnumerable<MetricBoardRow> rows,
        DateTime from,
        DateTime to,
        DateTime now,
        int fineMinutes,
        Dictionary<string, Dictionary<string, string>> tagCache)
    {
        var seen = false;
        double events = 0, good = 0, bad = 0, count = 0, sum = 0;
        double? min = null, max = null;

        foreach (var row in rows)
        {
            var fraction = MetricWindows.Overlap(row.Tier, row.BucketStart, from, to, now, fineMinutes);
            if (fraction <= 0)
            {
                continue;
            }

            seen = true;
            var tags = spec.NeedsTags ? Tags(row.Tags, tagCache) : null;
            if (tags is not null && !spec.Filter.Matches(tags))
            {
                continue;
            }

            if (kind == MetricKind.Gauge)
            {
                if (row.Count > 0)
                {
                    count += row.Count * fraction;
                    sum += row.Sum * fraction;
                    min = MetricBuckets.MinOf(min, row.Min);
                    max = MetricBuckets.MaxOf(max, row.Max);
                }

                continue;
            }

            var quantity = MetricPointQueries.EventQuantity(kind, row.Count, row.Sum) * fraction;
            if (spec.Kind == MetricTileKind.Ratio)
            {
                var value = tags!.GetValueOrDefault(spec.RatioTag!);
                good += spec.Good.Matches(value) ? quantity : 0;
                bad += spec.Bad.Matches(value) ? quantity : 0;
                continue;
            }

            events += quantity;
        }

        if (kind == MetricKind.Gauge)
        {
            return GaugeValue(spec.Aggregate, count, sum, min, max);
        }

        if (spec.Kind == MetricTileKind.Ratio)
        {
            return Ratio(good, bad);
        }

        return seen && double.IsFinite(events) ? events : null;
    }

    /// <summary>
    /// The sparkline over <c>[from, to)</c> at the display <paramref name="tier"/>: each row floors into its display
    /// bucket (a coarser row keeps its own), unprorated, as the series read bins; buckets with nothing to show are left
    /// out. Ordered by time.
    /// </summary>
    public static List<(DateTime Timestamp, double? Value)> Sparkline(
        MetricTileSpec spec,
        MetricKind kind,
        IEnumerable<MetricBoardRow> rows,
        DateTime from,
        DateTime to,
        MetricTier tier,
        int fineMinutes,
        Dictionary<string, Dictionary<string, string>> tagCache)
    {
        var buckets = new SortedDictionary<DateTime, List<MetricBoardRow>>();
        foreach (var row in rows)
        {
            if (row.BucketStart >= to || row.BucketStart + MetricWindows.TierWidth(row.Tier, fineMinutes) <= from)
            {
                continue;
            }

            var bucket = row.Tier >= tier ? row.BucketStart : MetricTiers.BucketStart(tier, row.BucketStart, fineMinutes);
            if (!buckets.TryGetValue(bucket, out var members))
            {
                members = [];
                buckets[bucket] = members;
            }

            members.Add(row);
        }

        // Each bucket is read as a window of its own that it covers entirely (now = MaxValue: nothing is clipped).
        return
        [
            .. buckets
                .Select(x => (x.Key, WindowValue(spec, kind, x.Value, DateTime.MinValue, DateTime.MaxValue, DateTime.MaxValue, fineMinutes, tagCache)))
                .Where(x => x.Item2 is not null),
        ];
    }

    /// <summary>
    /// The tile's status: null without a rule; <see cref="SloState.NoData"/> when the value (or, for a deviation, the
    /// chosen baseline) is missing, a relative baseline is not positive, or the maths are not finite; otherwise
    /// Healthy / Warning / Breaching from <see cref="SloMath.Classify"/>. Never Acknowledged — a tile has no ack.
    /// </summary>
    public static SloState? Status(MetricBoardTile tile, double? current, double? dayAgo, double? weekAgo)
    {
        ArgumentNullException.ThrowIfNull(tile);

        switch (tile.StatusRule)
        {
            case MetricTileStatusRule.Threshold:
                if (current is not { } observed || tile.Threshold is not { } threshold)
                {
                    return SloState.NoData;
                }

                return Classify(SloMath.EvaluateBound(observed, threshold, tile.Comparison ?? SloComparison.AtMost));

            case MetricTileStatusRule.Deviation:
                var baseline = tile.BaselineDays == 1 ? dayAgo : weekAgo;
                var ratioPoints = tile.Kind == MetricTileKind.Ratio;
                if (current is not { } now
                    || baseline is not { } then
                    || tile.MaxDeviation is not { } maxDeviation
                    || (!ratioPoints && then <= 0))
                {
                    return SloState.NoData;
                }

                return Classify(SloMath.EvaluateDeviation(now, then, maxDeviation, tile.DeviationDirection ?? MetricDeviationDirection.Either, ratioPoints));

            default:
                return null;
        }
    }

    private static SloState Classify((double Attainment, double BudgetRemaining, double BurnRate) result)
        => SloMath.IsNoData(result) ? SloState.NoData : SloMath.Classify(result.BudgetRemaining, acknowledgedActive: false);

    private static double? GaugeValue(SloGaugeAggregate aggregate, double count, double sum, double? min, double? max)
    {
        if (count <= 0)
        {
            return null;
        }

        var value = aggregate switch
        {
            SloGaugeAggregate.Min => min,
            SloGaugeAggregate.Max => max,
            _ => sum / count,
        };

        return value is { } v && double.IsFinite(v) ? v : null;
    }

    private static double? Ratio(double good, double bad)
    {
        var total = good + bad;
        if (good < 0 || bad < 0 || total <= 0 || !double.IsFinite(total))
        {
            return null;
        }

        return good / total;
    }

    private static Dictionary<string, string> Tags(string canonical, Dictionary<string, Dictionary<string, string>> cache)
    {
        if (!cache.TryGetValue(canonical, out var tags))
        {
            tags = MetricFilter.ParseCanonicalTags(canonical);
            cache[canonical] = tags;
        }

        return tags;
    }
}
