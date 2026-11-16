using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for <see cref="MetricBoardValidation"/> (#326) — the one tile validator shared by the dashboard upsert
/// and the seeder — and for the <see cref="MetricBoardOptions"/> builder that throws at registration.
/// </summary>
[Trait("Category", "NoDb")]
public class MetricBoardValidationTests
{
    [TimedFact]
    public void TryValidate_RatioOnAGauge_IsRejected()
    {
        var ok = MetricBoardValidation.TryValidate(RatioTile(), MetricKind.Gauge, out var error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("gauge");
    }

    [TimedTheory]
    [InlineData(MetricKind.Counter)]
    [InlineData(MetricKind.Histogram)]
    [InlineData(MetricKind.UpDownCounter)]
    public void TryValidate_RatioOnAnEventCountingInstrument_IsAccepted(MetricKind kind)
    {
        MetricBoardValidation.TryValidate(RatioTile(), kind, out _).ShouldBeTrue();
    }

    [TimedFact]
    public void TryValidate_ValueTileOnAGauge_DefaultsTheAggregateToAverage()
    {
        var tile = ValueTile();

        MetricBoardValidation.TryValidate(tile, MetricKind.Gauge, out _).ShouldBeTrue();

        tile.GaugeAggregate.ShouldBe(SloGaugeAggregate.Average);
    }

    [TimedFact]
    public void TryValidate_AggregateOnACounter_IsCleared()
    {
        var tile = ValueTile();
        tile.GaugeAggregate = SloGaugeAggregate.Max;

        MetricBoardValidation.TryValidate(tile, MetricKind.Counter, out _).ShouldBeTrue();

        tile.GaugeAggregate.ShouldBeNull();
    }

    [TimedTheory]
    [InlineData("status")]
    [InlineData("status=5*0")]
    [InlineData("a*=1")]
    [InlineData("a=1,a=2")]
    public void TryValidate_MalformedFilter_IsRejected(string filter)
    {
        var tile = ValueTile();
        tile.TagFilter = filter;

        MetricBoardValidation.TryValidate(tile, null, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldStartWith("TagFilter");
    }

    [TimedTheory]
    [InlineData("ok", "o*")]
    [InlineData("*", "error")]
    [InlineData("ok", "ok")]
    public void TryValidate_OverlappingGoodAndBad_IsRejected(string good, string bad)
    {
        var tile = RatioTile();
        tile.GoodValues = good;
        tile.BadValues = bad;

        MetricBoardValidation.TryValidate(tile, MetricKind.Counter, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void TryValidate_ThresholdRule_DefaultsToAtMostAndClearsDeviationFields()
    {
        var tile = ValueTile();
        tile.StatusRule = MetricTileStatusRule.Threshold;
        tile.Threshold = 0;
        tile.BaselineDays = 7;
        tile.MaxDeviation = 0.3;

        MetricBoardValidation.TryValidate(tile, MetricKind.Counter, out _).ShouldBeTrue();

        tile.Comparison.ShouldBe(SloComparison.AtMost);
        tile.BaselineDays.ShouldBeNull();
        tile.MaxDeviation.ShouldBeNull();
    }

    [TimedFact]
    public void TryValidate_ThresholdRuleWithoutAThreshold_IsRejected()
    {
        var tile = ValueTile();
        tile.StatusRule = MetricTileStatusRule.Threshold;

        MetricBoardValidation.TryValidate(tile, null, out _).ShouldBeFalse();
    }

    [TimedTheory]
    [InlineData(MetricTileKind.Value, MetricDeviationDirection.Drop, 3, 0.3, true)]
    [InlineData(MetricTileKind.Value, MetricDeviationDirection.Drop, 7, 1.0, false)]
    [InlineData(MetricTileKind.Value, MetricDeviationDirection.Rise, 7, 2.0, true)]
    [InlineData(MetricTileKind.Value, MetricDeviationDirection.Either, 7, 0.0, false)]
    [InlineData(MetricTileKind.Ratio, MetricDeviationDirection.Rise, 1, 1.5, false)]
    [InlineData(MetricTileKind.Ratio, MetricDeviationDirection.Drop, 1, 0.02, true)]
    public void TryValidate_DeviationRule_ChecksBaselineAndAllowance(MetricTileKind kind, MetricDeviationDirection direction, int baselineDays, double max, bool valid)
    {
        var tile = kind == MetricTileKind.Ratio ? RatioTile() : ValueTile();
        tile.StatusRule = MetricTileStatusRule.Deviation;
        tile.BaselineDays = baselineDays;
        tile.MaxDeviation = max;
        tile.DeviationDirection = direction;

        // BaselineDays 3 is never valid, whatever the allowance.
        var expected = valid && baselineDays is 1 or 7;

        MetricBoardValidation.TryValidate(tile, MetricKind.Counter, out _).ShouldBe(expected);
    }

    [TimedFact]
    public void TryValidate_NoRule_ClearsEveryRuleField()
    {
        var tile = ValueTile();
        tile.Threshold = 3;
        tile.Comparison = SloComparison.AtLeast;
        tile.MaxDeviation = 0.2;

        MetricBoardValidation.TryValidate(tile, null, out _).ShouldBeTrue();

        tile.Threshold.ShouldBeNull();
        tile.Comparison.ShouldBeNull();
        tile.MaxDeviation.ShouldBeNull();
    }

    [TimedFact]
    public void TryValidate_UndefinedStatusRule_IsRejected()
    {
        var tile = ValueTile();
        tile.StatusRule = (MetricTileStatusRule)99;

        MetricBoardValidation.TryValidate(tile, null, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void Options_DuplicateName_Throws()
    {
        var options = new MetricBoardOptions().Value("Volume", "orders");

        Should.Throw<ArgumentException>(() => options.Value("Volume", "payments"));
    }

    [TimedFact]
    public void Options_SameFieldsUnderAnotherName_Throws()
    {
        var options = new MetricBoardOptions().Value("Volume", "orders", tagFilter: "region=eu");

        Should.Throw<ArgumentException>(() => options.Value("Volume again", "orders", tagFilter: "region=eu"));
    }

    [TimedFact]
    public void Options_InvalidTile_Throws()
    {
        Should.Throw<ArgumentException>(() => new MetricBoardOptions().Ratio("Bad", "payments", "result", "ok", "ok"));
    }

    [TimedFact]
    public void Options_Check_IsAppliedToTheTile()
    {
        var options = new MetricBoardOptions()
            .Ratio("Success", "payments", "result", "ok", "error", check: MetricTileCheck.Deviation(0.02, 1, MetricDeviationDirection.Drop));

        var tile = options.Seeded.ShouldHaveSingleItem();
        tile.StatusRule.ShouldBe(MetricTileStatusRule.Deviation);
        tile.BaselineDays.ShouldBe(1);
        tile.MaxDeviation.ShouldBe(0.02);
        tile.DeviationDirection.ShouldBe(MetricDeviationDirection.Drop);
    }

    private static MetricBoardTile ValueTile() => new() { Name = "Volume", Instrument = "orders", Kind = MetricTileKind.Value };

    private static MetricBoardTile RatioTile() => new()
    {
        Name = "Success",
        Instrument = "payments",
        Kind = MetricTileKind.Ratio,
        RatioTag = "result",
        GoodValues = "ok",
        BadValues = "declined,error",
    };
}
