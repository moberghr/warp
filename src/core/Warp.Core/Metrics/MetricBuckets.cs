using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Numerics;

namespace Warp.Core.Metrics;

/// <summary>
/// Histogram bucket bounds for collected instruments. Precedence: the configured
/// <see cref="InstrumentOptions.Buckets"/> → the instrument's <c>Advice.HistogramBucketBoundaries</c> (which
/// ASP.NET Core and HttpClient set) → a default chosen by unit. At most
/// <see cref="WarpMetricsOptions.MaxBucketBounds"/> strictly increasing bounds; the overflow bucket is implicit,
/// so an aggregate carries <c>bounds.Length + 1</c> bucket counts.
/// <para>
/// Every resolved ladder is <b>normalized</b>: each bound is rounded to 7 significant digits, and the ladder must stay
/// strictly increasing and fit the 256-character <c>Bounds</c> column as CSV. Bucket assignment uses the normalized
/// values, which are exactly what storage writes, so stored bounds and bucket counts always agree.
/// </para>
/// </summary>
internal static class MetricBuckets
{
    /// <summary>The OTel seconds ladder, for instruments whose unit is <c>s</c>.</summary>
    public static readonly double[] Seconds = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];

    /// <summary>The millisecond ladder, for instruments whose unit is <c>ms</c>.</summary>
    public static readonly double[] Milliseconds = [5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000];

    /// <summary>The OTel default explicit-bucket ladder, for any other unit.</summary>
    public static readonly double[] Default = [0, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000];

    /// <summary>
    /// The normalized bounds for <paramref name="instrument"/>. <see cref="ResolvedBounds.AdviceRejected"/> is true
    /// when the instrument carried advice that could not be used (invalid, or too long once stored) and the unit
    /// default was taken instead, so the caller can report it.
    /// </summary>
    public static ResolvedBounds Resolve(IReadOnlyList<double>? configured, Instrument instrument)
    {
        if (configured is not null && TryNormalize(configured, out var normalized))
        {
            return new ResolvedBounds(normalized, false);
        }

        var advice = AdviceBounds(instrument);
        if (advice is null)
        {
            return new ResolvedBounds(ForUnit(instrument.Unit), false);
        }

        return TryNormalize(advice, out var adviceBounds)
            ? new ResolvedBounds(adviceBounds, false)
            : new ResolvedBounds(ForUnit(instrument.Unit), true);
    }

    /// <summary>
    /// Rounds each bound to 7 significant digits and checks the result is still a valid ladder whose CSV fits
    /// <see cref="MetricPointMapper.MaxBoundsLength"/>. False when it does not; nothing is truncated.
    /// </summary>
    public static bool TryNormalize(IReadOnlyList<double> bounds, [NotNullWhen(true)] out double[]? normalized)
    {
        normalized = null;
        if (!IsValid(bounds))
        {
            return false;
        }

        var rounded = new double[bounds.Count];
        var csvLength = bounds.Count - 1;
        for (var i = 0; i < bounds.Count; i++)
        {
            rounded[i] = Round(bounds[i]);
            if (i > 0 && rounded[i] <= rounded[i - 1])
            {
                return false;
            }

            csvLength += Format(rounded[i]).Length;
        }

        if (csvLength > MetricPointMapper.MaxBoundsLength)
        {
            return false;
        }

        normalized = rounded;

        return true;
    }

    /// <summary>The bounds as stored: a CSV of invariant round-trip numbers.</summary>
    public static string ToCsv(double[] bounds) => string.Join(',', bounds.Select(Format));

    public static double[] ForUnit(string? unit)
    {
        if (string.Equals(unit, "s", StringComparison.Ordinal))
        {
            return [.. Seconds];
        }

        return string.Equals(unit, "ms", StringComparison.Ordinal) ? [.. Milliseconds] : [.. Default];
    }

    public static bool IsValid(IReadOnlyList<double> bounds)
    {
        if (bounds.Count is 0 or > WarpMetricsOptions.MaxBucketBounds)
        {
            return false;
        }

        for (var i = 0; i < bounds.Count; i++)
        {
            if (!double.IsFinite(bounds[i]))
            {
                return false;
            }

            if (i > 0 && bounds[i] <= bounds[i - 1])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The index of the first bound ≥ <paramref name="value"/>, or <c>bounds.Length</c> (the overflow bucket).</summary>
    public static int IndexOf(double[] bounds, double value)
    {
        for (var i = 0; i < bounds.Length; i++)
        {
            if (value <= bounds[i])
            {
                return i;
            }
        }

        return bounds.Length;
    }

    /// <summary>
    /// The bucket counts as stored: a CSV of invariant integers. The one formatter of the <c>Buckets</c> column, shared
    /// by the flusher, the rollup and the read side.
    /// </summary>
    public static string FormatCounts(IReadOnlyList<long> counts)
        => string.Join(',', counts.Select(x => x.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Parses a stored bucket-count CSV. Tolerant by design: a null, empty, malformed or negative entry, or (when
    /// <paramref name="expectedLength"/> is given) a count of entries that does not match the bounds ladder, is false —
    /// never an exception — so one corrupt row cannot fail a read or stall the rollup.
    /// </summary>
    public static bool TryParseCounts(string? csv, int? expectedLength, [NotNullWhen(true)] out long[]? counts)
    {
        counts = null;
        if (string.IsNullOrEmpty(csv))
        {
            return false;
        }

        var parts = csv.Split(',');
        if (expectedLength is { } expected && parts.Length != expected)
        {
            return false;
        }

        var parsed = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]))
            {
                return false;
            }
        }

        counts = parsed;

        return true;
    }

    /// <summary>
    /// The element-wise sum of two stored bucket-count CSVs, as a CSV that fits <see cref="MetricPointMapper.MaxBucketsLength"/>.
    /// A null or empty side contributes nothing. False — never a truncated or misaligned CSV — when either side does not
    /// parse, their lengths (or <paramref name="expectedLength"/>) disagree, a sum overflows, or the result would not fit
    /// the column; the caller then keeps the counts and drops the bucket detail.
    /// </summary>
    public static bool TrySumCounts(string? first, string? second, int? expectedLength, out string? sum)
    {
        sum = null;
        var hasFirst = !string.IsNullOrEmpty(first);
        var hasSecond = !string.IsNullOrEmpty(second);
        if (!hasFirst && !hasSecond)
        {
            return true;
        }

        long[]? a = null;
        long[]? b = null;
        if ((hasFirst && !TryParseCounts(first, expectedLength, out a))
            || (hasSecond && !TryParseCounts(second, expectedLength, out b)))
        {
            return false;
        }

        // A missing side contributes nothing: the present one is the sum.
        if (!TryAddCounts(b is null ? null : a, b ?? a!, out var sums))
        {
            return false;
        }

        sum = FormatCounts(sums);

        return true;
    }

    /// <summary>
    /// <paramref name="current"/> plus <paramref name="addend"/> element-wise (or <paramref name="addend"/> itself when
    /// <paramref name="current"/> is null), as counts whose CSV fits <see cref="MetricPointMapper.MaxBucketsLength"/>.
    /// False when the lengths disagree, a sum overflows, or the CSV would not fit; the inputs are never modified.
    /// </summary>
    public static bool TryAddCounts(long[]? current, long[] addend, [NotNullWhen(true)] out long[]? sums)
    {
        sums = null;
        long[] result;
        if (current is null)
        {
            result = addend;
        }
        else
        {
            if (current.Length != addend.Length)
            {
                return false;
            }

            result = new long[current.Length];
            for (var i = 0; i < result.Length; i++)
            {
                // Counts are never negative (the parser rejects a sign), so overflow shows as a negative sum.
                result[i] = current[i] + addend[i];
                if (result[i] < 0)
                {
                    return false;
                }
            }
        }

        if (CsvLength(result) > MetricPointMapper.MaxBucketsLength)
        {
            return false;
        }

        sums = result;

        return true;
    }

    /// <summary>
    /// Parses a stored bounds CSV (invariant numbers; empty entries skipped). Null or empty is false, as is any entry
    /// that does not parse. The one parser of the <c>Bounds</c> column, shared by the read side and the rollup.
    /// </summary>
    public static bool TryParseBounds(string? csv, [NotNullWhen(true)] out double[]? bounds)
    {
        bounds = null;
        if (string.IsNullOrEmpty(csv))
        {
            return false;
        }

        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var parsed = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
            {
                return false;
            }
        }

        bounds = parsed;

        return true;
    }

    /// <summary>The smaller of two optional extremes; an absent side contributes nothing.</summary>
    public static double? MinOf(double? first, double? second)
        => first is { } a && second is { } b ? Math.Min(a, b) : first ?? second;

    /// <summary>The larger of two optional extremes; an absent side contributes nothing.</summary>
    public static double? MaxOf(double? first, double? second)
        => first is { } a && second is { } b ? Math.Max(a, b) : first ?? second;

    /// <summary>Reads <c>Instrument&lt;T&gt;.Advice.HistogramBucketBoundaries</c> for every numeric <c>T</c> a listener can observe.</summary>
    public static double[]? AdviceBounds(Instrument instrument) => instrument switch
    {
        Instrument<double> x => Convert(x.Advice?.HistogramBucketBoundaries),
        Instrument<float> x => Convert(x.Advice?.HistogramBucketBoundaries),
        Instrument<long> x => Convert(x.Advice?.HistogramBucketBoundaries),
        Instrument<int> x => Convert(x.Advice?.HistogramBucketBoundaries),
        Instrument<short> x => Convert(x.Advice?.HistogramBucketBoundaries),
        Instrument<byte> x => Convert(x.Advice?.HistogramBucketBoundaries),
        Instrument<decimal> x => Convert(x.Advice?.HistogramBucketBoundaries),
        _ => null,
    };

    private static double Round(double value)
        => double.Parse(value.ToString("G7", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Format(double bound) => bound.ToString("R", CultureInfo.InvariantCulture);

    // The length FormatCounts would produce, without building the string.
    private static int CsvLength(long[] counts)
    {
        var length = counts.Length - 1;
        foreach (var count in counts)
        {
            var digits = 1;
            for (var rest = count / 10; rest > 0; rest /= 10)
            {
                digits++;
            }

            length += digits;
        }

        return length;
    }

    private static double[]? Convert<T>(IReadOnlyList<T>? bounds)
        where T : struct, INumberBase<T>
    {
        if (bounds is null)
        {
            return null;
        }

        return [.. bounds.Select(double.CreateTruncating)];
    }
}

/// <summary>Resolved histogram bounds, and whether the instrument's advice had to be rejected for the unit default.</summary>
internal readonly record struct ResolvedBounds(double[] Bounds, bool AdviceRejected);
