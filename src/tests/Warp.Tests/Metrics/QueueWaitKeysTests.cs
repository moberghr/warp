using Shouldly;
using Warp.Core.Services;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for <see cref="QueueWaitKeys"/> — the per-queue queue-wait Counter key family (§8.26). Pins
/// the key layout (count + dur sum + pct histogram, app-agnostic + per-app), the round-trip parsers, bucket
/// assignment, colon-sanitisation, and the negative-wait clamp — so a regression in the key shape (which
/// would silently detach the fold from the reader) trips here.
/// </summary>
[Trait("Category", "NoDb")]
public class QueueWaitKeysTests
{
    [Fact]
    public void Build_AppAgnostic_EmitsCountDurAndBucket()
    {
        var counters = QueueWaitKeys.Build("default", waitMs: 42, application: null, hourBucket: "2026-07-26-08");

        counters.ShouldContain(c => c.Key == "qwait:default:count" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "qwait:default:dur" && c.Value == 42);
        counters.ShouldContain(c => c.Key == "qwait:default:hist:count:2026-07-26-08" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "qwait:default:hist:dur:2026-07-26-08" && c.Value == 42);

        // 42ms → smallest bucket bound >= 42 is 50.
        counters.ShouldContain(c => c.Key == "qwait:default:pct:50" && c.Value == 1);

        // No per-app keys when application is null.
        counters.ShouldNotContain(c => c.Key.StartsWith("qwait-app:", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithApplication_AddsPerAppSlice_WithoutHistogram()
    {
        var counters = QueueWaitKeys.Build("default", waitMs: 42, application: "orders", hourBucket: "2026-07-26-08");

        counters.ShouldContain(c => c.Key == "qwait-app:orders:default:count" && c.Value == 1);
        counters.ShouldContain(c => c.Key == "qwait-app:orders:default:dur" && c.Value == 42);
        counters.ShouldContain(c => c.Key == "qwait-app:orders:default:hist:count:2026-07-26-08");

        // Per-app slice omits the pct histogram (bounds volume — like jobstat-app).
        counters.ShouldNotContain(c => c.Key.StartsWith("qwait-app:orders:default:pct:", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_NegativeWait_ClampsToZero()
    {
        var counters = QueueWaitKeys.Build("q", waitMs: -5, application: null, hourBucket: "h");

        counters.ShouldContain(c => c.Key == "qwait:q:dur" && c.Value == 0);
        counters.ShouldContain(c => c.Key == "qwait:q:pct:5" && c.Value == 1);   // 0ms → smallest bucket (5)
    }

    [Fact]
    public void Build_SanitizesColonInQueueAndApplication()
    {
        var counters = QueueWaitKeys.Build("a:b", waitMs: 1, application: "app:1", hourBucket: "h");

        counters.ShouldContain(c => c.Key == "qwait:a-b:count");
        counters.ShouldContain(c => c.Key == "qwait-app:app-1:a-b:count");
    }

    [Fact]
    public void TryParseTotal_RoundTrips_AndRejectsOtherShapes()
    {
        QueueWaitKeys.TryParseTotal("qwait:default:dur", out var queue, out var token).ShouldBeTrue();
        queue.ShouldBe("default");
        token.ShouldBe("dur");

        QueueWaitKeys.TryParseTotal("qwait:default:pct:50", out _, out _).ShouldBeFalse();      // pct is longer
        QueueWaitKeys.TryParseTotal("qwait-app:orders:default:count", out _, out _).ShouldBeFalse(); // per-app
        QueueWaitKeys.TryParseTotal("jobstat:type:X:succeeded", out _, out _).ShouldBeFalse();   // foreign family
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
    public void TryParseApp_RoundTrips_AndRejectsHistory()
    {
        QueueWaitKeys.TryParseApp("qwait-app:orders:default:dur", out var app, out var queue, out var token).ShouldBeTrue();
        app.ShouldBe("orders");
        queue.ShouldBe("default");
        token.ShouldBe("dur");

        QueueWaitKeys.TryParseApp("qwait-app:orders:default:hist:count:2026-07-26-08", out _, out _, out _).ShouldBeFalse();
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
