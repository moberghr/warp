using Warp.Core.Services;

namespace Warp.Core.Metrics;

// How much of a stored metric_point bucket falls in a window, shared by the SLO evaluator and the health board so a
// window means the same thing in both: the bucket still being written (it straddles now) has only covered the time up
// to now, so its width is clipped there before prorating — otherwise the newest, partial bucket would be scaled down a
// second time. A coarse bucket that only partly overlaps a window contributes its events scaled by the overlap.
internal static class MetricWindows
{
    public static TimeSpan TierWidth(MetricTier tier, int fineMinutes) => tier switch
    {
        MetricTier.Fine => TimeSpan.FromMinutes(fineMinutes),
        MetricTier.Hourly => TimeSpan.FromHours(1),
        _ => TimeSpan.FromDays(1),
    };

    public static TimeSpan WrittenWidth(MetricTier tier, DateTime bucketStart, DateTime now, int fineMinutes)
    {
        var width = TierWidth(tier, fineMinutes);
        var written = now - bucketStart;

        return written < width ? written : width;
    }

    public static double Overlap(MetricTier tier, DateTime bucketStart, DateTime from, DateTime to, DateTime now, int fineMinutes)
        => SloMath.OverlapFraction(bucketStart, WrittenWidth(tier, bucketStart, now, fineMinutes), from, to);

    public static double CoveredSeconds(MetricTier tier, DateTime bucketStart, DateTime from, DateTime to, DateTime now, int fineMinutes)
    {
        var width = WrittenWidth(tier, bucketStart, now, fineMinutes);

        return SloMath.OverlapFraction(bucketStart, width, from, to) * width.TotalSeconds;
    }
}
