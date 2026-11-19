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

    [TimedTheory]
    [InlineData("blank name", "Name is required and must be at most 200 characters.")]
    [InlineData("long name", "Name is required and must be at most 200 characters.")]
    [InlineData("blank instrument", "Instrument is required and must be a valid instrument name of at most 128 characters.")]
    [InlineData("long instrument", "Instrument is required and must be a valid instrument name of at most 128 characters.")]
    [InlineData("negative position", "Position must be zero or greater.")]
    [InlineData("long application", "Application must be at most 200 characters.")]
    [InlineData("long filter", "TagFilter must be at most 512 characters.")]
    [InlineData("malformed filter", "TagFilter is invalid: Filter entry 'status' is missing '='.")]
    [InlineData("version filter", "TagFilter is invalid: the '{version}' key is reserved for the Metrics page")]
    [InlineData("undefined kind", "Kind '9' is not valid: use Value (1), Ratio (2), Chart (3) or Slo (4).")]
    [InlineData("ratio on gauge", "A ratio tile cannot use gauge instrument 'payments': gauges have no event count.")]
    [InlineData("undefined aggregate", "GaugeAggregate '9' is not valid: use Average (1), Min (2) or Max (3).")]
    [InlineData("missing ratio tag", "A ratio tile requires RatioTag (the tag whose value marks an event good or bad), at most 64 characters.")]
    [InlineData("long ratio tag", "A ratio tile requires RatioTag (the tag whose value marks an event good or bad), at most 64 characters.")]
    [InlineData("version ratio tag", "RatioTag is invalid: the '{version}' key is reserved for the Metrics page")]
    [InlineData("missing good", "A ratio tile requires non-empty GoodValues and BadValues of at most 512 characters each.")]
    [InlineData("long bad", "A ratio tile requires non-empty GoodValues and BadValues of at most 512 characters each.")]
    [InlineData("malformed good", "GoodValues is invalid: ")]
    [InlineData("malformed bad", "BadValues is invalid: ")]
    [InlineData("overlap", "GoodValues and BadValues must not overlap: no value may match a pattern in both lists.")]
    [InlineData("missing threshold", "A threshold rule requires a finite Threshold.")]
    [InlineData("infinite threshold", "A threshold rule requires a finite Threshold.")]
    [InlineData("undefined comparison", "Comparison '9' is not valid: use AtMost (1) or AtLeast (2).")]
    [InlineData("ratio threshold above 1", "A ratio tile's Threshold is a share between 0 and 1.")]
    [InlineData("ratio threshold below 0", "A ratio tile's Threshold is a share between 0 and 1.")]
    [InlineData("baseline 3", "A deviation rule requires BaselineDays of 1 or 7.")]
    [InlineData("undefined direction", "DeviationDirection '9' is not valid: use Drop (1), Rise (2) or Either (3).")]
    [InlineData("zero deviation", "A deviation rule requires a MaxDeviation greater than zero.")]
    [InlineData("ratio deviation of 1", "A ratio tile's MaxDeviation is in ratio points and must be below 1 (0.02 = 2 points).")]
    [InlineData("value drop of 1", "A value tile's MaxDeviation for a drop is a fraction of the baseline and must be below 1 (0.3 = 30 %).")]
    [InlineData("undefined rule", "StatusRule '9' is not valid: use None (1), Threshold (2) or Deviation (3).")]
    public void TryValidate_EachBranch_IsRejectedWithItsMessage(string branch, string expected)
    {
        var (tile, kind) = Invalid(branch);

        MetricBoardValidation.TryValidate(tile, kind, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldStartWith(expected);
    }

    [TimedTheory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void TryValidate_RatioThresholdAtTheShareBounds_IsAccepted(double threshold)
    {
        var tile = RatioTile();
        Threshold(tile, threshold, SloComparison.AtLeast);

        MetricBoardValidation.TryValidate(tile, MetricKind.Counter, out var error).ShouldBeTrue(error);
    }

    [TimedFact]
    public void TryValidate_ValueTileThresholdAboveOne_IsAccepted()
    {
        var tile = ValueTile();
        Threshold(tile, 250, SloComparison.AtMost);

        MetricBoardValidation.TryValidate(tile, MetricKind.Counter, out var error).ShouldBeTrue(error);
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
    public void Options_PastTheTileCap_Throws()
    {
        var options = new MetricBoardOptions();
        for (var i = 0; i < MetricBoardValidation.MaxTiles; i++)
        {
            options.Value($"t{i}", $"i{i}");
        }

        Should.Throw<ArgumentException>(() => options.Value("One too many", "other"))
            .Message.ShouldStartWith($"A metric board holds at most {MetricBoardValidation.MaxTiles} tiles.");
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

    private static (MetricBoardTile Tile, MetricKind? Kind) Invalid(string branch)
    {
        var ratio = branch is "ratio on gauge" or "missing ratio tag" or "long ratio tag" or "version ratio tag" or "missing good"
            or "long bad" or "malformed good" or "malformed bad" or "overlap" or "ratio deviation of 1" or "ratio threshold above 1" or "ratio threshold below 0";
        var tile = ratio ? RatioTile() : ValueTile();
        var kind = string.Equals(branch, "ratio on gauge", StringComparison.Ordinal) ? MetricKind.Gauge : MetricKind.Counter;
        Action<MetricBoardTile> breakIt = branch switch
        {
            "blank name" => x => x.Name = " ",
            "long name" => x => x.Name = new string('n', 201),
            "blank instrument" => x => x.Instrument = string.Empty,
            "long instrument" => x => x.Instrument = new string('i', 129),
            "negative position" => x => x.Position = -1,
            "long application" => x => x.Application = new string('a', 201),
            "long filter" => x => x.TagFilter = "k=" + new string('v', 511),
            "malformed filter" => x => x.TagFilter = "status",
            "version filter" => x => x.TagFilter = "{version}=2.*",
            "undefined kind" => x => x.Kind = (MetricTileKind)9,
            "ratio on gauge" => x => x.Position = 0,
            "undefined aggregate" => x => x.GaugeAggregate = (SloGaugeAggregate)9,
            "missing ratio tag" => x => x.RatioTag = null,
            "long ratio tag" => x => x.RatioTag = new string('r', 65),
            "version ratio tag" => x => x.RatioTag = "{version}",
            "missing good" => x => x.GoodValues = " ",
            "long bad" => x => x.BadValues = new string('b', 513),
            "malformed good" => x => x.GoodValues = "o*k",
            "malformed bad" => x => x.BadValues = "e*r",
            "overlap" => x => x.BadValues = "ok",
            "missing threshold" => x => x.StatusRule = MetricTileStatusRule.Threshold,
            "infinite threshold" => x => Threshold(x, double.PositiveInfinity, null),
            "undefined comparison" => x => Threshold(x, 1, (SloComparison)9),
            "ratio threshold above 1" => x => Threshold(x, 1.01, SloComparison.AtLeast),
            "ratio threshold below 0" => x => Threshold(x, -0.01, SloComparison.AtMost),
            "baseline 3" => x => Deviation(x, 3, 0.3, MetricDeviationDirection.Drop),
            "undefined direction" => x => Deviation(x, 1, 0.3, (MetricDeviationDirection)9),
            "zero deviation" => x => Deviation(x, 1, 0, MetricDeviationDirection.Drop),
            "ratio deviation of 1" => x => Deviation(x, 1, 1, MetricDeviationDirection.Rise),
            "value drop of 1" => x => Deviation(x, 7, 1, MetricDeviationDirection.Drop),
            "undefined rule" => x => x.StatusRule = (MetricTileStatusRule)9,
            _ => throw new ArgumentOutOfRangeException(nameof(branch), branch, "Unknown branch."),
        };
        breakIt(tile);

        return (tile, kind);
    }

    [TimedFact]
    public void TryValidateBoard_Valid_TrimsAndClearsBlanks()
    {
        var board = new MetricBoard { Name = "  Checkout ", Description = " ", Application = " shop-api " };

        MetricBoardValidation.TryValidateBoard(board, out var error).ShouldBeTrue(error);

        board.Name.ShouldBe("Checkout");
        board.Description.ShouldBeNull();
        board.Application.ShouldBe("shop-api");
    }

    [TimedTheory]
    [InlineData("blank-name")]
    [InlineData("long-name")]
    [InlineData("long-description")]
    [InlineData("long-application")]
    [InlineData("negative-position")]
    public void TryValidateBoard_Invalid_IsRejected(string scenario)
    {
        var board = new MetricBoard { Name = "Checkout" };
        switch (scenario)
        {
            case "blank-name":
                board.Name = "   ";
                break;
            case "long-name":
                board.Name = new string('n', MetricBoardValidation.MaxBoardNameLength + 1);
                break;
            case "long-description":
                board.Description = new string('d', MetricBoardValidation.MaxBoardDescriptionLength + 1);
                break;
            case "long-application":
                board.Application = new string('a', 201);
                break;
            default:
                board.Position = -1;
                break;
        }

        MetricBoardValidation.TryValidateBoard(board, out var error).ShouldBeFalse();

        error.ShouldNotBeNullOrEmpty();
    }

    [TimedTheory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-3, null)]
    [InlineData(7, 7)]
    public void TryValidate_BoardId_NonPositiveMeansTheDefaultBoard(int? boardId, int? expected)
    {
        var tile = ValueTile();
        tile.BoardId = boardId;

        MetricBoardValidation.TryValidate(tile, MetricKind.Counter, out _).ShouldBeTrue();

        tile.BoardId.ShouldBe(expected);
    }

    private static void Threshold(MetricBoardTile tile, double threshold, SloComparison? comparison)
    {
        tile.StatusRule = MetricTileStatusRule.Threshold;
        tile.Threshold = threshold;
        tile.Comparison = comparison;
    }

    private static void Deviation(MetricBoardTile tile, int baselineDays, double max, MetricDeviationDirection direction)
    {
        tile.StatusRule = MetricTileStatusRule.Deviation;
        tile.BaselineDays = baselineDays;
        tile.MaxDeviation = max;
        tile.DeviationDirection = direction;
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
