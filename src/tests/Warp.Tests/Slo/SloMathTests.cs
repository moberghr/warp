using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Services;

namespace Warp.Tests.Slo;

/// <summary>
/// NoDb coverage for <see cref="SloMath"/> — the pure error-budget arithmetic (§8.31). Rate objectives use the
/// standard budget = 1 − burn model; threshold objectives use a headroom fraction; both feed one state classifier.
/// </summary>
[Trait("Category", "NoDb")]
public class SloMathTests
{
    [Fact]
    public void EvaluateRate_WithinTarget_HasPositiveBudget()
    {
        // 99/100 succeeded, target 0.95 → error rate 0.01, allowed 0.05, burn 0.2, budget 0.8.
        var (attainment, budget, burn) = SloMath.EvaluateRate(99, 100, 0.95);

        attainment.ShouldBe(0.99, 1e-9);
        burn.ShouldBe(0.2, 1e-9);
        budget.ShouldBe(0.8, 1e-9);
    }

    [Fact]
    public void EvaluateRate_NoObservations_IsFullyHealthy()
    {
        var (attainment, budget, burn) = SloMath.EvaluateRate(0, 0, 0.99);

        attainment.ShouldBe(1.0);
        budget.ShouldBe(1.0);
        burn.ShouldBe(0.0);
    }

    [Fact]
    public void EvaluateRate_BlownBudget_IsNegative()
    {
        // 90/100, target 0.99 → error rate 0.10, allowed 0.01, burn 10, budget -9.
        var (_, budget, burn) = SloMath.EvaluateRate(90, 100, 0.99);

        burn.ShouldBe(10.0, 1e-6);
        budget.ShouldBe(-9.0, 1e-6);
    }

    [Fact]
    public void EvaluateThreshold_UnderTarget_HasHeadroom()
    {
        // observed 30ms, target 50ms → budget (50-30)/50 = 0.4, burn 0.6.
        var (attainment, budget, burn) = SloMath.EvaluateThreshold(30, 50);

        attainment.ShouldBe(30);
        budget.ShouldBe(0.4, 1e-9);
        burn.ShouldBe(0.6, 1e-9);
    }

    [Fact]
    public void EvaluateThreshold_OverTarget_IsNegative()
    {
        var (_, budget, burn) = SloMath.EvaluateThreshold(60, 50);

        budget.ShouldBe(-0.2, 1e-9);
        burn.ShouldBe(1.2, 1e-9);
    }

    [Theory]
    [InlineData(0.8, false, SloState.Healthy)]
    [InlineData(0.24, false, SloState.Warning)]
    [InlineData(-0.1, false, SloState.Breaching)]
    [InlineData(-0.1, true, SloState.Acknowledged)]
    public void Classify_MapsBudgetToState(double budget, bool ackActive, SloState expected)
    {
        SloMath.Classify(budget, ackActive).ShouldBe(expected);
    }

    [Fact]
    public void Percentile_WalksBucketHistogram()
    {
        // 100 samples: 90 at ≤50ms, 10 at ≤500ms. p95 falls in the 500 bucket.
        var buckets = new Dictionary<int, long> { [50] = 90, [500] = 10 };

        SloMath.Percentile(buckets, 95).ShouldBe(500);
        SloMath.Percentile(buckets, 50).ShouldBe(50);
        SloMath.Percentile(new Dictionary<int, long>(), 95).ShouldBe(0); // no data
    }

    [Fact]
    public void Percentile_WidenedJobBuckets_ResolvesPastTenSeconds()
    {
        // The job-domain histogram extends to 5 min (§8.31 #1). A p95 that lands in the 60s bucket must report
        // 60000, not saturate at the old 10s top — otherwise a "p95 < 30s" latency SLO could never breach.
        var buckets = new Dictionary<int, long> { [5_000] = 4, [60_000] = 96 };

        SloMath.Percentile(buckets, 95).ShouldBe(60_000);
    }

    [Fact]
    public void Percentile_OverflowBucket_ReportsLastFiniteBound()
    {
        // Beyond the top finite rung (300s) the open-ended overflow bucket has no bound, so the walk reports the
        // last finite bound as a floor — the documented display convention shared across every percentile surface.
        var buckets = new Dictionary<int, long> { [int.MaxValue] = 100 };

        SloMath.Percentile(buckets, 95).ShouldBe(300_000);
    }

    [Fact]
    public void EvaluateRate_ForMetricRatio_ReusesExistingMaths()
    {
        // good 990 / (990 + 10), target 0.95: identical to the jobstat rate maths.
        var (attainment, budget, burn) = SloMath.EvaluateRate(990, 1000, 0.95);

        attainment.ShouldBe(0.99, 1e-9);
        burn.ShouldBe(0.2, 1e-9);
        budget.ShouldBe(0.8, 1e-9);
    }

