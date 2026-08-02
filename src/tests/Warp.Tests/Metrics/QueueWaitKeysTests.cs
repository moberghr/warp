using Shouldly;
using Warp.Core.Services;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for <see cref="QueueWaitKeys"/> — the per-queue queue-wait Counter key family (§8.26) with the
/// tiered history keys (§8.30). Pins the key layout (count + dur sum + lifetime pct + tiered hist/pcth,
/// app-agnostic + per-app), the round-trip parsers, bucket assignment, colon-sanitisation, and the
/// negative-wait clamp — so a regression in the key shape (which would silently detach the fold from the
/// reader) trips here.
/// </summary>
[Trait("Category", "NoDb")]
public class QueueWaitKeysTests
{
    private static readonly DateTime Ts = new(2026, 7, 26, 8, 10, 0, DateTimeKind.Utc);

    private static string FineSuffix => MetricTiers.Suffix(MetricTier.Fine, Ts, 5);

    [Fact]
    public void Build_AppAgnostic_EmitsCountDurLifetimePctAndTieredHist()
    {
        FineSuffix.ShouldBe(":m5:2026-07-26-08-10");

        var counters = QueueWaitKeys.Build("default", waitMs: 42, application: null, tierSuffix: FineSuffix);

        counters.ShouldContain(c => c.Key == "qwait:default:count" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "qwait:default:dur" && c.Value == 42);
        counters.ShouldContain(c => c.Key == "qwait:default:hist:count:m5:2026-07-26-08-10" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "qwait:default:hist:dur:m5:2026-07-26-08-10" && c.Value == 42);

        // 42ms → smallest bucket bound >= 42 is 50: lifetime pct + tiered pcth.
        counters.ShouldContain(c => c.Key == "qwait:default:pct:50" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "qwait:default:pcth:50:m5:2026-07-26-08-10" && c.Value == 1);

        counters.ShouldNotContain(c => c.Key.StartsWith("qwait-app:", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithApplication_AddsPerAppSlice_WithoutHistogram()
    {
        var counters = QueueWaitKeys.Build("default", waitMs: 42, application: "orders", tierSuffix: FineSuffix);

        counters.ShouldContain(c => c.Key == "qwait-app:orders:default:count" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "qwait-app:orders:default:dur" && c.Value == 42);
        counters.ShouldContain(c => c.Key == "qwait-app:orders:default:hist:count:m5:2026-07-26-08-10");

        // Per-app slice omits both histograms (lifetime pct + tiered pcth) — bounds volume, like jobstat-app.
        counters.ShouldNotContain(c => c.Key.StartsWith("qwait-app:", StringComparison.Ordinal) && c.Key.Contains(":pct:", StringComparison.Ordinal));
        counters.ShouldNotContain(c => c.Key.StartsWith("qwait-app:", StringComparison.Ordinal) && c.Key.Contains(":pcth:", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_NegativeWait_ClampsToZero()
    {
        var counters = QueueWaitKeys.Build("q", waitMs: -5, application: null, tierSuffix: FineSuffix);

        counters.ShouldContain(c => c.Key == "qwait:q:dur" && c.Value == 0);
        counters.ShouldContain(c => c.Key == "qwait:q:pct:5" && c.Value == 1);   // 0ms → smallest bucket (5)
    }

    [Fact]
    public void Build_SanitizesColonInQueueAndApplication()
    {
        var counters = QueueWaitKeys.Build("a:b", waitMs: 1, application: "app:1", tierSuffix: FineSuffix);

        counters.ShouldContain(c => c.Key == "qwait:a-b:count");
        counters.ShouldContain(c => c.Key == "qwait-app:app-1:a-b:count");
    }

    [Fact]
    public void TryParseTotal_RoundTrips_AndRejectsOtherShapes()
    {
        QueueWaitKeys.TryParseTotal("qwait:default:dur", out var queue, out var token).ShouldBeTrue();
        queue.ShouldBe("default");
        token.ShouldBe("dur");

        QueueWaitKeys.TryParseTotal("qwait:default:pct:50", out _, out _).ShouldBeFalse();
        QueueWaitKeys.TryParseTotal("qwait-app:orders:default:count", out _, out _).ShouldBeFalse();
        QueueWaitKeys.TryParseTotal("jobstat:type:X:succeeded", out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParsePct_RoundTrips()
    {
        QueueWaitKeys.TryParsePct("qwait:default:pct:250", out var queue, out var upperMs).ShouldBeTrue();
        queue.ShouldBe("default");
        upperMs.ShouldBe(250);

        QueueWaitKeys.TryParsePct("qwait:default:dur", out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParseHistory_RoundTripsTierAndBucket_AndRejectsPcth()
    {
        var key = "qwait:default:hist:dur:m5:2026-07-26-08-10";

        QueueWaitKeys.TryParseHistory(key, out var queue, out var token, out var tier, out var bucket).ShouldBeTrue();
        queue.ShouldBe("default");
        token.ShouldBe("dur");
        tier.ShouldBe(MetricTier.Fine);
        bucket.ShouldBe(new DateTime(2026, 7, 26, 8, 10, 0, DateTimeKind.Utc));

        QueueWaitKeys.TryParsePctHistory(key, out _, out _, out _, out _).ShouldBeFalse();
        QueueWaitKeys.TryParseTotal(key, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParsePctHistory_RoundTripsTierAndBucket()
    {
        var key = "qwait:default:pcth:250:h1:2026-07-26-08";

        QueueWaitKeys.TryParsePctHistory(key, out var queue, out var upperMs, out var tier, out var bucket).ShouldBeTrue();
        queue.ShouldBe("default");
        upperMs.ShouldBe(250);
        tier.ShouldBe(MetricTier.Hourly);
        bucket.ShouldBe(new DateTime(2026, 7, 26, 8, 0, 0, DateTimeKind.Utc));

        QueueWaitKeys.TryParseHistory(key, out _, out _, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParseApp_RoundTrips_AndRejectsHistory()
    {
        QueueWaitKeys.TryParseApp("qwait-app:orders:default:dur", out var app, out var queue, out var token).ShouldBeTrue();
        app.ShouldBe("orders");
        queue.ShouldBe("default");
        token.ShouldBe("dur");

        QueueWaitKeys.TryParseApp("qwait-app:orders:default:hist:count:m5:2026-07-26-08-10", out _, out _, out _).ShouldBeFalse();
        QueueWaitKeys.TryParseApp("qwait:default:count", out _, out _, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 5)]
    [InlineData(6, 10)]
    [InlineData(1000, 1000)]
    [InlineData(1001, 2500)]
    [InlineData(999999, int.MaxValue)]
    public void BucketFor_PicksSmallestBoundAtOrAbove(int durationMs, int expectedBucket)
    {
        QueueWaitKeys.BucketFor(durationMs).ShouldBe(expectedBucket);
    }
}
