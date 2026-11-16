using System.Diagnostics.CodeAnalysis;
using Warp.Core.Enums;
using Warp.Core.Services;

namespace Warp.Core.Models;

/// <summary>
/// Read-side shape of one registry row of the collected-instrument list — the dashboard Metrics page row. Read from
/// <c>metric_instrument</c> only, never from the points.
/// </summary>
public sealed class MetricInstrumentModel
{
    public string Name { get; init; } = string.Empty;

    public string Meter { get; init; } = string.Empty;

    public MetricKind Kind { get; init; }

    public string? Unit { get; init; }

    public string? Description { get; init; }

    /// <summary>Histograms: the bucket upper bounds; empty for every other kind.</summary>
    public IReadOnlyList<double> Bounds { get; init; } = [];

    /// <summary>The tag keys observed on the instrument (keys only, never values — §1.2).</summary>
    public IReadOnlyList<string> TagKeys { get; init; } = [];

    public DateTime FirstSeenAt { get; init; }

    public DateTime LastSeenAt { get; init; }

    /// <summary>When the instrument last collapsed on a cardinality cap; null when it never did.</summary>
    public DateTime? CollapsedAt { get; init; }

    public IReadOnlyList<string> CollapsedTags { get; init; } = [];
}

/// <summary>The offsets a series read can be overlaid with (<c>compare=1d</c> / <c>compare=7d</c>). From 1 (§8.11).</summary>
public enum MetricCompareOffset
{
    Day = 1,
    Week = 2,
}

/// <summary>The wire label and the time span of a <see cref="MetricCompareOffset"/>.</summary>
public static class MetricCompareOffsets
{
    public const string DayLabel = "1d";
    public const string WeekLabel = "7d";

    public static string Label(MetricCompareOffset offset) => offset switch
    {
        MetricCompareOffset.Day => DayLabel,
        MetricCompareOffset.Week => WeekLabel,
        _ => throw new ArgumentOutOfRangeException(nameof(offset), offset, "Unknown compare offset."),
    };

    public static TimeSpan Span(MetricCompareOffset offset) => offset switch
    {
        MetricCompareOffset.Day => TimeSpan.FromDays(1),
        MetricCompareOffset.Week => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(offset), offset, "Unknown compare offset."),
    };

    public static bool TryParse(string? label, [NotNullWhen(true)] out MetricCompareOffset? offset)
    {
        offset = null;
        if (string.Equals(label, DayLabel, StringComparison.Ordinal))
        {
            offset = MetricCompareOffset.Day;
        }
        else if (string.Equals(label, WeekLabel, StringComparison.Ordinal))
        {
            offset = MetricCompareOffset.Week;
        }

        return offset is not null;
    }
}

/// <summary>One series read: the range, an optional application, one breakdown tag, tag filters, overlays and a percentile.</summary>
public sealed class MetricSeriesQuery
{
    /// <summary>Inclusive start of the range (UTC).</summary>
    public DateTime From { get; init; }

    /// <summary>Exclusive end of the range (UTC).</summary>
    public DateTime To { get; init; }

    /// <summary>Restricts the read to one application; null reads every application.</summary>
    public string? Application { get; init; }

    /// <summary>One tag key to split the series by; null returns a single "total" series.</summary>
    public string? Breakdown { get; init; }

    /// <summary>Tag key → value. A value ending in <c>*</c> is a prefix match (<c>http.response.status_code=5*</c>).</summary>
    public IReadOnlyDictionary<string, string> Filters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Overlays: the same range shifted back by each offset.</summary>
    public IReadOnlyCollection<MetricCompareOffset> Compare { get; init; } = [];

    /// <summary>Histograms: the percentile (1–99) to compute from the merged buckets; null skips it.</summary>
    public int? Percentile { get; init; }
}

/// <summary>One point of a series line.</summary>
public sealed class MetricPointModel
{
    /// <summary>The bucket start (UTC). On a compare overlay it is shifted forward so it aligns with the current series.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// The headline value of the bucket: the sum for a counter or up-down counter, the measurement count for a
    /// histogram (its rate), the average for a gauge.
    /// </summary>
    public double Value { get; init; }

    public long Count { get; init; }

    public double Sum { get; init; }

    /// <summary>Histograms and gauges only.</summary>
    public double? Min { get; init; }

    /// <summary>Histograms and gauges only.</summary>
    public double? Max { get; init; }

    /// <summary>Histograms only, when a percentile was requested: the upper bound of the bucket the percentile falls in.</summary>
    public double? Percentile { get; init; }

    /// <summary>
    /// True on the current range's newest bucket while it is still filling: it ends after now, or within one
    /// <see cref="WarpConfiguration.MetricFlushInterval"/> of now (processes flush once per interval, so its last
    /// measurements may not be written yet). Its value reads low and <see cref="MetricSeriesModel.Headlines"/> leave it
    /// out. Always false on a compare overlay, whose buckets are in the past.
    /// </summary>
    public bool Incomplete { get; init; }
}

/// <summary>One line of a series response: the total, one breakdown value, <c>(rest)</c>, or an overlay of one of those.</summary>
public sealed class MetricSeriesLine
{
    /// <summary>The breakdown tag value, <c>{none}</c>, <c>(rest)</c>, or <c>total</c> when no breakdown was asked for.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The overlay offset label (<c>1d</c> / <c>7d</c>); null for the current range.</summary>
    public string? Compare { get; init; }

    /// <summary>The resolution this line was served at.</summary>
    public MetricTier Resolution { get; init; }

    public IReadOnlyList<MetricPointModel> Points { get; init; } = [];
}

/// <summary>The answer to a series read: the instrument and every line, each labelled with its resolution.</summary>
public sealed class MetricSeriesModel
{
    public string Instrument { get; init; } = string.Empty;

    public MetricKind Kind { get; init; }

    public string? Unit { get; init; }

    public DateTime From { get; init; }

    public DateTime To { get; init; }

    /// <summary>The resolution of the current range (overlays carry their own on each line).</summary>
    public MetricTier Resolution { get; init; }

    /// <summary>
    /// Where the complete buckets end: the start of the current range's first still-filling bucket
    /// (<see cref="MetricPointModel.Incomplete"/>), clamped to [<see cref="From"/>, <see cref="To"/>]. The headlines cover
    /// [<see cref="From"/>, <see cref="CompleteUntil"/>), and each overlay the same span shifted back.
    /// </summary>
    public DateTime CompleteUntil { get; init; }

    public IReadOnlyList<MetricSeriesLine> Series { get; init; } = [];

    /// <summary>
    /// One headline per range: the current range first (<see cref="MetricHeadlineModel.Compare"/> null), then each
    /// overlay. Every line of the range is included, <c>(rest)</c> too, but only the complete buckets.
    /// </summary>
    public IReadOnlyList<MetricHeadlineModel> Headlines { get; init; } = [];
}

/// <summary>
/// The headline value of one range over its complete buckets only: the summed sum (counter, up-down counter), the summed
/// measurement count (histogram) or the average (gauge). The still-filling bucket is left out, never prorated, and an
/// overlay leaves out the buckets aligned with it, so the current range and its overlays cover the same span.
/// </summary>
public sealed class MetricHeadlineModel
{
    /// <summary>The overlay offset label (<c>1d</c> / <c>7d</c>); null for the current range.</summary>
    public string? Compare { get; init; }

    /// <summary>Null when the complete buckets hold no measurement.</summary>
    public double? Value { get; init; }

    /// <summary>The measurements behind <see cref="Value"/>.</summary>
    public long Count { get; init; }
}
