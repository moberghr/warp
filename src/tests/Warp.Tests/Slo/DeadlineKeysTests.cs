using Shouldly;
using Warp.Core.Services;

namespace Warp.Tests.Slo;

/// <summary>
/// NoDb coverage for <see cref="DeadlineKeys"/> — the per-job-type deadline-attainment counter family (§8.31),
/// tiered onto the metrics retention scheme (§8.30) so attainment downsamples and gets 5-min fast-burn. Pins the
/// count/miss layout, the fine-tier emission, and the tier-aware round-trip parsers.
/// </summary>
[Trait("Category", "NoDb")]
public class DeadlineKeysTests
{
    private static readonly DateTime Ts = new(2026, 8, 2, 14, 25, 0, DateTimeKind.Utc);

    private static string FineSuffix => MetricTiers.Suffix(MetricTier.Fine, Ts, 5);

    [Fact]
    public void Build_Missed_EmitsCountAndMissAtLifetimeAndFineTier()
    {
        FineSuffix.ShouldBe(":m5:2026-08-02-14-25");

        var counters = DeadlineKeys.Build("MyJob", missed: true, application: null, FineSuffix);

        counters.ShouldContain(c => c.Key == "deadline:MyJob:count" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "deadline:MyJob:hist:count:m5:2026-08-02-14-25" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "deadline:MyJob:miss" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "deadline:MyJob:hist:miss:m5:2026-08-02-14-25" && c.Value == 1);
    }

    [Fact]
    public void Build_NotMissed_EmitsOnlyCount()
    {
        var counters = DeadlineKeys.Build("MyJob", missed: false, application: null, FineSuffix);

        counters.ShouldContain(c => c.Key == "deadline:MyJob:count");
        counters.ShouldNotContain(c => c.Key.Contains(":miss", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithApplication_AddsPerAppSlice()
    {
        var counters = DeadlineKeys.Build("MyJob", missed: true, application: "orders", FineSuffix);

        counters.ShouldContain(c => c.Key == "deadline-app:orders:MyJob:count");
        counters.ShouldContain(c => c.Key == "deadline-app:orders:MyJob:hist:miss:m5:2026-08-02-14-25");
    }

    [Fact]
    public void TryParseHistory_RoundTripsTierAndBucket()
    {
        DeadlineKeys.TryParseHistory("deadline:MyJob:hist:miss:h1:2026-08-02-14", out var type, out var token, out var tier, out var bucket).ShouldBeTrue();
        type.ShouldBe("MyJob");
        token.ShouldBe("miss");
        tier.ShouldBe(MetricTier.Hourly);
        bucket.ShouldBe(new DateTime(2026, 8, 2, 14, 0, 0, DateTimeKind.Utc));

        // Lifetime totals must reject a history key.
        DeadlineKeys.TryParseTotal("deadline:MyJob:hist:miss:h1:2026-08-02-14", out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParseAppHistory_RoundTrips()
    {
        DeadlineKeys.TryParseAppHistory("deadline-app:orders:MyJob:hist:count:d1:2026-08-02", out var app, out var type, out var token, out var tier, out var bucket).ShouldBeTrue();
        app.ShouldBe("orders");
        type.ShouldBe("MyJob");
        token.ShouldBe("count");
        tier.ShouldBe(MetricTier.Daily);
        bucket.ShouldBe(new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void TryParseTotal_RoundTrips()
    {
        DeadlineKeys.TryParseTotal("deadline:MyJob:count", out var type, out var token).ShouldBeTrue();
        type.ShouldBe("MyJob");
        token.ShouldBe("count");
    }
}
