using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;

namespace Warp.Tests.Metrics;

/// <summary>
/// NoDb coverage for chart tiles (§8.34, spec 2026-11-19 batch 1): the stored query's grammar (<see cref="MetricChartQuery"/>:
/// only the metric page's control keys, never the range, filters in the one <c>MetricFilter</c> grammar, at most 2000
/// characters, stored canonical), the column clearing <see cref="MetricBoardValidation"/> applies per kind, and the
/// <see cref="MetricBoardOptions.Chart"/> builder's query generation and registration-time checks.
/// </summary>
[Trait("Category", "NoDb")]
public class MetricChartTileValidationTests
{
    [TimedTheory]
    [InlineData("range=7d", "range")]
    [InlineData("breakdown=status&from=2026-11-01T00:00:00.000Z", "from")]
    [InlineData("to=2026-11-01T00:00:00.000Z", "to")]
    public void TryValidate_ChartQueryWithARangeKey_IsRejected(string query, string key)
    {
        var tile = ChartTile(query);

        MetricBoardValidation.TryValidate(tile, MetricKind.Counter, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain($"the range ('{key}')");
    }

    [TimedTheory]
    [InlineData("board=1")]
    [InlineData("breakdown=status&foo=bar")]
    [InlineData("Breakdown=status")]
    public void TryValidate_ChartQueryWithAnUnknownKey_IsRejected(string query)
    {
        MetricBoardValidation.TryValidate(ChartTile(query), MetricKind.Counter, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain("is not read by the metric page");
    }

    [TimedTheory]
    [InlineData("filter=status")]
    [InlineData("filter=status:5*0")]
    [InlineData("filter=a*:1")]
    [InlineData("filter=status:5*&filter=status:4*")]
    [InlineData("filter=:500")]
    public void TryValidate_ChartQueryWithABadFilter_IsRejected(string query)
    {
        MetricBoardValidation.TryValidate(ChartTile(query), MetricKind.Counter, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain("filter");
    }

    [TimedTheory]
    [InlineData("compare=2d")]
    [InlineData("percentile=100")]
    [InlineData("percentile=p95")]
    [InlineData("view=table")]
    [InlineData("numerator=5*0")]
    [InlineData("minmax=yes")]
    [InlineData("rate=hour")]
    [InlineData("breakdown=")]
    [InlineData("breakdown=a&breakdown=b")]
    public void TryValidate_ChartQueryWithAValueThePageDoesNotRead_IsRejected(string query)
    {
        MetricBoardValidation.TryValidate(ChartTile(query), MetricKind.Histogram, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldStartWith("ChartQuery");
    }

    [TimedFact]
    public void TryValidate_ChartQueryOverTheLength_IsRejected()
    {
        var query = "breakdown=" + new string('a', MetricBoardValidation.MaxChartQueryLength);

        MetricBoardValidation.TryValidate(ChartTile(query), MetricKind.Counter, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain("2000");
    }

    [TimedFact]
    public void TryValidate_ChartQuery_IsStoredCanonical()
    {
        // Out of order, filters unsorted, a leading '?', '+' for a space, defaults spelled out: one canonical string.
        var tile = ChartTile("?rate=bucket&compare=7d,1d&filter=service.version:2.*&filter=outcome:error&view=values&percentile=95&application=shop+api&minmax=true");

        MetricBoardValidation.TryValidate(tile, MetricKind.Histogram, out var error).ShouldBeTrue(error);

        tile.ChartQuery.ShouldBe("application=shop%20api&filter=outcome%3Aerror&filter=service.version%3A2.%2A&compare=1d%2C7d&percentile=95&minmax=1");
    }

    [TimedFact]
    public void TryValidate_ChartQueryWithTheVersionPseudoTag_IsAccepted()
    {
        // The chart reads the series API, which injects {version}; only value/ratio tiles (the board read) reject it.
        var tile = ChartTile("breakdown=%7Bversion%7D&filter=%7Bversion%7D:2.*");

        MetricBoardValidation.TryValidate(tile, MetricKind.Counter, out var error).ShouldBeTrue(error);
    }

    [TimedFact]
    public void TryValidate_ChartWithoutAQuery_IsAcceptedWithANullQuery()
    {
        var tile = ChartTile("  ");

        MetricBoardValidation.TryValidate(tile, MetricKind.Gauge, out var error).ShouldBeTrue(error);

        tile.ChartQuery.ShouldBeNull();
    }

    [TimedFact]
    public void TryValidate_ChartWithAStatusRule_IsRejected()
    {
        var tile = ChartTile("breakdown=status");
        tile.StatusRule = MetricTileStatusRule.Threshold;
        tile.Threshold = 5;

        MetricBoardValidation.TryValidate(tile, MetricKind.Counter, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain("no status rule");
    }

    [TimedFact]
    public void TryValidate_Chart_ClearsEveryValueAndRatioColumn()
    {
        var tile = ChartTile("breakdown=status");
        tile.Application = "api";
        tile.TagFilter = "status=5*";
        tile.RatioTag = "result";
        tile.GoodValues = "ok";
        tile.BadValues = "error";
        tile.GaugeAggregate = SloGaugeAggregate.Max;
        tile.Comparison = SloComparison.AtLeast;
        tile.BaselineDays = 7;
        tile.MaxDeviation = 0.3;
        tile.DeviationDirection = MetricDeviationDirection.Drop;

        MetricBoardValidation.TryValidate(tile, MetricKind.Gauge, out var error).ShouldBeTrue(error);

        tile.Application.ShouldBeNull();
        tile.TagFilter.ShouldBeNull();
        tile.RatioTag.ShouldBeNull();
        tile.GoodValues.ShouldBeNull();
        tile.BadValues.ShouldBeNull();
        tile.GaugeAggregate.ShouldBeNull();
        tile.Comparison.ShouldBeNull();
        tile.Threshold.ShouldBeNull();
        tile.BaselineDays.ShouldBeNull();
        tile.MaxDeviation.ShouldBeNull();
        tile.DeviationDirection.ShouldBeNull();
        tile.ChartQuery.ShouldBe("breakdown=status");
    }

    [TimedTheory]
    [InlineData(MetricTileKind.Value)]
    [InlineData(MetricTileKind.Ratio)]
    public void TryValidate_ValueOrRatioTile_ClearsTheChartQuery(MetricTileKind kind)
    {
        var tile = new MetricBoardTile
        {
            Name = "Tile",
            Instrument = "c",
            Kind = kind,
            RatioTag = "result",
            GoodValues = "ok",
            BadValues = "error",
            ChartQuery = "breakdown=status",
        };

        MetricBoardValidation.TryValidate(tile, MetricKind.Counter, out var error).ShouldBeTrue(error);

        tile.ChartQuery.ShouldBeNull();
    }

    [TimedTheory]
    [InlineData(MetricKind.Counter)]
    [InlineData(MetricKind.UpDownCounter)]
    [InlineData(MetricKind.Histogram)]
    [InlineData(MetricKind.Gauge)]
    public void AcceptsInstrument_Chart_AcceptsEveryKind(MetricKind kind)
    {
        MetricBoardValidation.AcceptsInstrument(MetricTileKind.Chart, kind).ShouldBeTrue();
    }

    [TimedFact]
    public void Chart_TypedParameters_BuildTheCanonicalQuery()
    {
        var options = new MetricBoardOptions().Chart(
            "Checkout errors by version",
            "shop.checkout.completed",
            application: "shop-api",
            breakdown: "service.version",
            filter: "outcome=error*,region=eu",
            compare: "7d",
            rate: "minute");

        var tile = options.Seeded.ShouldHaveSingleItem();
        tile.Kind.ShouldBe(MetricTileKind.Chart);
        tile.StatusRule.ShouldBe(MetricTileStatusRule.None);
        tile.Application.ShouldBeNull();
        tile.TagFilter.ShouldBeNull();
        tile.ChartQuery.ShouldBe("application=shop-api&breakdown=service.version&filter=outcome%3Aerror%2A&filter=region%3Aeu&compare=7d&rate=minute");
    }

    [TimedFact]
    public void Chart_PercentileNumeratorAndMinMax_BuildTheirKeys()
    {
        var options = new MetricBoardOptions()
            .Chart("Latency p95", "http.server.request.duration", percentile: 95)
            .Chart("5xx share", "http.server.request.duration", breakdown: "http.response.status_code", numerator: "5*")
            .Chart("Pool", "db.pool", minMax: true);

        options.Seeded.Select(x => x.ChartQuery).ShouldBe(
        [
            "percentile=95",
            "breakdown=http.response.status_code&view=ratio&numerator=5%2A",
            "minmax=1",
        ]);
    }

    [TimedFact]
    public void Chart_BuiltQuery_RoundTripsThroughTheValidation()
    {
        var tile = new MetricBoardOptions()
            .Chart("All", "c", application: "a b", breakdown: "k", filter: "x=1*", compare: "1d,7d", percentile: 99, numerator: "2*", minMax: true, rate: "second")
            .Seeded
            .ShouldHaveSingleItem();
        var copy = ChartTile(tile.ChartQuery!);

        MetricBoardValidation.TryValidate(copy, MetricKind.Histogram, out var error).ShouldBeTrue(error);

        copy.ChartQuery.ShouldBe(tile.ChartQuery);
    }

    [TimedTheory]
    [InlineData("status", null, null)]
    [InlineData(null, "2d", null)]
    [InlineData(null, null, 100)]
    public void Chart_InvalidParameter_ThrowsAtRegistration(string? filter, string? compare, int? percentile)
    {
        Should.Throw<ArgumentException>(() => new MetricBoardOptions().Chart("Bad", "c", filter: filter, compare: compare, percentile: percentile));
    }

    [TimedFact]
    public void Chart_PastTheChartCap_Throws()
    {
        var options = new MetricBoardOptions();
        for (var i = 0; i < MetricBoardValidation.MaxChartTiles; i++)
        {
            options.Chart($"Chart {i}", $"c{i}");
        }

        // A value tile still fits; a thirteenth chart does not.
        options.Value("Volume", "c");
        var error = Should.Throw<ArgumentException>(() => options.Chart("One too many", "other"));

        error.Message.ShouldContain($"{MetricBoardValidation.MaxChartTiles} chart tiles");
    }

    [TimedFact]
    public void Chart_SameInstrumentAndQueryTwice_Throws()
    {
        var options = new MetricBoardOptions().Chart("By version", "c", breakdown: "service.version");

        Should.Throw<ArgumentException>(() => options.Chart("Again", "c", breakdown: "service.version"));
    }

    private static MetricBoardTile ChartTile(string query) => new()
    {
        Name = "Chart",
        Instrument = "c",
        Kind = MetricTileKind.Chart,
        ChartQuery = query,
    };
}
