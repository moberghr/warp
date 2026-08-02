using System.Globalization;

namespace Warp.Core.Services;

/// <summary>
/// The resolution of a time-bucketed <c>Statistic</c>/<c>Counter</c> series (§8.30 metrics retention tiers).
/// Fine buckets are emitted on the write path; the <c>StatisticRollup</c> task progressively sums them into the
/// coarser tiers as they age (Fine → Hourly → Daily) so recent data stays detailed while long history stays
/// cheap. Values start at 1 (§8.11).
/// </summary>
internal enum MetricTier
{
    /// <summary>5-minute (configurable) buckets — the finest tier, kept only for the recent window.</summary>
    Fine = 1,

    /// <summary>Hourly buckets — rolled up from Fine, kept for the mid window.</summary>
    Hourly = 2,

    /// <summary>Daily buckets — rolled up from Hourly, kept for the long window.</summary>
    Daily = 3,
}

/// <summary>
/// Builds and parses the trailing <c>:{marker}:{stamp}</c> suffix that tags a hist/pcth key with its
/// <see cref="MetricTier"/> (§8.30). Markers are explicit (<c>m5</c>/<c>h1</c>/<c>d1</c>) so the per-family
/// parsers stay unambiguous and fixed-length, matching Warp's existing key discipline (§8.6/§8.19). Pre-tiering
/// keys ended in a bare <c>:{yyyy-MM-dd-HH}</c> with no marker; <see cref="TryParseLegacyHourly"/> recognizes
/// those so the rollup migrates them to the marked scheme.
/// </summary>
internal static class MetricTiers
{
    public const string FineMarker = "m5";
    public const string HourlyMarker = "h1";
    public const string DailyMarker = "d1";

    public const string FineFormat = "yyyy-MM-dd-HH-mm";
    public const string HourlyFormat = "yyyy-MM-dd-HH";
    public const string DailyFormat = "yyyy-MM-dd";

    public static string Marker(MetricTier tier) => tier switch
    {
        MetricTier.Fine => FineMarker,
        MetricTier.Hourly => HourlyMarker,
        MetricTier.Daily => DailyMarker,
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    public static string Format(MetricTier tier) => tier switch
    {
        MetricTier.Fine => FineFormat,
        MetricTier.Hourly => HourlyFormat,
        MetricTier.Daily => DailyFormat,
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    /// <summary>The start instant of the bucket <paramref name="utc"/> falls in for the given tier.</summary>
    public static DateTime BucketStart(MetricTier tier, DateTime utc, int fineMinutes) => tier switch
    {
        MetricTier.Fine => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute / fineMinutes * fineMinutes, 0, DateTimeKind.Utc),
        MetricTier.Hourly => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc),
        MetricTier.Daily => new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc),
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    /// <summary>The bucket stamp string (floored) for <paramref name="utc"/> at the given tier.</summary>
    public static string Stamp(MetricTier tier, DateTime utc, int fineMinutes)
        => BucketStart(tier, utc, fineMinutes).ToString(Format(tier), CultureInfo.InvariantCulture);

    /// <summary>The trailing key suffix <c>:{marker}:{stamp}</c> for <paramref name="utc"/> at the given tier.</summary>
    public static string Suffix(MetricTier tier, DateTime utc, int fineMinutes)
        => $":{Marker(tier)}:{Stamp(tier, utc, fineMinutes)}";

    /// <summary>Parses a <c>{marker}</c> + <c>{stamp}</c> pair into a tier and its bucket-start instant.</summary>
    public static bool TryParse(string marker, string stamp, out MetricTier tier, out DateTime bucketStart)
    {
        bucketStart = default;

        MetricTier? parsed = marker switch
        {
            FineMarker => MetricTier.Fine,
            HourlyMarker => MetricTier.Hourly,
            DailyMarker => MetricTier.Daily,
            _ => null,
        };

        if (parsed is not { } resolved)
        {
            tier = default;

            return false;
        }

        tier = resolved;

        return DateTime.TryParseExact(stamp, Format(resolved), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out bucketStart);
    }

    /// <summary>The coarser tier a bucket rolls into, or null when already at the coarsest (<see cref="MetricTier.Daily"/>).</summary>
    public static MetricTier? Coarsen(MetricTier tier) => tier switch
    {
        MetricTier.Fine => MetricTier.Hourly,
        MetricTier.Hourly => MetricTier.Daily,
        _ => null,
    };

    /// <summary>Recognizes a pre-tiering unmarked hourly stamp (<c>yyyy-MM-dd-HH</c>) for rollup migration.</summary>
    public static bool TryParseLegacyHourly(string stamp, out DateTime bucketStart)
        => DateTime.TryParseExact(stamp, HourlyFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out bucketStart);

    /// <summary>
    /// Extracts the tier, bucket-start instant, and family base-key (everything before the tier suffix) from any
    /// time-bucketed <c>Statistic</c> key — marked (<c>…:{m5|h1|d1}:{stamp}</c>) or legacy unmarked
    /// (<c>…:{yyyy-MM-dd-HH}</c>, treated as hourly). Returns false for keys with no parseable tier suffix
    /// (lifetime totals, lifetime <c>pct</c>, <c>qbacklog</c> gauge, any non-date suffix) so callers leave them
    /// alone. Shared by <c>StatisticRollup</c> and the dashboard history readers.
    /// </summary>
    public static bool TryClassifyKey(string key, out string baseKey, out MetricTier tier, out DateTime bucketStart)
    {
        tier = default;
        bucketStart = default;
        baseKey = string.Empty;

        var lastColon = key.LastIndexOf(':');
        if (lastColon <= 0)
        {
            return false;
        }

        var stamp = key[(lastColon + 1)..];
        var beforeStamp = key[..lastColon];

        var prevColon = beforeStamp.LastIndexOf(':');
        if (prevColon > 0 && TryParse(beforeStamp[(prevColon + 1)..], stamp, out tier, out bucketStart))
        {
            baseKey = beforeStamp[..prevColon];

            return true;
        }

        if (TryParseLegacyHourly(stamp, out bucketStart))
        {
            tier = MetricTier.Hourly;
            baseKey = beforeStamp;

            return true;
        }

        return false;
    }
}