    [Fact]
    public void EvaluateRatioDrop_SmallDrop_HasBudget()
    {
        // baseline 0.99, current 0.97 → drop 0.02, max drop 0.05 → budget (0.05-0.02)/0.05 = 0.6, burn 0.4.
        var (attainment, budget, burn) = SloMath.EvaluateRatioDrop(0.99, 0.97, 0.05);

        attainment.ShouldBe(0.02, 1e-9);
        budget.ShouldBe(0.6, 1e-9);
        burn.ShouldBe(0.4, 1e-9);
        SloMath.Classify(budget, false).ShouldBe(SloState.Healthy);
    }

    [Fact]
    public void EvaluateRatioDrop_DropOverTarget_IsBreaching()
    {
        var (_, budget, burn) = SloMath.EvaluateRatioDrop(0.99, 0.90, 0.05);

        budget.ShouldBe(-0.8, 1e-9);
        burn.ShouldBe(1.8, 1e-9);
        SloMath.Classify(budget, false).ShouldBe(SloState.Breaching);
    }

    [Fact]
    public void EvaluateRatioDrop_DropEqualToTarget_IsNotBreaching()
    {
        var (_, budget, _) = SloMath.EvaluateRatioDrop(0.75, 0.5, 0.25);

        budget.ShouldBe(0.0, 1e-9);
        SloMath.Classify(budget, false).ShouldBe(SloState.Warning);
    }

    [Fact]
    public void EvaluateRatioDrop_Improvement_HasBudgetAboveOne()
    {
        var (attainment, budget, _) = SloMath.EvaluateRatioDrop(0.90, 0.95, 0.05);

        attainment.ShouldBe(-0.05, 1e-9);
        budget.ShouldBe(2.0, 1e-9);
    }

    [Fact]
    public void EvaluateCount_ZeroLimitZeroObserved_IsFullBudget()
    {
        var (attainment, budget, burn) = SloMath.EvaluateCount(0, 0);

        attainment.ShouldBe(0);
        budget.ShouldBe(1.0);
        burn.ShouldBe(0.0);
        SloMath.Classify(budget, false).ShouldBe(SloState.Healthy);
    }

    [Theory]
    [InlineData(1, -1.0)]
    [InlineData(7, -7.0)]
    public void EvaluateCount_ZeroLimitWithEvents_BudgetIsNegativeObserved(double observed, double expectedBudget)
    {
        var (_, budget, burn) = SloMath.EvaluateCount(observed, 0);

        budget.ShouldBe(expectedBudget, 1e-9);
        burn.ShouldBe(observed, 1e-9); // burn = obs / max(N, 1) = obs / 1
        SloMath.Classify(budget, false).ShouldBe(SloState.Breaching);
        SloMath.Classify(budget, true).ShouldBe(SloState.Acknowledged);
    }

    [Fact]
    public void EvaluateCount_PositiveLimitUnderLimit_HasHeadroom()
    {
        // N = 10, obs = 4 → budget (10-4)/10 = 0.6, burn 4/10 = 0.4.
        var (attainment, budget, burn) = SloMath.EvaluateCount(4, 10);

        attainment.ShouldBe(4);
        budget.ShouldBe(0.6, 1e-9);
        burn.ShouldBe(0.4, 1e-9);
    }

    [Fact]
    public void EvaluateCount_ObservedEqualToLimit_IsNotBreaching()
    {
        var (_, budget, burn) = SloMath.EvaluateCount(10, 10);

        budget.ShouldBe(0.0, 1e-9);
        burn.ShouldBe(1.0, 1e-9);
        SloMath.Classify(budget, false).ShouldBe(SloState.Warning);
    }

    [Fact]
    public void EvaluateCount_ObservedOverLimit_IsBreaching()
    {
        var (_, budget, burn) = SloMath.EvaluateCount(15, 10);

        budget.ShouldBe(-0.5, 1e-9);
        burn.ShouldBe(1.5, 1e-9);
        SloMath.Classify(budget, false).ShouldBe(SloState.Breaching);
    }

    [Fact]
    public void EvaluateCount_NegativeGapWithZeroLimit_IsHealthy()
    {
        // secondary exceeds primary → gap -3; N = 0: budget = -obs = 3 (not breaching).
        var (_, budget, _) = SloMath.EvaluateCount(-3, 0);

        budget.ShouldBe(3.0, 1e-9);
        SloMath.Classify(budget, false).ShouldBe(SloState.Healthy);
    }

