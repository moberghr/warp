using System.Globalization;

namespace Warp.Core.ErrorGrouping;

/// <summary>
/// The durable trend keys for error groups (§8.29) — hourly occurrence counts folded through
/// <c>Counter → CounterAggregator → Statistic</c> (§8.22) so a group's sparkline <b>survives raw-row and even
/// ErrorGroup cleanup</b>. Own top-level prefixes (<see cref="Prefix"/> / <see cref="AppPrefix"/>), DISJOINT
/// from every other family so first-segment-equality parsers reject them (§8.6/§8.19).
/// </summary>
internal static class ErrorGroupKeys
{
    public const string Prefix = "errorgroup";

    public const string AppPrefix = "errorgroup-app";

    /// <summary>Per-fingerprint hourly bucket: <c>errorgroup:{fp}:{yyyyMMddHH}</c>.</summary>
    public static string HourlyKey(string fingerprint, DateTime hourUtc)
        => $"{Prefix}:{fingerprint}:{Hour(hourUtc)}";

    /// <summary>Per-fingerprint, per-application hourly bucket (bounded app set, §8.23).</summary>
    public static string HourlyAppKey(string fingerprint, string application, DateTime hourUtc)
        => $"{AppPrefix}:{fingerprint}:{application}:{Hour(hourUtc)}";

    /// <summary>Prefix for scanning all hourly buckets of one fingerprint (trend read).</summary>
    public static string HourlyScanPrefix(string fingerprint)
        => $"{Prefix}:{fingerprint}:";

    public static bool TryParseHour(string bucket, out DateTime hourUtc)
        => DateTime.TryParseExact(bucket, "yyyyMMddHH", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out hourUtc);

    private static string Hour(DateTime instant)
        => instant.ToUniversalTime().ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
}
