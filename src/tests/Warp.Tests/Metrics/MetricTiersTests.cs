using Shouldly;
using Warp.Core.Services;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for <see cref="MetricTiers"/> — the shared tier suffix build/parse/classify logic behind the
/// metrics retention tiers (§8.30). Pins bucket flooring, the explicit <c>m5/h1/d1</c> marker round-trip,
/// coarsening, legacy-hourly recognition, and the key classifier that both <c>StatisticRollup</c> and the
/// dashboard history readers depend on.
/// </summary>
[Trait("Category", "NoDb")]
public class MetricTiersTests
{
    private static readonly DateTime Ts = new(2026, 8, 2, 14, 37, 12, DateTimeKind.Utc);

    [Fact]
    public void Stamp_FloorsToBucketStart()
    {
        MetricTiers.Stamp(MetricTier.Fine, Ts, 5).ShouldBe("2026-08-02-14-35");
        MetricTiers.Stamp(MetricTier.Hourly, Ts, 5).ShouldBe("2026-08-02-14");
        MetricTiers.Stamp(MetricTier.Daily, Ts, 5).ShouldBe("2026-08-02");
    }

    [Fact]
    public void Suffix_EmitsMarkerAndStamp()
    {
        MetricTiers.Suffix(MetricTier.Fine, Ts, 5).ShouldBe(":m5:2026-08-02-14-35");
        MetricTiers.Suffix(MetricTier.Hourly, Ts, 5).ShouldBe(":h1:2026-08-02-14");
        MetricTiers.Suffix(MetricTier.Daily, Ts, 5).ShouldBe(":d1:2026-08-02");
    }

    [Fact]
    public void TryParse_RoundTripsEachTier_AndRejectsBadInput()
    {
        MetricTiers.TryParse("m5", "2026-08-02-14-35", out var fine, out var fb).ShouldBeTrue();
        fine.ShouldBe(MetricTier.Fine);
        fb.ShouldBe(new DateTime(2026, 8, 2, 14, 35, 0, DateTimeKind.Utc));

        MetricTiers.TryParse("h1", "2026-08-02-14", out var hourly, out var hb).ShouldBeTrue();
        hourly.ShouldBe(MetricTier.Hourly);
        hb.ShouldBe(new DateTime(2026, 8, 2, 14, 0, 0, DateTimeKind.Utc));

        MetricTiers.TryParse("d1", "2026-08-02", out var daily, out var db).ShouldBeTrue();
        daily.ShouldBe(MetricTier.Daily);
        db.ShouldBe(new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc));

        MetricTiers.TryParse("zz", "2026-08-02", out _, out _).ShouldBeFalse();       // unknown marker
        MetricTiers.TryParse("m5", "2026-08-02-14", out _, out _).ShouldBeFalse();     // stamp/format mismatch
        MetricTiers.TryParse("m5", "not-a-date", out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Coarsen_WalksFineToHourlyToDailyThenStops()
    {
        MetricTiers.Coarsen(MetricTier.Fine).ShouldBe(MetricTier.Hourly);
        MetricTiers.Coarsen(MetricTier.Hourly).ShouldBe(MetricTier.Daily);
        MetricTiers.Coarsen(MetricTier.Daily).ShouldBeNull();
    }

    [Fact]
    public void TryClassifyKey_MarkedKey_ReturnsBaseTierAndBucket()
    {
        MetricTiers.TryClassifyKey("jobstat:type:X:hist:succeeded:m5:2026-08-02-14-35", out var baseKey, out var tier, out var bucket).ShouldBeTrue();
        baseKey.ShouldBe("jobstat:type:X:hist:succeeded");
        tier.ShouldBe(MetricTier.Fine);
        bucket.ShouldBe(new DateTime(2026, 8, 2, 14, 35, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void TryClassifyKey_LegacyUnmarkedHourly_ClassifiesAsHourly()
    {
        MetricTiers.TryClassifyKey("stats:succeeded:2026-08-02-14", out var baseKey, out var tier, out var bucket).ShouldBeTrue();
        baseKey.ShouldBe("stats:succeeded");
        tier.ShouldBe(MetricTier.Hourly);
        bucket.ShouldBe(new DateTime(2026, 8, 2, 14, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void TryClassifyKey_LeavesLifetimeAndGaugeKeysAlone()
    {
        MetricTiers.TryClassifyKey("jobstat:type:X:succeeded", out _, out _, out _).ShouldBeFalse();  // lifetime total
        MetricTiers.TryClassifyKey("jobstat:type:X:pct:100", out _, out _, out _).ShouldBeFalse();    // lifetime pct
        MetricTiers.TryClassifyKey("qbacklog:default:depth", out _, out _, out _).ShouldBeFalse();    // gauge
        MetricTiers.TryClassifyKey("stats:succeeded", out _, out _, out _).ShouldBeFalse();           // no date
    }
}