    [Fact]
    public void EvaluateCount_FractionalLimit_BurnUsesMaxOfLimitAndOne()
    {
        // Fractional limit below 1 still divides by 1 for burn.
        var (_, _, burn) = SloMath.EvaluateCount(2, 0.5);

        burn.ShouldBe(2.0, 1e-9);
    }

    [Fact]
    public void EvaluateVolumeFloor_AboveTarget_HasBudget()
    {
        // obs 80 / baseline 100 = 0.8, target 0.5 → budget (0.8-0.5)/0.5 = 0.6, burn 0.4.
        var (attainment, budget, burn) = SloMath.EvaluateVolumeFloor(80, 100, 0.5);

        attainment.ShouldBe(0.8, 1e-9);
        budget.ShouldBe(0.6, 1e-9);
        burn.ShouldBe(0.4, 1e-9);
    }

    [Fact]
    public void EvaluateVolumeFloor_BelowTarget_IsBreaching()
    {
        var (attainment, budget, burn) = SloMath.EvaluateVolumeFloor(20, 100, 0.5);

        attainment.ShouldBe(0.2, 1e-9);
        budget.ShouldBe(-0.6, 1e-9);
        burn.ShouldBe(1.6, 1e-9);
        SloMath.Classify(budget, false).ShouldBe(SloState.Breaching);
    }

    [Fact]
    public void EvaluateVolumeFloor_RatioEqualToTarget_IsNotBreaching()
    {
        var (_, budget, _) = SloMath.EvaluateVolumeFloor(50, 100, 0.5);

        budget.ShouldBe(0.0, 1e-9);
        SloMath.Classify(budget, false).ShouldBe(SloState.Warning);
    }

    [Fact]
    public void EvaluateVolumeFloor_BudgetEqualsOneMinusBurn()
    {
        var (_, budget, burn) = SloMath.EvaluateVolumeFloor(35, 100, 0.8);

        (1.0 - burn).ShouldBe(budget, 1e-9);
    }

    [Fact]
    public void EvaluateVolumeFloor_ZeroBaseline_ReadsHealthy()
    {
        var (_, budget, burn) = SloMath.EvaluateVolumeFloor(10, 0, 0.5);

        budget.ShouldBe(1.0);
        burn.ShouldBe(0.0);
    }

    [Fact]
    public void OverlapFraction_BucketFullyInsideWindow_IsOne()
    {
        var start = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

        SloMath.OverlapFraction(start, TimeSpan.FromHours(1), start.AddHours(-1), start.AddHours(3)).ShouldBe(1.0, 1e-9);
    }

    [Fact]
    public void OverlapFraction_PartialOverlap_IsProrated()
    {
        var bucket = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

        // Window starts 15 minutes into a one-hour bucket → 45/60.
        SloMath.OverlapFraction(bucket, TimeSpan.FromHours(1), bucket.AddMinutes(15), bucket.AddHours(5)).ShouldBe(0.75, 1e-9);

        // Window ends 30 minutes into the bucket → 30/60.
        SloMath.OverlapFraction(bucket, TimeSpan.FromHours(1), bucket.AddHours(-5), bucket.AddMinutes(30)).ShouldBe(0.5, 1e-9);
    }

    [Fact]
    public void OverlapFraction_WindowInsideBucket_IsWindowShare()
    {
        var bucket = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);

