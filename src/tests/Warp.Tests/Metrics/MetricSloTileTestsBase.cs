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
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.Observability;

namespace Warp.Tests.Metrics;

/// <summary>
/// Database coverage for SLO tiles (§8.34, spec 2026-11-19 batch 2): CRUD with the columns cleared per kind, the route's
/// "objective not found" 400, the board read joining every SLO tile to ONE objective read (constant statement count, a
/// deleted objective flagged missing), the SLO delete removing its tiles, and the seeder resolving an objective by name
/// and application (matched by name or by objective id; an unknown or ambiguous objective skipped and logged at Error).
/// </summary>
[GenerateDatabaseTests]
public abstract class MetricSloTileTestsBase : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly IDatabaseFixture _fixture;

    protected MetricSloTileTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Upsert_InsertSloTile_StoresTheObjectiveAndNoInstrument()
    {
        var objective = await SeedObjectiveAsync("Checkout success", "shop-api");
        var tile = Slo("Checkout success", objective);
        tile.Instrument = "ignored";
        tile.Application = "ignored";
        Validated(tile);

        var id = await Command().Upsert(tile, Ct);

        id.ShouldNotBeNull().ShouldBeGreaterThan(0);
        var stored = (await Query().GetTiles(Ct)).ShouldHaveSingleItem();
        stored.Kind.ShouldBe(MetricTileKind.Slo);
        stored.SloDefinitionId.ShouldBe(objective);
        stored.Instrument.ShouldBe(string.Empty);
        stored.Application.ShouldBeNull();
        stored.StatusRule.ShouldBe(MetricTileStatusRule.None);
    }

    [TimedFact]
    public async Task Upsert_EditValueTileIntoAnSloTile_ClearsEveryValueColumn()
    {
        var objective = await SeedObjectiveAsync("Checkout success", null);
        var id = await Command().Upsert(Validated(Value("Errors", "c", "status=5*", threshold: 10)), Ct);
        var edit = Validated(Slo("Errors", objective));
        edit.Id = id!.Value;

        (await Command().Upsert(edit, Ct)).ShouldBe(id.Value);

        var stored = await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(Ct);
        stored.Kind.ShouldBe(MetricTileKind.Slo);
        stored.SloDefinitionId.ShouldBe(objective);
        stored.Instrument.ShouldBe(string.Empty);
        stored.TagFilter.ShouldBeNull();
        stored.StatusRule.ShouldBe(MetricTileStatusRule.None);
        stored.Threshold.ShouldBeNull();
        stored.Comparison.ShouldBeNull();
    }

    [TimedFact]
    public async Task Upsert_EditSloTileIntoAValueTile_ClearsTheObjective()
    {
        var objective = await SeedObjectiveAsync("Checkout success", null);
        var id = await Command().Upsert(Validated(Slo("Errors", objective)), Ct);
        var edit = Validated(Value("Errors", "c", "status=5*"));
        edit.Id = id!.Value;

        (await Command().Upsert(edit, Ct)).ShouldBe(id.Value);

        var stored = await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(Ct);
        stored.Kind.ShouldBe(MetricTileKind.Value);
        stored.SloDefinitionId.ShouldBeNull();
        stored.Instrument.ShouldBe("c");
    }

    [TimedFact]
    public async Task Delete_Objective_DeletesItsTilesAndKeepsTheOthers()
    {
        var deleted = await SeedObjectiveAsync("Checkout success", null);
        var kept = await SeedObjectiveAsync("Search success", null);
        var board = await SeedBoardAsync("Checkout");
        await SeedTilesAsync(
            Validated(Slo("On Default", deleted)),
            OnBoard(Validated(Slo("On Checkout", deleted)), board),
            Validated(Slo("Other objective", kept)),
            Validated(Value("Volume", "c", null)));

        var result = await new SloCommandService<TestContext>(_fixture.CreateContext(), TimeProvider.System, NullLogger<SloCommandService<TestContext>>.Instance)
            .Delete(deleted, Ct);

        result.ShouldBeTrue();
        var names = await _fixture.CreateContext().Set<MetricBoardTile>().Select(x => x.Name).ToListAsync(Ct);
        names.ShouldBe(["Other objective", "Volume"], ignoreOrder: true);
    }

    [TimedFact]
    public async Task GetBoard_SloTile_CarriesTheObjectivesCurrentEvaluation()
    {
        var objective = await SeedObjectiveAsync("Checkout success", "shop-api", SloState.Acknowledged, acknowledgedUntil: Now.AddHours(2));
        await SeedTilesAsync(Validated(Slo("Checkout", objective)));

        var board = await Query().GetBoard(Now.AddHours(-1), Now, Ct);

        var tile = board.Tiles.ShouldHaveSingleItem();
        tile.Missing.ShouldBeFalse();
        var slo = tile.Slo.ShouldNotBeNull();
        slo.Id.ShouldBe(objective);
        slo.Name.ShouldBe("Checkout success");
        slo.Kind.ShouldBe(SloKind.SuccessRate);
        slo.Application.ShouldBe("shop-api");
        slo.TargetValue.ShouldBe(0.99);
        slo.State.ShouldBe(SloState.Acknowledged);
        slo.Attainment.ShouldBe(0.995);
        slo.BudgetRemaining.ShouldBe(0.5);
        slo.BurnRateShort.ShouldBe(2);
        slo.BurnRateLong.ShouldBe(0.5);
        slo.AcknowledgedUntil.ShouldBe(Now.AddHours(2));
        slo.LastEvaluatedAt.ShouldBe(Now.AddMinutes(-1));
        tile.Status.ShouldBeNull();
        tile.Current.ShouldBeNull();
        tile.Sparkline.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task GetBoard_SloTileOfADeletedObjective_IsFlaggedMissing()
    {
        // A delete made by an older binary mid rolling deploy leaves the tile behind; the board shows it as missing.
        await SeedTilesAsync(Validated(Slo("Gone", 4242)));

        var board = await Query().GetBoard(Now.AddHours(-1), Now, Ct);

        var tile = board.Tiles.ShouldHaveSingleItem();
        tile.Missing.ShouldBeTrue();
        tile.Slo.ShouldBeNull();
        tile.Tile.SloDefinitionId.ShouldBe(4242);
    }

    [TimedFact]
    public async Task GetBoard_StatementCount_DoesNotGrowWithTheSloTiles()
    {
        await SeedAsync([Instrument("v", MetricKind.Counter)], [Point("v", Now.AddMinutes(-30), 4)]);
        var first = await SeedObjectiveAsync("A", null);
        var second = await SeedObjectiveAsync("B", null);
        var third = await SeedObjectiveAsync("C", null);
        await SeedTilesAsync(Validated(Value("Volume", "v", null)), Validated(Slo("A", first)));
        var (one, _) = await ReadCountingStatementsAsync();
        await SeedTilesAsync(Validated(Slo("B", second)), Validated(Slo("C", third)), Validated(Slo("Gone", 4242)));

        var (four, board) = await ReadCountingStatementsAsync();

        four.ShouldBe(one);
        board.Tiles.Where(x => x.Tile.Kind == MetricTileKind.Slo).Select(x => x.Slo?.Name).ShouldBe(["A", "B", "C", null]);
        board.Tiles.Single(x => string.Equals(x.Tile.Name, "Volume", StringComparison.Ordinal)).Current.ShouldBe(4);
    }

    [TimedFact]
    public async Task GetBoard_NoSloTile_ReadsNoObjective()
    {
        await SeedAsync([Instrument("v", MetricKind.Counter)], [Point("v", Now.AddMinutes(-30), 4)]);
        await SeedTilesAsync(Validated(Value("Volume", "v", null)));
        var (withoutObjectives, _) = await ReadCountingStatementsAsync();
        await SeedTilesAsync(Validated(Slo("A", await SeedObjectiveAsync("A", null))));

        var (withObjectives, _) = await ReadCountingStatementsAsync();

        // The objective read (definitions + evaluations) is made only for a board that shows an SLO.
        withObjectives.ShouldBe(withoutObjectives + 2);
    }

    [TimedFact]
    public async Task PostTile_Slo_PersistsTheObjective()
    {
        var objective = await SeedObjectiveAsync("Checkout success", null);
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.PostAsJsonAsync(
            "/warp/api/metrics/board/tiles",
            new { id = 0, name = "Checkout", instrument = string.Empty, kind = (int)MetricTileKind.Slo, sloDefinitionId = objective },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Query().GetTiles(Ct)).ShouldHaveSingleItem().SloDefinitionId.ShouldBe(objective);
    }

    [TimedFact]
    public async Task PostTile_SloOfAMissingObjective_ReturnsBadRequestAndPersistsNothing()
    {
        await SeedObjectiveAsync("Checkout success", null);
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.PostAsJsonAsync(
            "/warp/api/metrics/board/tiles",
            new { id = 0, name = "Checkout", instrument = string.Empty, kind = (int)MetricTileKind.Slo, sloDefinitionId = 4242 },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("objective was not found");
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Seeder_Slo_ResolvesTheObjectiveByNameIgnoringCaseAndApplication_AndReseedIsIdempotent()
    {
        await SeedObjectiveAsync("Checkout success", "admin");
        var shop = await SeedObjectiveAsync("Checkout success", "shop-api");
        await SeedObjectiveAsync("Checkout success", null);

        await SeedFromConfigAsync(b => b.Slo("checkout SUCCESS", application: "shop-api"));
        await SeedFromConfigAsync(b => b.Slo("checkout SUCCESS", application: "shop-api"));

        var tile = (await Query().GetTiles(Ct)).ShouldHaveSingleItem();
        tile.Kind.ShouldBe(MetricTileKind.Slo);
        tile.Name.ShouldBe("checkout SUCCESS");
        tile.SloDefinitionId.ShouldBe(shop);
    }

    [TimedFact]
    public async Task Seeder_SloWithoutApplication_MatchesOnlyTheObjectiveWithNone()
    {
        await SeedObjectiveAsync("Checkout success", "shop-api");
        var global = await SeedObjectiveAsync("Checkout success", null);

        await SeedFromConfigAsync(b => b.Slo("Checkout success"));

        (await Query().GetTiles(Ct)).ShouldHaveSingleItem().SloDefinitionId.ShouldBe(global);
    }

    [TimedFact]
    public async Task Seeder_SloTileRenamedInTheDashboard_MatchesByObjective()
    {
        await SeedObjectiveAsync("Checkout success", null);
        await SeedFromConfigAsync(b => b.Slo("Checkout success"));
        var context = _fixture.CreateContext();
        var row = await context.Set<MetricBoardTile>().SingleAsync(Ct);
        row.Name = "Renamed";
        await context.SaveChangesAsync(Ct);

        await SeedFromConfigAsync(b => b.Slo("Checkout success"));

        (await Query().GetTiles(Ct)).ShouldHaveSingleItem().Name.ShouldBe("Renamed");
    }

    [TimedFact]
    public async Task Seeder_SloTilePointedAtAnotherObjectiveInTheDashboard_MatchesByNameAndLogsTheField()
    {
        await SeedObjectiveAsync("Checkout success", null);
        var other = await SeedObjectiveAsync("Search success", null);
        await SeedFromConfigAsync(b => b.Slo("Checkout success"));
        var context = _fixture.CreateContext();
        var row = await context.Set<MetricBoardTile>().SingleAsync(Ct);
        row.SloDefinitionId = other;
        await context.SaveChangesAsync(Ct);
        var logs = new CapturingLoggerProvider();

        await SeedFromConfigAsync(b => b.Slo("Checkout success"), logs);

        (await Query().GetTiles(Ct)).ShouldHaveSingleItem().SloDefinitionId.ShouldBe(other);
        var log = logs.Logs.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Information);
        log.Fields["Fields"].ShouldBe("SloDefinitionId");
    }

    [TimedFact]
    public async Task Seeder_UnknownObjective_SkipsTheTileAndLogsAnError()
    {
        await SeedObjectiveAsync("Checkout success", "shop-api");
        var logs = new CapturingLoggerProvider();

        await SeedFromConfigAsync(b => b.Slo("Checkout success").Value("Volume", "c"), logs);

        (await Query().GetTiles(Ct)).ShouldHaveSingleItem().Name.ShouldBe("Volume");
        var log = logs.Logs.Single(x => x.Level == LogLevel.Error);
        log.Fields["Name"].ShouldBe("Checkout success");
        log.Fields["Count"].ShouldBe(0);
    }

    [TimedFact]
    public async Task Seeder_AmbiguousObjective_SkipsTheTileAndLogsAnError()
    {
        // Objective names are unique only among config seeds; the dashboard may create a second one of a name.
        await SeedObjectiveAsync("Checkout success", null);
        await SeedObjectiveAsync("CHECKOUT SUCCESS", null);
        var logs = new CapturingLoggerProvider();

        await SeedFromConfigAsync(b => b.Slo("Checkout success"), logs);

        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();
        var log = logs.Logs.Single(x => x.Level == LogLevel.Error);
        log.Fields["Count"].ShouldBe(2);
    }

    private static MetricBoardTile Slo(string name, int objective) => new()
    {
        Name = name,
        Kind = MetricTileKind.Slo,
        SloDefinitionId = objective,
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

    private static MetricInstrument Instrument(string name, MetricKind kind) => new()
    {
        Name = name,
        Meter = "test",
        Kind = kind,
        FirstSeenAt = Now.AddDays(-10),
        LastSeenAt = Now,
    };

    private static MetricPoint Point(string instrument, DateTime bucket, double sum) => new()
    {
        Instrument = instrument,
        Tags = "-",
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

    private async Task<int> SeedObjectiveAsync(string name, string? application, SloState state = SloState.Healthy, DateTime? acknowledgedUntil = null)
    {
        var context = _fixture.CreateContext();
        var definition = new SloDefinition
        {
            Name = name,
            Kind = SloKind.SuccessRate,
            Dimension = "*",
            Application = application,
            TargetValue = 0.99,
            WindowSeconds = 3600,
        };
        context.Set<SloDefinition>().Add(definition);
        await context.SaveChangesAsync(Ct);
        context.Set<SloEvaluation>().Add(new SloEvaluation
        {
            SloDefinitionId = definition.Id,
            Attainment = 0.995,
            BudgetRemaining = 0.5,
            BurnRateShort = 2,
            BurnRateLong = 0.5,
            State = state,
            AcknowledgedUntil = acknowledgedUntil,
            LastEvaluatedAt = Now.AddMinutes(-1),
        });
        await context.SaveChangesAsync(Ct);

        return definition.Id;
    }

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
        var position = await context.Set<MetricBoardTile>().MaxAsync(x => (int?)x.Position, Ct) ?? 0;
        foreach (var tile in tiles)
        {
            tile.Position = ++position;
            tile.CreatedAt = Now;
            tile.UpdatedAt = Now;
        }

        context.Set<MetricBoardTile>().AddRange(tiles);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<(int Statements, MetricBoardModel Board)> ReadCountingStatementsAsync()
    {
        var counter = new StatementCounter();
        var options = (DbContextOptions<TestContext>)_fixture.CreateContext().GetService<IDbContextOptions>();
        await using var context = new TestContext(new DbContextOptionsBuilder<TestContext>(options).AddInterceptors(counter).Options);
        var query = new MetricBoardQueryService<TestContext>(context, Options.Create(new WarpConfiguration()), new FakeTimeProvider(new DateTimeOffset(Now)), new SloQueryService<TestContext>(context));

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
        builder.Services.AddScoped<ISloQueryService>(_ => new SloQueryService<TestContext>(_fixture.CreateContext()));
        builder.Services.AddScoped<IMetricBoardQueryService>(_ => Query());
        builder.Services.AddScoped<IMetricBoardCommandService>(_ => Command());

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
