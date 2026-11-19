using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for SLO tiles (§8.34, spec 2026-11-19 batch 2): the objective id is required, the tile adds no status
/// rule of its own, every column the kind does not use is cleared (instrument stored empty), and the
/// <see cref="MetricBoardOptions.Slo"/> builder's registration-time checks.
/// </summary>
[Trait("Category", "NoDb")]
public class MetricSloTileValidationTests
{
    [TimedFact]
    public void TryValidate_SloTile_ClearsEveryColumnTheKindDoesNotUse()
    {
        var tile = new MetricBoardTile
        {
            Name = "Checkout success",
            Kind = MetricTileKind.Slo,
            SloDefinitionId = 7,
            Instrument = "shop.checkout.completed",
            Application = "shop-api",
            TagFilter = "outcome=ok",
            ChartQuery = "breakdown=status",
            RatioTag = "outcome",
            GoodValues = "ok",
            BadValues = "error",
            GaugeAggregate = SloGaugeAggregate.Max,
        };

        MetricBoardValidation.TryValidate(tile, null, out var error).ShouldBeTrue(error);

        tile.SloDefinitionId.ShouldBe(7);
        tile.Instrument.ShouldBe(string.Empty);
        tile.Application.ShouldBeNull();
        tile.TagFilter.ShouldBeNull();
        tile.ChartQuery.ShouldBeNull();
        tile.RatioTag.ShouldBeNull();
        tile.GoodValues.ShouldBeNull();
        tile.BadValues.ShouldBeNull();
        tile.GaugeAggregate.ShouldBeNull();
        tile.Threshold.ShouldBeNull();
        tile.Comparison.ShouldBeNull();
        tile.BaselineDays.ShouldBeNull();
    }

    [TimedTheory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-3)]
    public void TryValidate_SloTileWithoutAnObjective_IsRejected(int? objective)
    {
        var tile = SloTile(objective);

        MetricBoardValidation.TryValidate(tile, null, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain("SloDefinitionId");
    }

    [TimedTheory]
    [InlineData(MetricTileStatusRule.Threshold)]
    [InlineData(MetricTileStatusRule.Deviation)]
    public void TryValidate_SloTileWithAStatusRule_IsRejected(MetricTileStatusRule rule)
    {
        var tile = SloTile(3);
        tile.StatusRule = rule;
        tile.Threshold = 0.5;
        tile.BaselineDays = 7;
        tile.MaxDeviation = 0.1;

        MetricBoardValidation.TryValidate(tile, null, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain("An SLO tile has no status rule");
    }

    [TimedTheory]
    [InlineData(MetricTileKind.Value)]
    [InlineData(MetricTileKind.Chart)]
    public void TryValidate_OtherKinds_ClearTheObjective(MetricTileKind kind)
    {
        var tile = new MetricBoardTile { Name = "Tile", Instrument = "c", Kind = kind, SloDefinitionId = 4 };

        MetricBoardValidation.TryValidate(tile, MetricKind.Counter, out var error).ShouldBeTrue(error);

        tile.SloDefinitionId.ShouldBeNull();
    }

    [TimedFact]
    public void Slo_Builder_AddsAnSloTileNamedAfterTheObjective()
    {
        var options = new MetricBoardOptions().Slo(" Checkout success ", application: "shop-api");

        var tile = options.Seeded.ShouldHaveSingleItem();
        tile.Kind.ShouldBe(MetricTileKind.Slo);
        tile.Name.ShouldBe("Checkout success");
        tile.StatusRule.ShouldBe(MetricTileStatusRule.None);
        options.SloSeeds[tile].ShouldBe(new SloTileSeed("Checkout success", "shop-api"));
    }

    [TimedTheory]
    [InlineData("")]
    [InlineData("   ")]
    public void Slo_Builder_WithoutAName_Throws(string name)
        => Should.Throw<ArgumentException>(() => new MetricBoardOptions().Slo(name));

    [TimedFact]
    public void Slo_Builder_TheSameObjectiveTwice_Throws()
    {
        var options = new MetricBoardOptions().Slo("Checkout success", "shop-api");

        Should.Throw<ArgumentException>(() => options.Slo("checkout SUCCESS", "shop-api")).Message.ShouldContain("names the same objective");
    }

    [TimedFact]
    public void Slo_Builder_TheSameNameInTwoApplications_IsAllowed()
    {
        var options = new MetricBoardOptions().Slo("Success", "shop-api", label: "Shop success").Slo("Success", "admin", label: "Admin success");

        options.Seeded.Select(x => x.Name).ShouldBe(["Shop success", "Admin success"]);
    }

    [TimedFact]
    public void Slo_Builder_ANameAnotherTileHas_Throws()
        => Should.Throw<ArgumentException>(() => new MetricBoardOptions().Value("Checkout success", "c").Slo("Checkout success"));

    private static MetricBoardTile SloTile(int? objective) => new()
    {
        Name = "Checkout success",
        Kind = MetricTileKind.Slo,
        SloDefinitionId = objective,
    };
}
