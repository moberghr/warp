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
using Warp.Tests.TestData.Handlers;

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
    public async Task Upsert_SloTileWhoseObjectiveWasDeletedAfterValidation_ReturnsObjectiveMissingAndStoresNothing()
    {
        // The route's validator saw the objective; it is deleted before the insert takes the board lock.
        var objective = await SeedObjectiveAsync("Checkout success", null);
        var tile = Validated(Slo("Checkout", objective));
        await _fixture.CreateContext().Set<SloDefinition>().Where(x => x.Id == objective).ExecuteDeleteAsync(Ct);

        var id = await Command().Upsert(tile, Ct);

        id.ShouldBe(IMetricBoardCommandService.ObjectiveMissing);
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Delete_ObjectiveWhileATileInsertHoldsTheBoardLock_WaitsForItThenDeletesThatTileToo()
    {
        // N=2: a second connection is a tile insert that passed its objective check and added an SLO tile under the board
        // lock, pinned inside its transaction by a BarrierSignal. The delete must wait for it (it writes board tiles) and
        // then remove the tile the insert committed: no tile may outlive its objective.
        var objective = await SeedObjectiveAsync("Checkout success", null);
        var barrier = new BarrierSignal();
        var holderContext = _fixture.CreateContext();
        var holder = TestTasks.QueriesFor(holderContext).RunUnderTransactionLockAsync(
            holderContext,
            "warp:metric-board",
            async (context, ct) =>
            {
                var tile = Validated(Slo("Checkout", objective));
                tile.CreatedAt = Now;
                tile.UpdatedAt = Now;
                context.Set<MetricBoardTile>().Add(tile);
                await context.SaveChangesAsync(ct);
                barrier.Running.Release();
                await barrier.CanFinish.WaitAsync(ct);

                return tile.Id;
            },
            Ct);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();

        var delete = SloCommand().Delete(objective, Ct);

        // Held at the lock: it does not finish while the insert is inside.
        await Should.ThrowAsync<TimeoutException>(() => delete.WaitAsync(TimeSpan.FromMilliseconds(500), Ct));
        barrier.CanFinish.Release();
        (await holder).LockHeld.ShouldBeTrue();
        (await delete).ShouldBeTrue();
        (await _fixture.CreateContext().Set<SloDefinition>().AnyAsync(Ct)).ShouldBeFalse();
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Delete_ObjectiveWhileTheBoardLockIsHeldPastTheTimeout_ThrowsTimeoutAndDeletesNothing()
    {
        var objective = await SeedObjectiveAsync("Checkout success", null);
        await SeedTilesAsync(Validated(Slo("Checkout", objective)));
        var barrier = new BarrierSignal();
        var holderContext = _fixture.CreateContext();
        var holder = TestTasks.QueriesFor(holderContext).RunUnderTransactionLockAsync(
            holderContext,
            "warp:metric-board",
            async (_, ct) =>
            {
                barrier.Running.Release();
                await barrier.CanFinish.WaitAsync(ct);

                return 0;
            },
            Ct);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
        try
        {
            await Should.ThrowAsync<TimeoutException>(() => SloCommand(TimeSpan.FromMilliseconds(300)).Delete(objective, Ct));
        }
        finally
        {
            barrier.CanFinish.Release();
            await holder;
        }

        (await _fixture.CreateContext().Set<SloDefinition>().AnyAsync(Ct)).ShouldBeTrue();
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeTrue();
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
    public async Task Seeder_UnknownObjective_SkipsTheTileAndWarnsThatItWillRetry()
    {
        // Another process may still seed the objective: a Warning, not an Error, on the first miss.
        await SeedObjectiveAsync("Checkout success", "shop-api");
        var logs = new CapturingLoggerProvider();
        await using var run = await StartSeederAsync(b => b.Slo("Checkout success").Value("Volume", "c"), logs);

        (await Query().GetTiles(Ct)).ShouldHaveSingleItem().Name.ShouldBe("Volume");
        var log = logs.Logs.Single(x => x.Level == LogLevel.Warning);
        log.Fields["Name"].ShouldBe("Checkout success");
        log.Message.ShouldContain("will retry");
        logs.Logs.ShouldNotContain(x => x.Level == LogLevel.Error);
        run.Seeder.Retry.ShouldNotBeNull().IsCompleted.ShouldBeFalse();
    }

    [TimedFact]
    public async Task Seeder_ObjectiveSeededAfterTheFirstPass_TileAppearsOnTheNextRetry()
    {
        var logs = new CapturingLoggerProvider();
        await using var run = await StartSeederAsync(b => b.Slo("Checkout success").Value("Volume", "c"), logs);
        (await Query().GetTiles(Ct)).ShouldHaveSingleItem().Name.ShouldBe("Volume");

        // The SLO seeder of another process commits the objective; one retry interval later the tile is seeded.
        var objective = await SeedObjectiveAsync("Checkout success", null);
        run.Time.Advance(MetricBoardSeeds.RetryInterval);
        (await run.RetryPassed.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();

        await run.Seeder.Retry.ShouldNotBeNull().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var tiles = await Query().GetTiles(Ct);
        tiles.Select(x => x.Name).ShouldBe(["Volume", "Checkout success"]);
        tiles.Single(x => x.Kind == MetricTileKind.Slo).SloDefinitionId.ShouldBe(objective);
        logs.Logs.ShouldNotContain(x => x.Level == LogLevel.Error);
    }

    [TimedFact]
    public async Task Seeder_ObjectiveNeverSeeded_LogsAnErrorOnlyAfterTheLastRetry()
    {
        var logs = new CapturingLoggerProvider();
        await using var run = await StartSeederAsync(b => b.Slo("Checkout success"), logs);

        for (var attempt = 1; attempt <= MetricBoardSeeds.MaxRetries; attempt++)
        {
            logs.Logs.ShouldNotContain(x => x.Level == LogLevel.Error);
            run.Time.Advance(MetricBoardSeeds.RetryInterval);
            (await run.RetryPassed.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
        }

        await run.Seeder.Retry.ShouldNotBeNull().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        logs.Logs.Count(x => x.Level == LogLevel.Warning).ShouldBe(1);
        var error = logs.Logs.Single(x => x.Level == LogLevel.Error);
        error.Fields["Name"].ShouldBe("Checkout success");
        error.Fields["Count"].ShouldBe(0);
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();

        // Given up: a later objective is not picked up any more.
        await SeedObjectiveAsync("Checkout success", null);
        run.Time.Advance(MetricBoardSeeds.RetryInterval);
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Seeder_StopAsync_CancelsThePendingRetry()
    {
        await using var run = await StartSeederAsync(b => b.Slo("Checkout success"));
        var retry = run.Seeder.Retry.ShouldNotBeNull();

        await run.Seeder.StopAsync(Ct);

        retry.IsCompleted.ShouldBeTrue();
        await SeedObjectiveAsync("Checkout success", null);
        run.Time.Advance(MetricBoardSeeds.RetryInterval);
        run.RetryPassed.CurrentCount.ShouldBe(0);
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Seeder_AmbiguousObjective_SkipsTheTileAndLogsAnError()
    {
        // Objective names are unique only among config seeds; the dashboard may create a second one of a name.
        await SeedObjectiveAsync("Checkout success", null);
        await SeedObjectiveAsync("CHECKOUT SUCCESS", null);
        var logs = new CapturingLoggerProvider();

        await using var run = await StartSeederAsync(b => b.Slo("Checkout success"), logs);

        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();
        var log = logs.Logs.Single(x => x.Level == LogLevel.Error);
        log.Fields["Count"].ShouldBe(2);

        // Two matches never resolve on their own: no retry.
        run.Seeder.Retry.ShouldBeNull();
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

    private SloCommandService<TestContext> SloCommand(TimeSpan? lockTimeout = null)
    {
        var context = _fixture.CreateContext();

        return new(context, TimeProvider.System, NullLogger<SloCommandService<TestContext>>.Instance, TestTasks.QueriesFor(context))
        {
            LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(10),
        };
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
        await using var run = await StartSeederAsync(configure, logs);
    }

    // Starts a seeder on a fake clock (its retry timer only ticks when the test advances it); disposing the run stops the
    // seeder (cancelling any retry) and then its service provider.
    private async Task<SeederRun> StartSeederAsync(Action<MetricBoardOptions> configure, CapturingLoggerProvider? logs = null)
    {
        var seeds = new MetricBoardSeeds();
        configure(seeds.For(null).Tiles);
        var services = new ServiceCollection();
        services.AddScoped(_ => _fixture.CreateContext());
        services.AddScoped<IMetricQueryService>(_ => new MetricQueryService<TestContext>(_fixture.CreateContext(), Options.Create(new WarpConfiguration())));
        services.AddSingleton(TestTasks.QueriesFor(_fixture.CreateContext()));
        var provider = services.BuildServiceProvider();
        var logger = logs is null
            ? NullLogger<MetricBoardSeeder<TestContext>>.Instance
            : new CapturingLoggerFactory(logs).CreateLogger<MetricBoardSeeder<TestContext>>();
        var time = new FakeTimeProvider(new DateTimeOffset(Now));
        var retryPassed = new SemaphoreSlim(0);
        var seeder = new MetricBoardSeeder<TestContext>(provider.GetRequiredService<IServiceScopeFactory>(), seeds, time, logger)
        {
            RetryPassed = retryPassed,
        };

        await seeder.StartAsync(Ct);

        return new SeederRun(seeder, provider, time, retryPassed);
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

    private sealed record SeederRun(MetricBoardSeeder<TestContext> Seeder, ServiceProvider Provider, FakeTimeProvider Time, SemaphoreSlim RetryPassed) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Seeder.StopAsync(CancellationToken.None);
            Seeder.Dispose();
            RetryPassed.Dispose();
            await Provider.DisposeAsync();
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
