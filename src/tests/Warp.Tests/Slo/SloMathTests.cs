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
}
