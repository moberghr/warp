using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.Observability;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Metrics;

/// <summary>
/// Database coverage for the metrics health board (#326): tile CRUD and the tile cap, the insert-if-absent seeder (a
/// dashboard edit survives a reseed, a ratio on a gauge is skipped), the batched board read — now / 1d / 7d values, a
/// ratio tile, a gauge tile, threshold and deviation status, empty windows and unknown instruments, a read across the
/// fine and hourly tiers, the application scope — the routes with their 400s, and a dashboard-only host (AddWarp without
/// AddMetrics) that seeds and serves the board. "Now" is a fixed instant; rows are seeded relative to it.
/// </summary>
[GenerateDatabaseTests]
public abstract class MetricBoardTestsBase : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly IDatabaseFixture _fixture;

    protected MetricBoardTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Upsert_Insert_AppendsAfterTheLastTileAndIsListed()
    {
        await SeedTilesAsync(Tile("First", "c", position: 4));

        var id = await Command().Upsert(Tile("Second", "c", position: 0), Ct);

        id.ShouldNotBeNull();
        var tiles = await Query().GetTiles(Ct);
        tiles.Select(x => (x.Name, x.Position)).ShouldBe([("First", 4), ("Second", 5)]);
        tiles[1].Id.ShouldBe(id.Value);
    }

    [TimedFact]
    public async Task Upsert_Update_ReplacesTheFieldsIncludingClearedOnes()
    {
        var id = await Command().Upsert(Tile("Volume", "c", rule: MetricTileStatusRule.Threshold, threshold: 5, comparison: SloComparison.AtMost), Ct);

        await Command().Upsert(
            new MetricBoardTile
            {
                Id = id!.Value,
                Name = "Success",
                Instrument = "p",
                Position = 2,
                Kind = MetricTileKind.Ratio,
                RatioTag = "result",
                GoodValues = "ok",
                BadValues = "error",
                StatusRule = MetricTileStatusRule.None,
            },
            Ct);

        var tile = (await Query().GetTiles(Ct)).ShouldHaveSingleItem();
        tile.Name.ShouldBe("Success");
        tile.Kind.ShouldBe(MetricTileKind.Ratio);
        tile.RatioTag.ShouldBe("result");
        tile.StatusRule.ShouldBe(MetricTileStatusRule.None);
        tile.Threshold.ShouldBeNull();
        tile.Comparison.ShouldBeNull();
    }

    [TimedFact]
    public async Task Upsert_InsertPastTheTileCap_ReturnsNullAndAddsNothing()
    {
        await SeedTilesAsync([.. Enumerable.Range(0, MetricBoardValidation.MaxTiles).Select(x => Tile($"t{x}", "c", position: x + 1))]);

        var id = await Command().Upsert(Tile("One too many", "c"), Ct);

        id.ShouldBeNull();
        (await _fixture.CreateContext().Set<MetricBoardTile>().CountAsync(Ct)).ShouldBe(MetricBoardValidation.MaxTiles);
    }

    [TimedFact]
    public async Task Upsert_EditOfADeletedTile_ReturnsZeroAndRecreatesNothing()
    {
        var id = await Command().Upsert(Tile("Volume", "c"), Ct);
        (await Command().Delete(id!.Value, Ct)).ShouldBeTrue();
        var edit = Tile("Volume edited", "c");
        edit.Id = id.Value;

        var result = await Command().Upsert(edit, Ct);

        result.ShouldBe(0);
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Upsert_EditOfATileOnAFullBoard_IsNotCapped()
    {
        await SeedTilesAsync([.. Enumerable.Range(0, MetricBoardValidation.MaxTiles).Select(x => Tile($"t{x}", "c", position: x + 1))]);
        var stored = await _fixture.CreateContext().Set<MetricBoardTile>().Where(x => x.Name == "t0").SingleAsync(Ct);
        var edit = Tile("t0 renamed", "c", position: 1);
        edit.Id = stored.Id;

        var result = await Command().Upsert(edit, Ct);

        result.ShouldBe(stored.Id);
        (await _fixture.CreateContext().Set<MetricBoardTile>().Where(x => x.Name == "t0 renamed").CountAsync(Ct)).ShouldBe(1);
    }

    [TimedFact]
    public async Task Delete_ExistingAndUnknown_RemovesOnlyTheExistingOne()
    {
        var id = await Command().Upsert(Tile("Volume", "c"), Ct);

        (await Command().Delete(id!.Value, Ct)).ShouldBeTrue();
        (await Command().Delete(id.Value, Ct)).ShouldBeFalse();
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Seeder_FreshBoard_InsertsInDeclarationOrder_AndReseedIsIdempotent()
    {
        static void Configure(MetricBoardOptions b) => b
            .Value("Volume", "c")
            .Ratio("Success", "p", "result", "ok", "error", check: MetricTileCheck.Deviation(0.02, 1, MetricDeviationDirection.Drop));

        await SeedFromConfigAsync(Configure);
        await SeedFromConfigAsync(Configure);

        var tiles = await Query().GetTiles(Ct);
        tiles.Select(x => (x.Name, x.Position)).ShouldBe([("Volume", 1), ("Success", 2)]);
        tiles[1].MaxDeviation.ShouldBe(0.02);
        tiles[1].DeviationDirection.ShouldBe(MetricDeviationDirection.Drop);
    }

    [TimedFact]
    public async Task Seeder_TileRenamedAndRuleEditedInTheDashboard_ReseedKeepsOneRowWithTheEdit()
    {
        static void Configure(MetricBoardOptions b) => b.Value("5xx", "http", tagFilter: "status=5*", check: MetricTileCheck.AtMost(0));

        await SeedFromConfigAsync(Configure);
        var ctx = _fixture.CreateContext();
        var row = await ctx.Set<MetricBoardTile>().SingleAsync(Ct);
        row.Name = "Server errors";
        row.Threshold = 10;
        await ctx.SaveChangesAsync(Ct);

        await SeedFromConfigAsync(Configure);

        var tile = (await Query().GetTiles(Ct)).ShouldHaveSingleItem();
        tile.Name.ShouldBe("Server errors");
        tile.Threshold.ShouldBe(10);
    }

    [TimedFact]
    public async Task Seeder_TwoReplicasStartAtOnce_SeedOneCopy()
    {
        // Two replicas with the same config. The first is pinned inside the board lock after it read the empty board
        // (BarrierSignal, N=2); the second must not get to read the board until the first commits, or both insert.
        static void Configure(MetricBoardOptions b) => b.Value("Volume", "c").Value("Errors", "e");
        var barrier = new BarrierSignal();

        async Task Pin(CancellationToken ct)
        {
            barrier.Running.Release();
            await barrier.CanFinish.WaitAsync(ct);
        }

        var first = SeedFromConfigAsync(Configure, Pin);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
        var second = SeedFromConfigAsync(Configure, Pin);

        // The second replica is held at the lock, so it cannot reach the pin while the first is inside.
        (await barrier.Running.WaitAsync(TimeSpan.FromMilliseconds(500), Ct)).ShouldBeFalse();
        barrier.CanFinish.Release(2);
        await Task.WhenAll(first, second);

        var tiles = await Query().GetTiles(Ct);
        tiles.Select(x => x.Name).ShouldBe(["Volume", "Errors"]);
    }

    [TimedFact]
    public async Task Seeder_StoredTileWithTheSameNameButOtherFields_IsKeptAndTheSkipIsLogged()
    {
        await SeedTilesAsync(Tile("Errors", "http", rule: MetricTileStatusRule.Threshold, threshold: 10, comparison: SloComparison.AtMost));
        var logs = new CapturingLoggerProvider();

        await SeedFromConfigAsync(b => b.Value("Errors", "http.server", tagFilter: "status=5*", check: MetricTileCheck.AtMost(5)), logs: logs);

        var tile = (await Query().GetTiles(Ct)).ShouldHaveSingleItem();
        tile.Instrument.ShouldBe("http");
        tile.Threshold.ShouldBe(10);
        var log = logs.Logs.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Information);
        log.Fields["Name"].ShouldBe("Errors");
        log.Fields["Kind"].ShouldBe(MetricTileKind.Value);
        log.Fields["Fields"].ShouldBe("Instrument, TagFilter, Threshold");

        // Field names only: never the filter's tag values (§1.2).
        log.Message.ShouldNotContain("status=5*");
    }

    [TimedFact]
    public async Task Seeder_BoardAtTheCap_SkipsTheSeed()
    {
        await SeedTilesAsync([.. Enumerable.Range(0, MetricBoardValidation.MaxTiles).Select(x => Tile($"t{x}", "c", position: x + 1))]);

        await SeedFromConfigAsync(b => b.Value("One too many", "other"));

        (await _fixture.CreateContext().Set<MetricBoardTile>().CountAsync(Ct)).ShouldBe(MetricBoardValidation.MaxTiles);
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(x => x.Name == "One too many", Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Seeder_RatioOnAGaugeInstrument_IsSkipped_AndTheValidSeedStillInserts()
    {
        await SeedAsync([Instrument("pool", MetricKind.Gauge)], []);

        await SeedFromConfigAsync(b => b
            .Ratio("Pool ratio", "pool", "state", "idle", "busy")
            .Value("Pool level", "pool", aggregate: SloGaugeAggregate.Max));

        var tile = (await Query().GetTiles(Ct)).ShouldHaveSingleItem();
        tile.Name.ShouldBe("Pool level");
        tile.GaugeAggregate.ShouldBe(SloGaugeAggregate.Max);
    }

    [TimedFact]
    public async Task GetBoard_CounterTile_ReadsNowTheDayBeforeAndTheWeekBefore()
    {
        var dayAgo = Now.AddDays(-1);
        var weekAgo = Now.AddDays(-7);
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", Now.AddMinutes(-50), sum: 10),
                Point("c", Now.AddMinutes(-20), sum: 20),
                Point("c", dayAgo.AddHours(-1), sum: 40, tier: MetricTier.Hourly),
                Point("c", dayAgo.AddHours(-2), sum: 999, tier: MetricTier.Hourly),
                Point("c", weekAgo.AddHours(-1), sum: 50, tier: MetricTier.Hourly),
            ]);
        await SeedTilesAsync(Tile("Volume", "c"));

        var board = await Query().GetBoard(Now.AddHours(-1), Now, Ct);

        board.Resolution.ShouldBe(MetricTier.Fine);
        var tile = board.Tiles.ShouldHaveSingleItem();
        tile.InstrumentKind.ShouldBe(MetricKind.Counter);
        tile.Current.ShouldBe(30);
        tile.DayAgo.ShouldBe(40);
        tile.WeekAgo.ShouldBe(50);
        tile.Status.ShouldBeNull();
        tile.Sparkline.Select(x => (x.Timestamp, x.Value)).ShouldBe([(Now.AddMinutes(-50), (double?)10), (Now.AddMinutes(-20), (double?)20)]);
    }

    [TimedFact]
    public async Task GetBoard_RatioTileWithADropRule_ComputesTheRatioAndBreachesOnTheDrop()
    {
        await SeedAsync(
            [Instrument("p", MetricKind.Counter)],
            [
                Point("p", Now.AddMinutes(-30), sum: 90, tags: "result=ok"),
                Point("p", Now.AddMinutes(-30), sum: 10, tags: "result=error"),
                Point("p", Now.AddMinutes(-30), sum: 1000, tags: "result=pending"),
                Point("p", Now.AddDays(-1).AddHours(-1), sum: 99, tags: "result=ok", tier: MetricTier.Hourly),
                Point("p", Now.AddDays(-1).AddHours(-1), sum: 1, tags: "result=error", tier: MetricTier.Hourly),
            ]);
        await SeedTilesAsync(new MetricBoardTile
        {
            Name = "Success",
            Instrument = "p",
            Kind = MetricTileKind.Ratio,
            RatioTag = "result",
            GoodValues = "ok",
            BadValues = "error",
            StatusRule = MetricTileStatusRule.Deviation,
            BaselineDays = 1,
            MaxDeviation = 0.05,
            DeviationDirection = MetricDeviationDirection.Drop,
        });

        var tile = (await Query().GetBoard(Now.AddHours(-1), Now, Ct)).Tiles.ShouldHaveSingleItem();

        tile.Current.ShouldBe(0.9);
        tile.DayAgo.ShouldBe(0.99);
        tile.WeekAgo.ShouldBeNull();
        tile.Status.ShouldBe(SloState.Breaching);
    }

    [TimedTheory]
    [InlineData(SloGaugeAggregate.Max, 50.0, SloState.Breaching)]
    [InlineData(SloGaugeAggregate.Average, 20.0, SloState.Healthy)]
    public async Task GetBoard_GaugeTileWithAThreshold_HoldsTheAggregateToIt(SloGaugeAggregate aggregate, double expected, SloState state)
    {
        await SeedAsync(
            [Instrument("pool", MetricKind.Gauge)],
            [
                Point("pool", Now.AddMinutes(-40), count: 2, sum: 20, min: 5, max: 15),
                Point("pool", Now.AddMinutes(-10), count: 2, sum: 60, min: 10, max: 50),
            ]);
        await SeedTilesAsync(Tile("Pool", "pool", rule: MetricTileStatusRule.Threshold, threshold: 40, comparison: SloComparison.AtMost, aggregate: aggregate));

        var tile = (await Query().GetBoard(Now.AddHours(-1), Now, Ct)).Tiles.ShouldHaveSingleItem();

        tile.Current.ShouldBe(expected);
        tile.Status.ShouldBe(state);
    }

    [TimedTheory]
    [InlineData(25.0, SloState.Healthy)]
    [InlineData(200.0, SloState.Breaching)]
    public async Task GetBoard_ValueTileWithADeviationRule_ComparesWithTheWeekBefore(double current, SloState expected)
    {
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", Now.AddMinutes(-30), sum: current),
                Point("c", Now.AddDays(-7).AddHours(-1), sum: 30, tier: MetricTier.Hourly),
            ]);
        await SeedTilesAsync(Tile("Volume", "c", rule: MetricTileStatusRule.Deviation, baselineDays: 7, maxDeviation: 0.5));

        var tile = (await Query().GetBoard(Now.AddHours(-1), Now, Ct)).Tiles.ShouldHaveSingleItem();

        tile.Status.ShouldBe(expected);
    }

    [TimedFact]
    public async Task GetBoard_InstrumentWithNoRowsInAnyWindow_IsEmptyAndNoData()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddDays(-3), sum: 5)]);
        await SeedTilesAsync(Tile("Volume", "c", rule: MetricTileStatusRule.Threshold, threshold: 10));

        var tile = (await Query().GetBoard(Now.AddHours(-1), Now, Ct)).Tiles.ShouldHaveSingleItem();

        tile.Current.ShouldBeNull();
        tile.DayAgo.ShouldBeNull();
        tile.WeekAgo.ShouldBeNull();
        tile.Sparkline.ShouldBeEmpty();
        tile.Status.ShouldBe(SloState.NoData);
    }

    [TimedFact]
    public async Task GetBoard_UnknownInstrument_IsNoDataWithoutAKind()
    {
        await SeedTilesAsync(Tile("Ghost", "missing", rule: MetricTileStatusRule.Threshold, threshold: 1));

        var tile = (await Query().GetBoard(Now.AddHours(-1), Now, Ct)).Tiles.ShouldHaveSingleItem();

        tile.InstrumentKind.ShouldBeNull();
        tile.Current.ShouldBeNull();
        tile.Status.ShouldBe(SloState.NoData);
    }

    [TimedFact]
    public async Task GetBoard_EmptyBoard_ReturnsNoTiles()
    {
        var board = await Query().GetBoard(Now.AddHours(-1), Now, Ct);

        board.Tiles.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task GetBoard_DayWindowAcrossHourlyAndFineRows_SumsEachRowOnceAndBinsTheSparklineByHour()
    {
        // Hourly rows for the hours older than 6h, fine rows for the last 6h (rollup has not moved them yet).
        var points = new List<MetricPoint>();
        var start = Now.AddDays(-1);
        for (var i = 0; i < 18; i++)
        {
            points.Add(Point("c", start.AddHours(i), sum: 10, tier: MetricTier.Hourly));
        }

        for (var minutes = 360; minutes > 0; minutes -= 30)
        {
            points.Add(Point("c", Now.AddMinutes(-minutes), sum: 1));
        }

        await SeedAsync([Instrument("c", MetricKind.Counter)], [.. points]);
        await SeedTilesAsync(Tile("Volume", "c"));

        var board = await Query().GetBoard(start, Now, Ct);

        board.Resolution.ShouldBe(MetricTier.Hourly);
        var tile = board.Tiles.ShouldHaveSingleItem();
        tile.Current.ShouldBe((18 * 10) + 12);
        tile.Sparkline.Count.ShouldBe(24);
        tile.Sparkline[^1].ShouldSatisfyAllConditions(
            x => x.Timestamp.ShouldBe(Now.AddHours(-1)),
            x => x.Value.ShouldBe(2));
    }

    [TimedFact]
    public async Task GetBoard_ApplicationScopedTile_ReadsOnlyThatApplicationsRows()
    {
        await SeedAsync(
            [Instrument("c", MetricKind.Counter)],
            [
                Point("c", Now.AddMinutes(-30), sum: 3, application: "shop"),
                Point("c", Now.AddMinutes(-30), sum: 100, application: "admin"),
            ]);
        await SeedTilesAsync(Tile("Shop", "c", application: "shop"), Tile("All", "c", position: 2));

        var board = await Query().GetBoard(Now.AddHours(-1), Now, Ct);

        board.Tiles.Select(x => (x.Tile.Name, x.Current)).ShouldBe([("Shop", (double?)3), ("All", (double?)103)]);
    }

    [TimedFact]
    public async Task GetBoard_FilteredTileBesideAnUnfilteredOne_EachReadsItsOwnSlice()
    {
        await SeedAsync(
            [Instrument("http", MetricKind.Histogram)],
            [
                Point("http", Now.AddMinutes(-30), count: 90, sum: 1, tags: "status=200"),
                Point("http", Now.AddMinutes(-30), count: 4, sum: 1, tags: "status=503"),
            ]);
        await SeedTilesAsync(Tile("Requests", "http"), Tile("5xx", "http", filter: "status=5*", position: 2));

        var board = await Query().GetBoard(Now.AddHours(-1), Now, Ct);

        board.Tiles.Select(x => x.Current).ShouldBe([94, 4]);
    }

    [TimedTheory]
    [InlineData(60, 60)]
    [InlineData(30, 60)]
    [InlineData(91 * 24 * 60, 0)]
    public async Task GetBoard_EmptyInvertedOrOverlongRange_Throws(int fromMinutesAgo, int toMinutesAgo)
    {
        await Should.ThrowAsync<MetricQueryValidationException>(() => Query().GetBoard(Now.AddMinutes(-fromMinutesAgo), Now.AddMinutes(-toMinutesAgo), Ct));
    }

    [TimedFact]
    public async Task GetMetricBoard_Route_ReturnsTheEvaluatedTiles()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddMinutes(-10), sum: 7)]);
        await SeedTilesAsync(Tile("Volume", "c", rule: MetricTileStatusRule.Threshold, threshold: 5));
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.GetAsync($"/warp/api/metrics/board?{Range(Now.AddHours(-1), Now)}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var tile = json.RootElement.GetProperty("tiles").EnumerateArray().ShouldHaveSingleItem();
        tile.GetProperty("tile").GetProperty("name").GetString().ShouldBe("Volume");
        tile.GetProperty("current").GetDouble().ShouldBe(7);
        tile.GetProperty("status").GetInt32().ShouldBe((int)SloState.Breaching);
    }

    [TimedTheory]
    [InlineData("")]
    [InlineData("from=2026-11-13T10:00:00Z")]
    [InlineData("from=nonsense&to=2026-11-13T10:00:00Z")]
    [InlineData("from=2026-11-13T10:00:00Z&to=2026-11-13T09:00:00Z")]
    [InlineData("from=2026-01-01T00:00:00Z&to=2026-11-13T10:00:00Z")]
    [InlineData("from=0001-01-01T00:00:00Z&to=0001-01-02T00:00:00Z")]
    public async Task GetMetricBoard_InvalidRange_ReturnsBadRequest(string queryString)
    {
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.GetAsync($"/warp/api/metrics/board?{queryString}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TimedFact]
    public async Task PostTile_Valid_PersistsTheNormalizedTile()
    {
        await SeedAsync([Instrument("pool", MetricKind.Gauge)], []);
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.PostAsJsonAsync(
            "/warp/api/metrics/board/tiles",
            new { id = 0, name = "Pool", instrument = "pool", kind = (int)MetricTileKind.Value, statusRule = (int)MetricTileStatusRule.Threshold, threshold = 40.0 },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tile = (await Query().GetTiles(Ct)).ShouldHaveSingleItem();
        tile.GaugeAggregate.ShouldBe(SloGaugeAggregate.Average);
        tile.Comparison.ShouldBe(SloComparison.AtMost);
    }

    [TimedTheory]
    [InlineData("ratio-on-gauge")]
    [InlineData("bad-filter")]
    [InlineData("no-name")]
    [InlineData("bad-baseline")]
    public async Task PostTile_Invalid_ReturnsBadRequestAndPersistsNothing(string scenario)
    {
        await SeedAsync([Instrument("pool", MetricKind.Gauge), Instrument("c", MetricKind.Counter)], []);
        await using var host = await CreateDashboardHostAsync();
        object body = scenario switch
        {
            "ratio-on-gauge" => new { id = 0, name = "R", instrument = "pool", kind = (int)MetricTileKind.Ratio, ratioTag = "s", goodValues = "a", badValues = "b" },
            "bad-filter" => new { id = 0, name = "F", instrument = "c", tagFilter = "status" },
            "no-name" => new { id = 0, name = string.Empty, instrument = "c" },
            _ => new { id = 0, name = "D", instrument = "c", statusRule = (int)MetricTileStatusRule.Deviation, baselineDays = 3, maxDeviation = 0.2 },
        };

        var response = await host.Client.PostAsJsonAsync("/warp/api/metrics/board/tiles", body, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task PostTile_PastTheTileCap_ReturnsBadRequest()
    {
        await SeedTilesAsync([.. Enumerable.Range(0, MetricBoardValidation.MaxTiles).Select(x => Tile($"t{x}", "c", position: x + 1))]);
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.PostAsJsonAsync("/warp/api/metrics/board/tiles", new { id = 0, name = "Extra", instrument = "c" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TimedFact]
    public async Task PostTile_EditOfADeletedTile_ReturnsNotFoundAndRecreatesNothing()
    {
        var id = await Command().Upsert(Tile("Volume", "c"), Ct);
        (await Command().Delete(id!.Value, Ct)).ShouldBeTrue();
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.PostAsJsonAsync("/warp/api/metrics/board/tiles", new { id, name = "Volume", instrument = "c" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task DeleteTile_Route_RemovesItThenReportsNotFound()
    {
        var id = await Command().Upsert(Tile("Volume", "c"), Ct);
        await using var host = await CreateDashboardHostAsync();

        var first = await host.Client.DeleteAsync($"/warp/api/metrics/board/tiles/{id}", Ct);
        var second = await host.Client.DeleteAsync($"/warp/api/metrics/board/tiles/{id}", Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [TimedFact]
    public async Task GetTiles_Route_ListsTheTilesInBoardOrder()
    {
        await SeedTilesAsync(Tile("B", "c", position: 2), Tile("A", "c", position: 1));
        await using var host = await CreateDashboardHostAsync();

        var tiles = await host.Client.GetFromJsonAsync<JsonElement>("/warp/api/metrics/board/tiles", Ct);

        tiles.EnumerateArray().Select(x => x.GetProperty("name").GetString()).ShouldBe(["A", "B"]);
    }

    [TimedFact]
    public async Task DashboardOnlyHost_AddWarpWithoutAddMetrics_SeedsAndServesTheBoard()
    {
        // A host that registers the dashboard API but no worker, no server and no AddMetrics: the board still resolves,
        // the seeder still runs, and the read is served from metric_point rows another process wrote.
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddMinutes(-10), sum: 4)]);
        await using var probe = _fixture.CreateContext();
        var connectionString = probe.Database.GetConnectionString()!;
        var isPostgres = probe.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(Now)));
        builder.Services.AddDbContext<TestContext>(x =>
        {
            if (isPostgres)
            {
                x.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
            }
            else
            {
                x.UseSqlServer(connectionString);
            }
        });
        builder.Services.AddWarp<TestContext>(x => x.AddMetricBoard(b => b.Value("Volume", "c", check: MetricTileCheck.AtMost(10))));
        await using var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(Ct);
        try
        {
            app.Services.GetService<Warp.Core.Metrics.IMetricsMarker>().ShouldBeNull();
            using var client = app.GetTestClient();

            var board = await client.GetFromJsonAsync<JsonElement>($"/warp/api/metrics/board?{Range(Now.AddHours(-1), Now)}", Ct);

            var tile = board.GetProperty("tiles").EnumerateArray().ShouldHaveSingleItem();
            tile.GetProperty("current").GetDouble().ShouldBe(4);
            tile.GetProperty("status").GetInt32().ShouldBe((int)SloState.Healthy);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static MetricBoardTile Tile(
        string name,
        string instrument,
        int position = 1,
        string? application = null,
        string? filter = null,
        MetricTileStatusRule rule = MetricTileStatusRule.None,
        double? threshold = null,
        SloComparison? comparison = null,
        SloGaugeAggregate? aggregate = null,
        int? baselineDays = null,
        double? maxDeviation = null)
        => new()
        {
            Name = name,
            Instrument = instrument,
            Position = position,
            Application = application,
            TagFilter = filter,
            Kind = MetricTileKind.Value,
            GaugeAggregate = aggregate,
            StatusRule = rule,
            Threshold = threshold,
            Comparison = comparison,
            BaselineDays = baselineDays,
            MaxDeviation = maxDeviation,
        };

    private static MetricInstrument Instrument(string name, MetricKind kind)
        => new()
        {
            Name = name,
            Meter = "test",
            Kind = kind,
            FirstSeenAt = Now.AddDays(-10),
            LastSeenAt = Now,
        };

    private static MetricPoint Point(
        string instrument,
        DateTime bucket,
        long count = 1,
        double sum = 0,
        double? min = null,
        double? max = null,
        string tags = "-",
        string? application = null,
        MetricTier tier = MetricTier.Fine)
        => new()
        {
            Instrument = instrument,
            Application = application,
            Tags = tags,
            Tier = tier,
            BucketStart = bucket,
            Count = count,
            Sum = sum,
            Min = min,
            Max = max,
            Compacted = tier != MetricTier.Fine,
        };

    private static string Range(DateTime from, DateTime to)
        => $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    private MetricBoardQueryService<TestContext> Query()
        => new(_fixture.CreateContext(), Options.Create(new WarpConfiguration()), new FakeTimeProvider(new DateTimeOffset(Now)));

    private MetricBoardCommandService<TestContext> Command()
    {
        var context = _fixture.CreateContext();

        return new(context, TimeProvider.System, NullLogger<MetricBoardCommandService<TestContext>>.Instance, TestTasks.QueriesFor(context));
    }

    private async Task SeedAsync(MetricInstrument[] instruments, MetricPoint[] points)
    {
        var context = _fixture.CreateContext();
        context.Set<MetricInstrument>().AddRange(instruments);
        context.Set<MetricPoint>().AddRange(points);
        await context.SaveChangesAsync(Ct);
    }

    private async Task SeedTilesAsync(params MetricBoardTile[] tiles)
    {
        var context = _fixture.CreateContext();
        foreach (var tile in tiles)
        {
            MetricBoardValidation.TryValidate(tile, null, out var error).ShouldBeTrue(error);
            tile.CreatedAt = Now;
            tile.UpdatedAt = Now;
        }

        context.Set<MetricBoardTile>().AddRange(tiles);
        await context.SaveChangesAsync(Ct);
    }

    private async Task SeedFromConfigAsync(
        Action<MetricBoardOptions> configure,
        Func<CancellationToken, Task>? beforeSave = null,
        CapturingLoggerProvider? logs = null)
    {
        var options = new MetricBoardOptions();
        configure(options);
        var services = new ServiceCollection();
        services.AddScoped(_ => _fixture.CreateContext());
        services.AddScoped<IMetricQueryService>(_ => new MetricQueryService<TestContext>(_fixture.CreateContext(), Options.Create(new WarpConfiguration())));
        services.AddSingleton(TestTasks.QueriesFor(_fixture.CreateContext()));
        await using var provider = services.BuildServiceProvider();
        var logger = logs is null
            ? NullLogger<MetricBoardSeeder<TestContext>>.Instance
            : new CapturingLoggerFactory(logs).CreateLogger<MetricBoardSeeder<TestContext>>();
        var seeder = new MetricBoardSeeder<TestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            logger)
        {
            BeforeSave = beforeSave,
        };

        await seeder.StartAsync(Ct);
    }

    private async Task<DashboardHost> CreateDashboardHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        builder.Services.AddScoped<IMetricQueryService>(_ => new MetricQueryService<TestContext>(_fixture.CreateContext(), Options.Create(new WarpConfiguration())));
        builder.Services.AddScoped<IMetricBoardQueryService>(_ => Query());
        builder.Services.AddScoped<IMetricBoardCommandService>(_ => Command());

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(CancellationToken.None);

        return new DashboardHost(app, app.GetTestClient());
    }

    private sealed record DashboardHost(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(CancellationToken.None);
            await App.DisposeAsync();
        }
    }
}
