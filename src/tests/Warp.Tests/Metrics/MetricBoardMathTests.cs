using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Services;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for the metrics health board's pure maths (#326): the <see cref="SloMath"/> bound / ceiling /
/// deviation functions the tile status shares with the SLOs, <see cref="MetricBoardMath.Status"/> for threshold and
/// deviation rules (NoData included), and the window value and sparkline — proration by overlap, gauge aggregates, ratio
/// tiles, the filter, and empty windows.
/// </summary>
[Trait("Category", "NoDb")]
public class MetricBoardMathTests
{
    private const int FineMinutes = 5;

    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    [TimedTheory]
    [InlineData(5, 10, SloComparison.AtMost, 0.5)]
    [InlineData(12, 10, SloComparison.AtMost, -0.2)]
    [InlineData(0, 0, SloComparison.AtMost, 1.0)]
    [InlineData(3, 0, SloComparison.AtMost, -3.0)]
    [InlineData(-4, -5, SloComparison.AtMost, -0.2)]
    [InlineData(12, 10, SloComparison.AtLeast, 0.2)]
    [InlineData(8, 10, SloComparison.AtLeast, -0.2)]
    [InlineData(-1, 0, SloComparison.AtLeast, -1.0)]
    [InlineData(2, 0, SloComparison.AtLeast, 1.0)]
    public void EvaluateBound_ObservedAgainstTarget_GivesTheHeadroomBudget(double observed, double target, SloComparison comparison, double expectedBudget)
    {
        var (attainment, budget, _) = SloMath.EvaluateBound(observed, target, comparison);

        attainment.ShouldBe(observed);
        budget.ShouldBe(expectedBudget, 1e-9);
    }

    [TimedTheory]
    [InlineData(0.995, 0.98, 0.75)]
    [InlineData(1.0, 0.98, 1.0)]
    [InlineData(0.98, 0.98, 0.0)]
    [InlineData(0.97, 0.98, -0.5)]
    [InlineData(0.5, 0.0, 0.5)]
    public void EvaluateBound_AtLeastOnARatio_IsTheRateBudget(double observed, double target, double expectedBudget)
    {
        var (attainment, budget, burn) = SloMath.EvaluateBound(observed, target, SloComparison.AtLeast, ratio: true);

        attainment.ShouldBe(observed);
        budget.ShouldBe(expectedBudget, 1e-9);
        burn.ShouldBe(1 - expectedBudget, 1e-9);
    }

    [TimedFact]
    public void EvaluateBound_AtLeastOnARatio_MatchesEvaluateRate()
    {
        // 995 good of 1000 against 0.98: the same budget an SLO with that target would give.
        var rate = SloMath.EvaluateRate(995, 1000, 0.98);

        SloMath.EvaluateBound(0.995, 0.98, SloComparison.AtLeast, ratio: true).BudgetRemaining.ShouldBe(rate.BudgetRemaining, 1e-9);
    }

    [TimedTheory]
    [InlineData(0.995, SloState.Healthy)]
    [InlineData(0.984, SloState.Warning)]
    [InlineData(0.98, SloState.Warning)]
    [InlineData(0.975, SloState.Breaching)]
    public void Status_AtLeastThresholdOnARatioTile_CanBeHealthy(double current, SloState expected)
    {
        // Before the fix the budget was (obs − t) / t, at most 0.02 for t = 0.98: never Healthy.
        var tile = Tile(MetricTileStatusRule.Threshold, threshold: 0.98, comparison: SloComparison.AtLeast, kind: MetricTileKind.Ratio);

        MetricBoardMath.Status(tile, current, null, null).ShouldBe(expected);
    }

    [TimedFact]
    public void Status_AtLeastThresholdOnAValueTile_KeepsTheHeadroomOverTheTarget()
    {
        // 12 vs at least 10 on a value tile: 20 % headroom, so Warning (the ratio formula is not applied).
        var tile = Tile(MetricTileStatusRule.Threshold, threshold: 10, comparison: SloComparison.AtLeast);

        MetricBoardMath.Status(tile, 12, null, null).ShouldBe(SloState.Warning);
    }

    [TimedTheory]
    [InlineData(10, SloComparison.AtMost)]
    [InlineData(10, SloComparison.AtLeast)]
    public void Status_ValueEqualToTheThreshold_IsWarningNeverBreaching(double current, SloComparison comparison)
    {
        MetricBoardMath.Status(Tile(MetricTileStatusRule.Threshold, threshold: 10, comparison: comparison), current, null, null)
            .ShouldBe(SloState.Warning);
    }