        // A 6-hour window inside a daily bucket.
        SloMath.OverlapFraction(bucket, TimeSpan.FromDays(1), bucket.AddHours(6), bucket.AddHours(12)).ShouldBe(0.25, 1e-9);
    }

    [Fact]
    public void OverlapFraction_Disjoint_IsZero()
    {
        var bucket = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

        SloMath.OverlapFraction(bucket, TimeSpan.FromHours(1), bucket.AddHours(2), bucket.AddHours(3)).ShouldBe(0.0);
        SloMath.OverlapFraction(bucket, TimeSpan.FromHours(1), bucket.AddHours(-3), bucket).ShouldBe(0.0); // adjacent
    }

    [Fact]
    public void OverlapFraction_DegenerateInputs_AreZero()
    {
        var bucket = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

        SloMath.OverlapFraction(bucket, TimeSpan.Zero, bucket, bucket.AddHours(1)).ShouldBe(0.0);
        SloMath.OverlapFraction(bucket, TimeSpan.FromHours(1), bucket.AddHours(1), bucket).ShouldBe(0.0);
    }

    [Fact]
    public void EvaluateRateThresholdClassify_PinnedInputs_KeepTheirMaths()
    {
        // Pinned values from before B7: EvaluateRate / EvaluateThreshold / Classify / Percentile keep their maths.
        SloMath.EvaluateRate(0, 0, 0.99).ShouldBe((1.0, 1.0, 0.0));
        SloMath.EvaluateRate(90, 100, 0.95).BudgetRemaining.ShouldBe(-1.0, 1e-9);
        SloMath.EvaluateThreshold(0, 0).BudgetRemaining.ShouldBe(1.0, 1e-9);
        SloMath.EvaluateThreshold(60, 50).BurnRate.ShouldBe(1.2, 1e-9);
        SloMath.Classify(0.25, false).ShouldBe(SloState.Healthy);
        SloMath.Classify(0.0, false).ShouldBe(SloState.Warning);
        SloMath.Classify(-0.0001, false).ShouldBe(SloState.Breaching);
    }

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(double.PositiveInfinity, 10)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(5, double.NaN)]
    [InlineData(5, double.PositiveInfinity)]
    [InlineData(double.MaxValue, 0.5)]
    public void EvaluateCount_NonFiniteInputOrResult_ReturnsNoData(double observed, double limit)
    {
        var result = SloMath.EvaluateCount(observed, limit);

        SloMath.IsNoData(result).ShouldBeTrue();
        double.IsNaN(result.BudgetRemaining).ShouldBeTrue();
    }

    [Theory]
    [InlineData(double.NaN, 100, 0.5)]
    [InlineData(double.PositiveInfinity, 100, 0.5)]
    [InlineData(10, double.NaN, 0.5)]
    [InlineData(10, double.PositiveInfinity, 0.5)]
    [InlineData(10, 100, double.NaN)]
    public void EvaluateVolumeFloor_NonFiniteInput_ReturnsNoData(double observed, double baseline, double target)
    {
        SloMath.IsNoData(SloMath.EvaluateVolumeFloor(observed, baseline, target)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(double.NaN, 0.9, 0.05)]
    [InlineData(0.99, double.PositiveInfinity, 0.05)]
    [InlineData(0.99, 0.9, double.NaN)]
    public void EvaluateRatioDrop_NonFiniteInput_ReturnsNoData(double baseline, double current, double maxDrop)
    {
        SloMath.IsNoData(SloMath.EvaluateRatioDrop(baseline, current, maxDrop)).ShouldBeTrue();
    }

    [Fact]
    public void IsNoData_FiniteEvaluations_ReturnsFalse()
    {
        SloMath.IsNoData(SloMath.EvaluateCount(4, 10)).ShouldBeFalse();
        SloMath.IsNoData(SloMath.EvaluateVolumeFloor(80, 100, 0.5)).ShouldBeFalse();
        SloMath.IsNoData(SloMath.EvaluateRatioDrop(0.99, 0.97, 0.05)).ShouldBeFalse();
        SloMath.IsNoData(SloMath.NoDataResult).ShouldBeTrue();
    }

    [Fact]
    public void TrySplitAtThreshold_ThresholdOnABound_CountsThatBucketAsWithin()
    {
        // Bounds 0.1, 0.25, 0.5 plus overflow; threshold 0.25 keeps the first two buckets.
        SloMath.TrySplitAtThreshold([0.1, 0.25, 0.5], [10, 20, 30, 40], 0.25, out var within, out var over).ShouldBeTrue();

        within.ShouldBe(30);
        over.ShouldBe(70);
    }

    [Fact]
    public void TrySplitAtThreshold_ThresholdBetweenBounds_BehavesLikeTheLowerBound()
    {
        // 0.3 sits inside (0.25, 0.5]: that bucket's events may be faster than 0.3, but the ladder cannot tell, so they are over.
        SloMath.TrySplitAtThreshold([0.1, 0.25, 0.5], [10, 20, 30, 40], 0.3, out var within, out var over).ShouldBeTrue();

        within.ShouldBe(30);
        over.ShouldBe(70);
    }

    [Fact]
    public void TrySplitAtThreshold_ThresholdAboveTheLadder_LeavesOnlyTheOverflowBucketOver()
    {
        SloMath.TrySplitAtThreshold([0.1, 0.25, 0.5], [10, 20, 30, 40], 1000, out var within, out var over).ShouldBeTrue();

        within.ShouldBe(60);
        over.ShouldBe(40);
    }

    [Fact]
    public void TrySplitAtThreshold_ThresholdBelowTheFirstBound_IsAllOver()
    {
        SloMath.TrySplitAtThreshold([0.1, 0.25], [5, 5, 5], 0.05, out var within, out var over).ShouldBeTrue();

        within.ShouldBe(0);
        over.ShouldBe(15);
    }

    [Fact]
    public void TrySplitAtThreshold_CountsNotAlignedWithBounds_ReturnsFalse()
    {
        SloMath.TrySplitAtThreshold([0.1, 0.25], [5, 5], 0.1, out _, out _).ShouldBeFalse();
    }
}
