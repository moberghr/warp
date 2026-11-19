using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Services;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.Observability;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Metrics;

/// <summary>
/// Database coverage for chart tiles (§8.34, spec 2026-11-19 batch 1): CRUD with the canonical query, the columns cleared
/// per kind on an edit, the 12-chart cap (insert, move, kind change, an N=2 holder of the board lock inserting the
/// twelfth chart), the seeder's chart match by name and by (instrument, query), the board read returning a chart with its
/// query and no computed values (never part of the batched value read), and the tile routes.
/// </summary>
[GenerateDatabaseTests]
public abstract class MetricChartTileTestsBase : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly IDatabaseFixture _fixture;

    protected MetricChartTileTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Upsert_InsertChart_StoresTheCanonicalQueryAndIsListed()
    {
        var tile = Chart("By version", "c", "compare=7d&breakdown=service.version");
        MetricBoardValidation.TryValidate(tile, MetricKind.Counter, out var error).ShouldBeTrue(error);

        var id = await Command().Upsert(tile, Ct);

        id.ShouldNotBeNull().ShouldBeGreaterThan(0);
        var stored = (await Query().GetTiles(Ct)).ShouldHaveSingleItem();
        stored.Kind.ShouldBe(MetricTileKind.Chart);
        stored.ChartQuery.ShouldBe("breakdown=service.version&compare=7d");
        stored.StatusRule.ShouldBe(MetricTileStatusRule.None);
    }

    [TimedFact]
    public async Task Upsert_EditValueTileIntoAChart_ClearsEveryValueColumn()
    {
        var id = await Command().Upsert(Validated(Value("Errors", "c", "status=5*", threshold: 10)), Ct);
        var edit = Validated(Chart("Errors", "c", "breakdown=status"));
        edit.Id = id!.Value;

        (await Command().Upsert(edit, Ct)).ShouldBe(id.Value);

        var stored = await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(Ct);
        stored.Kind.ShouldBe(MetricTileKind.Chart);
        stored.ChartQuery.ShouldBe("breakdown=status");
        stored.TagFilter.ShouldBeNull();
        stored.StatusRule.ShouldBe(MetricTileStatusRule.None);
        stored.Threshold.ShouldBeNull();
        stored.Comparison.ShouldBeNull();
    }

    [TimedFact]
    public async Task Upsert_EditChartIntoAValueTile_ClearsTheQuery()
    {
        var id = await Command().Upsert(Validated(Chart("Errors", "c", "breakdown=status")), Ct);
        var edit = Validated(Value("Errors", "c", "status=5*"));
        edit.Id = id!.Value;

        (await Command().Upsert(edit, Ct)).ShouldBe(id.Value);

        var stored = await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(Ct);
        stored.Kind.ShouldBe(MetricTileKind.Value);
        stored.ChartQuery.ShouldBeNull();
        stored.TagFilter.ShouldBe("status=5*");
    }

    [TimedFact]
    public async Task Upsert_ChartPastTheChartCap_ReturnsNullAndAddsNothing()
    {
        await SeedTilesAsync(Charts(MetricBoardValidation.MaxChartTiles));

        var id = await Command().Upsert(Validated(Chart("One too many", "other", null)), Ct);

        id.ShouldBeNull();
        (await ChartCountAsync()).ShouldBe(MetricBoardValidation.MaxChartTiles);
    }

    [TimedFact]
    public async Task Upsert_ValueTileOnABoardFullOfCharts_IsAccepted()
    {
        await SeedTilesAsync(Charts(MetricBoardValidation.MaxChartTiles));

        var id = await Command().Upsert(Validated(Value("Volume", "c", null)), Ct);

        id.ShouldNotBeNull().ShouldBeGreaterThan(0);
    }

    [TimedFact]
    public async Task Upsert_ValueTileTurnedIntoAChartOnABoardFullOfCharts_ReturnsNullAndKeepsTheTile()
    {
        await SeedTilesAsync(Charts(MetricBoardValidation.MaxChartTiles));
        var id = await Command().Upsert(Validated(Value("Volume", "c", null)), Ct);
        var edit = Validated(Chart("Volume", "c", "breakdown=status"));
        edit.Id = id!.Value;

        var result = await Command().Upsert(edit, Ct);

        result.ShouldBeNull();
        (await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(x => x.Id == id.Value, Ct)).Kind.ShouldBe(MetricTileKind.Value);
    }

    [TimedFact]
    public async Task Upsert_EditOfAChartOnABoardFullOfCharts_IsNotCapped()
    {
        await SeedTilesAsync(Charts(MetricBoardValidation.MaxChartTiles));
        var stored = await _fixture.CreateContext().Set<MetricBoardTile>().Where(x => x.Name == "chart 0").SingleAsync(Ct);
        var edit = Validated(Chart("chart 0 renamed", "c0", "compare=1d"));
        edit.Id = stored.Id;

        var result = await Command().Upsert(edit, Ct);

        result.ShouldBe(stored.Id);
    }

    [TimedFact]
    public async Task Upsert_ChartMovedOntoABoardFullOfCharts_ReturnsNull()
    {
        var target = await SeedBoardAsync("Full");
        await SeedTilesAsync([.. Charts(MetricBoardValidation.MaxChartTiles).Select(x => OnBoard(x, target))]);
        var id = await Command().Upsert(Validated(Chart("Mover", "m", null)), Ct);
        var move = Validated(Chart("Mover", "m", null));
        move.Id = id!.Value;
        move.BoardId = target;

        var result = await Command().Upsert(move, Ct);

        result.ShouldBeNull();
        (await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(x => x.Id == id.Value, Ct)).BoardId.ShouldBeNull();
    }

    [TimedFact]
    public async Task Upsert_ChartWhileAnotherInsertsTheTwelfthChartUnderTheBoardLock_WaitsAndReturnsNull()
    {
        // N=2: a second connection holds warp:metric-board and inserts the twelfth chart inside its transaction, pinned
        // there by a BarrierSignal. The command must wait at the lock and count after the holder commits, or two inserts
        // would both pass the chart cap.
        await SeedTilesAsync(Charts(MetricBoardValidation.MaxChartTiles - 1));
        var barrier = new BarrierSignal();
        var holderContext = _fixture.CreateContext();
        var holder = TestTasks.QueriesFor(holderContext).RunUnderTransactionLockAsync(
            holderContext,
            "warp:metric-board",
            async (context, ct) =>
            {
                var twelfth = Validated(Chart("Twelfth", "t", null));
                twelfth.Position = 100;
                twelfth.CreatedAt = Now;
                twelfth.UpdatedAt = Now;
                context.Set<MetricBoardTile>().Add(twelfth);
                await context.SaveChangesAsync(ct);
                barrier.Running.Release();
                await barrier.CanFinish.WaitAsync(ct);

                return 0;
            },
            Ct);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();

        var insert = Command().Upsert(Validated(Chart("Thirteenth", "x", null)), Ct);

        await Should.ThrowAsync<TimeoutException>(() => insert.WaitAsync(TimeSpan.FromMilliseconds(500), Ct));
        barrier.CanFinish.Release();
        (await holder).LockHeld.ShouldBeTrue();
        (await insert).ShouldBeNull();
        (await ChartCountAsync()).ShouldBe(MetricBoardValidation.MaxChartTiles);
    }

    [TimedFact]
    public async Task Seeder_Chart_InsertsTheBuiltQuery_AndReseedIsIdempotent()
    {
        static void Configure(MetricBoardOptions b) => b
            .Chart("Checkout errors by version", "shop.checkout.completed", application: "shop-api", breakdown: "service.version", filter: "outcome=error", compare: "7d")
            .Chart("Checkout latency p95", "http.server.request.duration", application: "shop-api", percentile: 95);

        await SeedFromConfigAsync(Configure);
        await SeedFromConfigAsync(Configure);

        var tiles = await Query().GetTiles(Ct);
        tiles.Select(x => x.Name).ShouldBe(["Checkout errors by version", "Checkout latency p95"]);
        tiles[0].ChartQuery.ShouldBe("application=shop-api&breakdown=service.version&filter=outcome%3Aerror&compare=7d");
        tiles[1].ChartQuery.ShouldBe("application=shop-api&percentile=95");
    }

    [TimedFact]
    public async Task Seeder_ChartRenamedInTheDashboard_MatchesByInstrumentAndQuery()
    {
        static void Configure(MetricBoardOptions b) => b.Chart("By version", "c", breakdown: "service.version");

        await SeedFromConfigAsync(Configure);
        var context = _fixture.CreateContext();
        var row = await context.Set<MetricBoardTile>().SingleAsync(Ct);
        row.Name = "Renamed";
        await context.SaveChangesAsync(Ct);

        await SeedFromConfigAsync(Configure);

        (await Query().GetTiles(Ct)).ShouldHaveSingleItem().Name.ShouldBe("Renamed");
    }

    [TimedFact]
    public async Task Seeder_ChartWhoseQueryWasEditedInTheDashboard_MatchesByNameAndLogsTheField()
    {
        await SeedFromConfigAsync(b => b.Chart("By version", "c", breakdown: "service.version"));
        var context = _fixture.CreateContext();
        var row = await context.Set<MetricBoardTile>().SingleAsync(Ct);
        row.ChartQuery = "breakdown=region";
        await context.SaveChangesAsync(Ct);
        var logs = new CapturingLoggerProvider();

        await SeedFromConfigAsync(b => b.Chart("By version", "c", breakdown: "service.version"), logs);

        (await Query().GetTiles(Ct)).ShouldHaveSingleItem().ChartQuery.ShouldBe("breakdown=region");
        var log = logs.Logs.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Information);
        log.Fields["Fields"].ShouldBe("ChartQuery");
    }

    [TimedFact]
    public async Task Seeder_BoardFullOfCharts_SkipsTheChartButSeedsAValueTile()
    {
        await SeedTilesAsync(Charts(MetricBoardValidation.MaxChartTiles));

        await SeedFromConfigAsync(b => b.Chart("One too many", "other").Value("Volume", "c"));

        (await ChartCountAsync()).ShouldBe(MetricBoardValidation.MaxChartTiles);
        var names = await _fixture.CreateContext().Set<MetricBoardTile>().Select(x => x.Name).ToListAsync(Ct);
        names.ShouldContain("Volume");
        names.ShouldNotContain("One too many");
    }

    [TimedFact]
    public async Task GetBoard_ChartTile_HasItsQueryKindAndUnitAndNoComputedValues()
    {
        await SeedAsync(
            [Instrument("c", MetricKind.Histogram, "ms")],
            [Point("c", Now.AddMinutes(-30), 5), Point("c", Now.AddDays(-1).AddMinutes(-30), 7)]);
        await SeedTilesAsync(Validated(Chart("p95", "c", "percentile=95")));

        var board = await Query().GetBoard(Now.AddHours(-1), Now, Ct);

        var tile = board.Tiles.ShouldHaveSingleItem();
        tile.Tile.Kind.ShouldBe(MetricTileKind.Chart);
        tile.Tile.ChartQuery.ShouldBe("percentile=95");
        tile.InstrumentKind.ShouldBe(MetricKind.Histogram);
        tile.Unit.ShouldBe("ms");
        tile.Current.ShouldBeNull();
        tile.DayAgo.ShouldBeNull();
        tile.WeekAgo.ShouldBeNull();
        tile.Status.ShouldBeNull();
        tile.Sparkline.ShouldBeEmpty();
        tile.Annotations.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task GetBoard_ChartTilesBesideAValueTile_AddNoStatementAndLeaveTheValueTileComputed()
    {
        await SeedAsync(
            [Instrument("v", MetricKind.Counter), Instrument("a", MetricKind.Counter), Instrument("b", MetricKind.Histogram)],
            [Point("v", Now.AddMinutes(-30), 4), Point("a", Now.AddMinutes(-30), 9, "status=500"), Point("b", Now.AddMinutes(-30), 2)]);
        await SeedTilesAsync(Validated(Value("Volume", "v", null)));
        var (alone, _) = await ReadCountingStatementsAsync();
        await SeedTilesAsync(
            Validated(Chart("A", "a", "filter=status:5*")),
            Validated(Chart("B", "b", "percentile=95")),
            Validated(Chart("V", "v", "compare=7d")));

        var (withCharts, board) = await ReadCountingStatementsAsync();

        withCharts.ShouldBe(alone);
        board.Tiles.Single(x => string.Equals(x.Tile.Name, "Volume", StringComparison.Ordinal)).Current.ShouldBe(4);
        board.Tiles.Where(x => x.Tile.Kind == MetricTileKind.Chart).ShouldAllBe(x => x.Current == null && x.Sparkline.Count == 0);
    }

    [TimedFact]
    public async Task PostTile_Chart_PersistsTheCanonicalQuery()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], []);
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.PostAsJsonAsync(
            "/warp/api/metrics/board/tiles",
            new { id = 0, name = "By status", instrument = "c", kind = (int)MetricTileKind.Chart, chartQuery = "filter=status:5*&breakdown=status", tagFilter = "ignored=1" },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tile = (await Query().GetTiles(Ct)).ShouldHaveSingleItem();
        tile.ChartQuery.ShouldBe("breakdown=status&filter=status%3A5%2A");
        tile.TagFilter.ShouldBeNull();
    }

    [TimedTheory]
    [InlineData("range=7d")]
    [InlineData("from=2026-11-01T00:00:00.000Z")]
    [InlineData("unknown=1")]
    [InlineData("filter=status")]
    public async Task PostTile_ChartWithABadQuery_ReturnsBadRequestAndPersistsNothing(string query)
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], []);
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.PostAsJsonAsync(
            "/warp/api/metrics/board/tiles",
            new { id = 0, name = "Chart", instrument = "c", kind = (int)MetricTileKind.Chart, chartQuery = query },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task PostTile_ChartPastTheChartCap_ReturnsBadRequestNamingTheChartCap()
    {
        await SeedTilesAsync(Charts(MetricBoardValidation.MaxChartTiles));
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.PostAsJsonAsync(
            "/warp/api/metrics/board/tiles",
            new { id = 0, name = "Extra", instrument = "c", kind = (int)MetricTileKind.Chart },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain($"at most {MetricBoardValidation.MaxChartTiles} charts");
    }

    private static MetricBoardTile Chart(string name, string instrument, string? query) => new()
    {
        Name = name,
        Instrument = instrument,
        Kind = MetricTileKind.Chart,
        ChartQuery = query,
        StatusRule = MetricTileStatusRule.None,
    };

    private static MetricBoardTile Value(string name, string instrument, string? filter, double? threshold = null) => new()
    {
        Name = name,
        Instrument = instrument,
        TagFilter = filter,
        Kind = MetricTileKind.Value,
        StatusRule = threshold is null ? MetricTileStatusRule.None : MetricTileStatusRule.Threshold,
        Threshold = threshold,
    };

    private static MetricBoardTile Validated(MetricBoardTile tile)
    {
        MetricBoardValidation.TryValidate(tile, null, out var error).ShouldBeTrue(error);

        return tile;
    }

    private static MetricBoardTile OnBoard(MetricBoardTile tile, int boardId)
    {
        tile.BoardId = boardId;

        return tile;
    }

    private static MetricBoardTile[] Charts(int count)
        => [.. Enumerable.Range(0, count).Select(ChartAt)];

    private static MetricBoardTile ChartAt(int index)
    {
        var tile = Validated(Chart($"chart {index}", $"c{index}", null));
        tile.Position = index + 1;

        return tile;
    }

    private static MetricInstrument Instrument(string name, MetricKind kind, string? unit = null) => new()
    {
        Name = name,
        Meter = "test",
        Kind = kind,
        Unit = unit,
        FirstSeenAt = Now.AddDays(-10),
        LastSeenAt = Now,
    };

    private static MetricPoint Point(string instrument, DateTime bucket, double sum, string tags = "-") => new()
    {
        Instrument = instrument,
        Tags = tags,
        Tier = MetricTier.Fine,
        BucketStart = bucket,
        Count = 1,
        Sum = sum,
    };

    private MetricBoardQueryService<TestContext> Query()
        => new(_fixture.CreateContext(), Options.Create(new WarpConfiguration()), new FakeTimeProvider(new DateTimeOffset(Now)));

    private MetricBoardCommandService<TestContext> Command()
    {
        var context = _fixture.CreateContext();

        return new(context, TimeProvider.System, NullLogger<MetricBoardCommandService<TestContext>>.Instance, TestTasks.QueriesFor(context));
    }

    private async Task<int> ChartCountAsync()
        => await _fixture.CreateContext().Set<MetricBoardTile>().CountAsync(x => x.Kind == MetricTileKind.Chart, Ct);

    private async Task SeedAsync(MetricInstrument[] instruments, MetricPoint[] points)
    {
        var context = _fixture.CreateContext();
        context.Set<MetricInstrument>().AddRange(instruments);
        context.Set<MetricPoint>().AddRange(points);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<int> SeedBoardAsync(string name)
    {
        var context = _fixture.CreateContext();
        var board = new MetricBoard { Name = name, Position = 1, CreatedAt = Now, UpdatedAt = Now };
        context.Set<MetricBoard>().Add(board);
        await context.SaveChangesAsync(Ct);

        return board.Id;
    }

    private async Task SeedTilesAsync(params MetricBoardTile[] tiles)
    {
        var context = _fixture.CreateContext();
        foreach (var tile in tiles)
        {
            tile.CreatedAt = Now;
            tile.UpdatedAt = Now;
        }

        context.Set<MetricBoardTile>().AddRange(tiles);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<(int Statements, Warp.Core.Models.MetricBoardModel Board)> ReadCountingStatementsAsync()
    {
        var counter = new StatementCounter();
        var options = (DbContextOptions<TestContext>)_fixture.CreateContext().GetService<IDbContextOptions>();
        await using var context = new TestContext(new DbContextOptionsBuilder<TestContext>(options).AddInterceptors(counter).Options);
        var query = new MetricBoardQueryService<TestContext>(context, Options.Create(new WarpConfiguration()), new FakeTimeProvider(new DateTimeOffset(Now)));

        var board = await query.GetBoard(Now.AddHours(-1), Now, Ct);

        return (counter.Count, board);
    }

    private async Task SeedFromConfigAsync(Action<MetricBoardOptions> configure, CapturingLoggerProvider? logs = null)
    {
        var seeds = new MetricBoardSeeds();
        configure(seeds.For(null).Tiles);
        var services = new ServiceCollection();
        services.AddScoped(_ => _fixture.CreateContext());
        services.AddScoped<IMetricQueryService>(_ => new MetricQueryService<TestContext>(_fixture.CreateContext(), Options.Create(new WarpConfiguration())));
        services.AddSingleton(TestTasks.QueriesFor(_fixture.CreateContext()));
        await using var provider = services.BuildServiceProvider();
        var logger = logs is null
            ? NullLogger<MetricBoardSeeder<TestContext>>.Instance
            : new CapturingLoggerFactory(logs).CreateLogger<MetricBoardSeeder<TestContext>>();
        var seeder = new MetricBoardSeeder<TestContext>(provider.GetRequiredService<IServiceScopeFactory>(), seeds, TimeProvider.System, logger);

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
        builder.Services.AddScoped<ISloQueryService>(_ => new SloQueryService<TestContext>(_fixture.CreateContext()));

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(CancellationToken.None);

        return new DashboardHost(app, app.GetTestClient());
    }

    private sealed class StatementCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override DbCommand CommandCreated(CommandEndEventData eventData, DbCommand result)
        {
            Interlocked.Increment(ref _count);

            return result;
        }
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