    [TimedTheory]
    [InlineData(7.5, SloState.Healthy)]
    [InlineData(7.6, SloState.Warning)]
    public void Status_ThresholdAtTheQuarterBudgetEdge_IsHealthyAtExactly25Percent(double current, SloState expected)
    {
        // At most 10: 7.5 leaves exactly 25 % budget, the Warning line (strictly below 0.25 warns).
        MetricBoardMath.Status(Tile(MetricTileStatusRule.Threshold, threshold: 10, comparison: SloComparison.AtMost), current, null, null)
            .ShouldBe(expected);
    }

    [TimedFact]
    public void EvaluateBound_AtMostPositiveTarget_IsExactlyTheSloCountMaths()
    {
        SloMath.EvaluateBound(7, 10, SloComparison.AtMost).ShouldBe(SloMath.EvaluateCount(7, 10));
    }

    [TimedFact]
    public void EvaluateBound_NonFiniteInput_IsNoData()
    {
        SloMath.IsNoData(SloMath.EvaluateBound(double.NaN, 1, SloComparison.AtMost)).ShouldBeTrue();
    }

    [TimedFact]
    public void EvaluateVolumeCeiling_RiseWithinTheAllowance_KeepsBudget()
    {
        // 120 vs a 100 baseline under a 1.5x ceiling: ratio 1.2, budget (1.5 - 1.2) / 0.5 = 0.6.
        var (ratio, budget, burn) = SloMath.EvaluateVolumeCeiling(120, 100, 1.5);

        ratio.ShouldBe(1.2, 1e-9);
        budget.ShouldBe(0.6, 1e-9);
        burn.ShouldBe(0.4, 1e-9);
    }

    [TimedFact]
    public void EvaluateVolumeCeiling_RisePastTheAllowance_IsNegative()
    {
        SloMath.EvaluateVolumeCeiling(200, 100, 1.5).BudgetRemaining.ShouldBeLessThan(0);
    }

    [TimedFact]
    public void EvaluateDeviation_RelativeDrop_IsTheVolumeFloorMaths()
    {
        SloMath.EvaluateDeviation(80, 100, 0.3, MetricDeviationDirection.Drop, ratioPoints: false)
            .ShouldBe(SloMath.EvaluateVolumeFloor(80, 100, 0.7));
    }

    [TimedFact]
    public void EvaluateDeviation_RatioDrop_IsTheBaselineRatioMaths()
    {
        SloMath.EvaluateDeviation(0.95, 0.99, 0.02, MetricDeviationDirection.Drop, ratioPoints: true)
            .ShouldBe(SloMath.EvaluateRatioDrop(0.99, 0.95, 0.02));
    }

    [TimedFact]
    public void EvaluateDeviation_RatioRise_SwapsTheBaselineAndCurrent()
    {
        SloMath.EvaluateDeviation(0.10, 0.02, 0.05, MetricDeviationDirection.Rise, ratioPoints: true)
            .ShouldBe(SloMath.EvaluateRatioDrop(0.10, 0.02, 0.05));
    }

    [TimedFact]
    public void EvaluateDeviation_Either_TakesTheSideWithLessBudget()
    {
        // 140 vs 100 at ±30 %: the drop side is far inside, the rise side is past the ceiling.
        var either = SloMath.EvaluateDeviation(140, 100, 0.3, MetricDeviationDirection.Either, ratioPoints: false);

        either.ShouldBe(SloMath.EvaluateVolumeCeiling(140, 100, 1.3));
        either.BudgetRemaining.ShouldBeLessThan(0);
    }

    [TimedTheory]
    [InlineData(5, SloState.Healthy)]
    [InlineData(9, SloState.Warning)]
    [InlineData(11, SloState.Breaching)]
    public void Status_ThresholdRule_ClassifiesTheBudget(double current, SloState expected)
    {
        var tile = Tile(MetricTileStatusRule.Threshold, threshold: 10, comparison: SloComparison.AtMost);

        MetricBoardMath.Status(tile, current, null, null).ShouldBe(expected);
    }

    [TimedFact]
    public void Status_ThresholdRuleWithoutAValue_IsNoData()
    {
        MetricBoardMath.Status(Tile(MetricTileStatusRule.Threshold, threshold: 10), null, 5, 5).ShouldBe(SloState.NoData);
    }

    [TimedTheory]
    [InlineData(1, 95, SloState.Healthy)]
    [InlineData(7, 95, SloState.Breaching)]
    public void Status_DeviationRule_ReadsTheChosenBaseline(int baselineDays, double current, SloState expected)
    {
        // A 30 % drop allowance: 95 vs yesterday's 100 is fine, 95 vs last week's 200 is a 52 % drop.
        var tile = Tile(MetricTileStatusRule.Deviation, baselineDays: baselineDays, maxDeviation: 0.3, direction: MetricDeviationDirection.Drop);

        MetricBoardMath.Status(tile, current, 100, 200).ShouldBe(expected);
    }

    [TimedTheory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void Status_DeviationOnAValueTileWithoutAPositiveBaseline_IsNoData(double? baseline)
    {
        var tile = Tile(MetricTileStatusRule.Deviation, baselineDays: 1, maxDeviation: 0.3);

        MetricBoardMath.Status(tile, 10, baseline, null).ShouldBe(SloState.NoData);
    }

    [TimedFact]
    public void Status_DeviationOnARatioTile_AcceptsAZeroBaseline()
    {
        var tile = Tile(MetricTileStatusRule.Deviation, baselineDays: 1, maxDeviation: 0.05, kind: MetricTileKind.Ratio);

        MetricBoardMath.Status(tile, 0.01, 0.0, null).ShouldBe(SloState.Healthy);
    }

    [TimedFact]
    public void Status_NoRule_IsNull()
    {
        MetricBoardMath.Status(Tile(MetricTileStatusRule.None), 10, 10, 10).ShouldBeNull();
    }

    [TimedFact]
    public void WindowValue_CounterRows_SumsThoseInTheWindowAndProratesAStraddlingHour()
    {
        // Window 10:30-12:00. The 10:00 hourly row overlaps it by half; the fine rows are inside; 09:00 is outside.
        var rows = new[]
        {
            Row(MetricTier.Hourly, Now.AddHours(-3), sum: 1000),
            Row(MetricTier.Hourly, Now.AddHours(-2), sum: 100),
            Row(MetricTier.Fine, Now.AddMinutes(-30), sum: 7),
            Row(MetricTier.Fine, Now.AddMinutes(-10), sum: 3),
        };

        var value = MetricBoardMath.WindowValue(Spec(), MetricKind.Counter, rows, Now.AddMinutes(-90), Now, Now, FineMinutes, []);

        value.ShouldBe(60);
    }

    [TimedFact]
    public void WindowValue_FilterMatchingNothingWhileTheInstrumentHasRows_IsZeroNotEmpty()
    {
        var rows = new[] { Row(MetricTier.Fine, Now.AddMinutes(-10), sum: 3, tags: "status=200") };

        var value = MetricBoardMath.WindowValue(Spec(filter: "status=5*"), MetricKind.Counter, rows, Now.AddHours(-1), Now, Now, FineMinutes, []);

        value.ShouldBe(0);
    }

    [TimedFact]
    public void WindowValue_NoRowsInTheWindow_IsNull()
    {
        var rows = new[] { Row(MetricTier.Fine, Now.AddHours(-5), sum: 3) };

        MetricBoardMath.WindowValue(Spec(), MetricKind.Counter, rows, Now.AddHours(-1), Now, Now, FineMinutes, []).ShouldBeNull();
    }

    [TimedFact]
    public void WindowValue_Histogram_CountsMeasurements()
    {
        var rows = new[] { Row(MetricTier.Fine, Now.AddMinutes(-10), count: 4, sum: 900) };

        MetricBoardMath.WindowValue(Spec(), MetricKind.Histogram, rows, Now.AddHours(-1), Now, Now, FineMinutes, []).ShouldBe(4);
    }

    [TimedTheory]
    [InlineData(SloGaugeAggregate.Average, 15.0)]
    [InlineData(SloGaugeAggregate.Min, 2.0)]
    [InlineData(SloGaugeAggregate.Max, 40.0)]
    public void WindowValue_Gauge_TakesTheAggregateOfEverySampleNeverTheTotal(SloGaugeAggregate aggregate, double expected)
    {
        // Two processes reporting the same bucket: (3 samples summing 30) and (1 sample of 30) average 60 / 4 = 15.
        var rows = new[]
        {
            Row(MetricTier.Fine, Now.AddMinutes(-10), count: 3, sum: 30, min: 2, max: 20),
            Row(MetricTier.Fine, Now.AddMinutes(-10), count: 1, sum: 30, min: 30, max: 40, tags: "pool=b"),
        };

        MetricBoardMath.WindowValue(Spec(aggregate: aggregate), MetricKind.Gauge, rows, Now.AddHours(-1), Now, Now, FineMinutes, []).ShouldBe(expected);
    }

    [TimedFact]
    public void WindowValue_GaugeWithoutSamples_IsNull()
    {
        MetricBoardMath.WindowValue(Spec(), MetricKind.Gauge, [], Now.AddHours(-1), Now, Now, FineMinutes, []).ShouldBeNull();
    }

    [TimedFact]
    public void WindowValue_Ratio_IsGoodOverGoodPlusBadIgnoringOtherValues()
    {
        var rows = new[]
        {
            Row(MetricTier.Fine, Now.AddMinutes(-10), sum: 90, tags: "result=ok"),
            Row(MetricTier.Fine, Now.AddMinutes(-10), sum: 8, tags: "result=declined"),
            Row(MetricTier.Fine, Now.AddMinutes(-10), sum: 2, tags: "result=error"),
            Row(MetricTier.Fine, Now.AddMinutes(-10), sum: 500, tags: "result=pending"),
        };

        var value = MetricBoardMath.WindowValue(RatioSpec(), MetricKind.Counter, rows, Now.AddHours(-1), Now, Now, FineMinutes, []);

        value.ShouldBe(0.9);
    }

    [TimedFact]
    public void WindowValue_RatioWithNoGoodOrBadEvent_IsNull()
    {
        var rows = new[] { Row(MetricTier.Fine, Now.AddMinutes(-10), sum: 5, tags: "result=pending") };

        MetricBoardMath.WindowValue(RatioSpec(), MetricKind.Counter, rows, Now.AddHours(-1), Now, Now, FineMinutes, []).ShouldBeNull();
    }

    [TimedFact]
    public void Sparkline_FineRowsInAnHourlyDisplay_FloorIntoTheirHourAndSkipEmptyBuckets()
    {
        var rows = new[]
        {
            Row(MetricTier.Fine, Now.AddMinutes(-55), sum: 2),
            Row(MetricTier.Fine, Now.AddMinutes(-50), sum: 3),
            Row(MetricTier.Hourly, Now.AddHours(-3), sum: 9),
        };

        var points = MetricBoardMath.Sparkline(Spec(), MetricKind.Counter, rows, Now.AddHours(-24), Now, MetricTier.Hourly, FineMinutes, []);

        points.ShouldBe([(Now.AddHours(-3), (double?)9), (Now.AddHours(-1), (double?)5)]);
    }

    private static MetricBoardTile Tile(
        MetricTileStatusRule rule,
        double? threshold = null,
        SloComparison? comparison = null,
        int? baselineDays = null,
        double? maxDeviation = null,
        MetricDeviationDirection? direction = null,
        MetricTileKind kind = MetricTileKind.Value)
        => new()
        {
            Name = "t",
            Instrument = "i",
            Kind = kind,
            StatusRule = rule,
            Threshold = threshold,
            Comparison = comparison,
            BaselineDays = baselineDays,
            MaxDeviation = maxDeviation,
            DeviationDirection = direction,
        };

    private static MetricTileSpec Spec(string? filter = null, SloGaugeAggregate? aggregate = null)
    {
        var tile = new MetricBoardTile { Name = "t", Instrument = "i", TagFilter = filter, GaugeAggregate = aggregate };
        MetricTileSpec.TryCreate(tile, out var spec).ShouldBeTrue();

        return spec;
    }

    private static MetricTileSpec RatioSpec()
    {
        var tile = new MetricBoardTile { Name = "t", Instrument = "i", Kind = MetricTileKind.Ratio, RatioTag = "result", GoodValues = "ok", BadValues = "declined,error" };
        MetricTileSpec.TryCreate(tile, out var spec).ShouldBeTrue();

        return spec;
    }

    private static MetricBoardRow Row(MetricTier tier, DateTime bucket, long count = 1, double sum = 0, double? min = null, double? max = null, string tags = "-")
        => new()
        {
            Instrument = "i",
            Tier = tier,
            BucketStart = bucket,
            Tags = tags,
            Count = count,
            Sum = sum,
            Min = min,
            Max = max,
        };
}
