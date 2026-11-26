using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using Warp.Core.Services;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.Observability;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Metrics;

/// <summary>
/// Database coverage for named metric boards (§8.34): board CRUD and the Default board (created once, never deleted,
/// owner of the legacy null-board tiles), deleting a board with only its tiles, the per-board tile cap and the board cap,
/// moving tiles, the board read filtered by board (bounded statements), the seeder across named boards (insert-if-absent,
/// a dashboard edit survives, two replicas seed one copy), the routes with their 400s and 404s, and a dashboard-only host.
/// </summary>
[GenerateDatabaseTests]
public abstract class MetricNamedBoardTestsBase : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly IDatabaseFixture _fixture;

    protected MetricNamedBoardTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task UpsertBoard_Create_CreatesTheDefaultBoardFirstAndAppendsTheNewBoard()
    {
        var result = await Command().UpsertBoard(new MetricBoard { Name = "Checkout", Description = "Checkout health" }, "alice", Ct);

        result.Status.ShouldBe(MetricBoardWriteStatus.Saved);
        var boards = await Query().GetBoards(Ct);
        boards.Select(x => (x.Name, x.IsDefault, x.Position)).ShouldBe([("Default", true, 0), ("Checkout", false, 1)]);
        boards[1].Id.ShouldBe(result.Id);
        boards[1].Description.ShouldBe("Checkout health");
        boards[1].CreatedBy.ShouldBe("alice");
    }

    [TimedFact]
    public async Task UpsertBoard_RenameTheDefaultBoard_KeepsItsIdentity()
    {
        var id = await Command().EnsureDefaultBoard(Ct);

        var result = await Command().UpsertBoard(new MetricBoard { Id = id, Name = "Main" }, "alice", Ct);

        result.ShouldBeEquivalentTo(new Warp.Core.Models.MetricBoardWriteResult { Status = MetricBoardWriteStatus.Saved, Id = id });
        var board = (await Query().GetBoards(Ct)).ShouldHaveSingleItem();
        board.Name.ShouldBe("Main");
        board.IsDefault.ShouldBeTrue();
    }

    [TimedTheory]
    [InlineData("checkout")]
    [InlineData("CHECKOUT")]
    public async Task UpsertBoard_NameTakenIgnoringCase_IsRefusedForAnInsertAndARename(string name)
    {
        await Command().UpsertBoard(new MetricBoard { Name = "Checkout" }, "alice", Ct);
        var payments = await Command().UpsertBoard(new MetricBoard { Name = "Payments" }, "alice", Ct);

        var insert = await Command().UpsertBoard(new MetricBoard { Name = name }, "bob", Ct);
        var rename = await Command().UpsertBoard(new MetricBoard { Id = payments.Id, Name = name }, "bob", Ct);

        insert.Status.ShouldBe(MetricBoardWriteStatus.NameTaken);
        rename.Status.ShouldBe(MetricBoardWriteStatus.NameTaken);
        (await Query().GetBoards(Ct)).Select(x => x.Name).ShouldBe(["Default", "Checkout", "Payments"]);
    }

    [TimedFact]
    public async Task UpsertBoard_EditOfADeletedBoard_ReturnsNotFoundAndRecreatesNothing()
    {
        var created = await Command().UpsertBoard(new MetricBoard { Name = "Checkout" }, "alice", Ct);
        (await Command().DeleteBoard(created.Id, "alice", Ct)).ShouldBe(MetricBoardDeleteStatus.Deleted);

        var result = await Command().UpsertBoard(new MetricBoard { Id = created.Id, Name = "Checkout 2" }, "alice", Ct);

        result.Status.ShouldBe(MetricBoardWriteStatus.NotFound);
        (await Query().GetBoards(Ct)).ShouldHaveSingleItem().IsDefault.ShouldBeTrue();
    }

    [TimedFact]
    public async Task UpsertBoard_AtTheBoardCap_ReturnsFull()
    {
        await Command().EnsureDefaultBoard(Ct);
        var context = _fixture.CreateContext();
        context.Set<MetricBoard>().AddRange(Enumerable.Range(1, MetricBoardValidation.MaxBoards - 1).Select(x => Board($"b{x}", x)));
        await context.SaveChangesAsync(Ct);

        var result = await Command().UpsertBoard(new MetricBoard { Name = "One too many" }, "alice", Ct);

        result.Status.ShouldBe(MetricBoardWriteStatus.Full);
        (await _fixture.CreateContext().Set<MetricBoard>().CountAsync(Ct)).ShouldBe(MetricBoardValidation.MaxBoards);
    }

    [TimedFact]
    public async Task EnsureDefaultBoard_WhileASeederHoldsTheLock_WaitsAndCreatesOneDefaultBoard()
    {
        // N=2: a seeder is pinned inside the board lock after it created the Default board; the dashboard's first use
        // must wait for that commit and then find it, not create a second Default board.
        var barrier = new BarrierSignal();

        async Task Pin(CancellationToken ct)
        {
            barrier.Running.Release();
            await barrier.CanFinish.WaitAsync(ct);
        }

        var seeding = SeedFromConfigAsync(x => x.For(null).Tiles.Value("Volume", "c"), Pin);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
        var ensure = Command().EnsureDefaultBoard(Ct);

        await Should.ThrowAsync<TimeoutException>(() => ensure.WaitAsync(TimeSpan.FromMilliseconds(500), Ct));
        barrier.CanFinish.Release();
        await seeding;
        var id = await ensure;

        var board = (await _fixture.CreateContext().Set<MetricBoard>().ToListAsync(Ct)).ShouldHaveSingleItem();
        board.Id.ShouldBe(id);
        board.IsDefault.ShouldBeTrue();
        board.Name.ShouldBe("Default");
    }

    [TimedFact]
    public async Task DeleteBoard_RemovesTheBoardAndOnlyItsTiles()
    {
        var checkout = await CreateBoardAsync("Checkout");
        var payments = await CreateBoardAsync("Payments");
        await SeedTilesAsync(Tile("Legacy", "c"), Tile("Checkout volume", "c", boardId: checkout), Tile("Checkout errors", "e", boardId: checkout), Tile("Payments volume", "c", boardId: payments));

        var status = await Command().DeleteBoard(checkout, "alice", Ct);

        status.ShouldBe(MetricBoardDeleteStatus.Deleted);
        (await Query().GetBoards(Ct)).Select(x => x.Name).ShouldBe(["Default", "Payments"]);
        (await Query().GetTiles(Ct)).Select(x => x.Name).ShouldBe(["Legacy", "Payments volume"]);
    }

    [TimedFact]
    public async Task DeleteBoard_TheDefaultBoard_IsRefusedAndDeletesNothing()
    {
        var id = await Command().EnsureDefaultBoard(Ct);
        await SeedTilesAsync(Tile("Legacy", "c"));

        var status = await Command().DeleteBoard(id, "alice", Ct);

        status.ShouldBe(MetricBoardDeleteStatus.DefaultBoard);
        (await _fixture.CreateContext().Set<MetricBoard>().CountAsync(Ct)).ShouldBe(1);
        (await _fixture.CreateContext().Set<MetricBoardTile>().CountAsync(Ct)).ShouldBe(1);
    }

    [TimedFact]
    public async Task DeleteBoard_Unknown_ReturnsNotFound()
    {
        (await Command().DeleteBoard(12345, "alice", Ct)).ShouldBe(MetricBoardDeleteStatus.NotFound);
    }

    [TimedFact]
    public async Task GetBoards_LegacyNullBoardTiles_CountOnTheDefaultBoard()
    {
        var checkout = await CreateBoardAsync("Checkout");
        await SeedTilesAsync(Tile("Legacy 1", "c"), Tile("Legacy 2", "c"), Tile("Checkout volume", "c", boardId: checkout));

        var boards = await Query().GetBoards(Ct);

        boards.Select(x => (x.Name, x.TileCount)).ShouldBe([("Default", 2), ("Checkout", 1)]);
    }

    [TimedFact]
    public async Task GetBoards_BeforeTheDefaultBoardWasCreated_ListsItWithIdZero()
    {
        await SeedTilesAsync(Tile("Legacy", "c"));

        var board = (await Query().GetBoards(Ct)).ShouldHaveSingleItem();

        board.Id.ShouldBe(0);
        board.IsDefault.ShouldBeTrue();
        board.Name.ShouldBe("Default");
        board.TileCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetBoard_Default_ReadsTheLegacyNullBoardTilesAndNotTheNamedBoards()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddMinutes(-20), 3)]);
        var defaultId = await Command().EnsureDefaultBoard(Ct);
        var checkout = await CreateBoardAsync("Checkout");
        await SeedTilesAsync(Tile("Legacy", "c"), Tile("Checkout volume", "c", boardId: checkout));

        var board = await Query().GetBoard(Now.AddHours(-1), Now, Ct);
        var byId = await Query().GetBoard(defaultId, Now.AddHours(-1), Now, Ct);

        board.Board.ShouldNotBeNull().Name.ShouldBe("Default");
        board.Tiles.ShouldHaveSingleItem().Tile.Name.ShouldBe("Legacy");
        board.Tiles[0].Current.ShouldBe(3);
        byId.ShouldNotBeNull().Tiles.ShouldHaveSingleItem().Tile.Name.ShouldBe("Legacy");
    }

    [TimedFact]
    public async Task GetBoard_NamedBoard_ReadsOnlyItsTilesAndUnknownIsNull()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddMinutes(-20), 3)]);
        var checkout = await CreateBoardAsync("Checkout");
        await SeedTilesAsync(Tile("Legacy", "c"), Tile("Checkout volume", "c", boardId: checkout));

        var board = await Query().GetBoard(checkout, Now.AddHours(-1), Now, Ct);
        var unknown = await Query().GetBoard(checkout + 1000, Now.AddHours(-1), Now, Ct);

        board.ShouldNotBeNull();
        board.Board.ShouldNotBeNull().Name.ShouldBe("Checkout");
        board.Tiles.ShouldHaveSingleItem().Tile.Name.ShouldBe("Checkout volume");
        board.Tiles[0].Tile.BoardId.ShouldBe(checkout);
        unknown.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetAllBoards_ReadsTheTilesOfEveryBoard()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddMinutes(-20), 3)]);
        var checkout = await CreateBoardAsync("Checkout");
        await SeedTilesAsync(Tile("Legacy", "c"), Tile("Checkout volume", "c", boardId: checkout));

        var board = await Query().GetAllBoards(Now.AddHours(-1), Now, Ct);

        board.Board.ShouldBeNull();
        board.Tiles.Select(x => x.Tile.Name).ShouldBe(["Legacy", "Checkout volume"]);
    }

    [TimedFact]
    public async Task GetBoard_NamedBoard_StatementCountDoesNotGrowWithItsTilesOrTheOtherBoards()
    {
        await SeedAsync(
            [.. Enumerable.Range(0, 6).Select(x => Instrument($"i{x}", MetricKind.Counter))],
            [.. Enumerable.Range(0, 6).Select(x => Point($"i{x}", Now.AddMinutes(-20), x))]);
        var checkout = await CreateBoardAsync("Checkout");
        var payments = await CreateBoardAsync("Payments");
        await SeedTilesAsync(Tile("t0", "i0", boardId: checkout), Tile("t1", "i1", boardId: checkout, filter: "status=5*"), Tile("t2", "i2", boardId: checkout));
        var small = await CountBoardStatementsAsync(checkout);
        await SeedTilesAsync(Tile("t3", "i3", boardId: checkout), Tile("t4", "i4", boardId: checkout), Tile("p0", "i5", boardId: payments));

        var large = await CountBoardStatementsAsync(checkout);

        // Board + tiles + registry + annotations + at most two grouped metric reads.
        small.ShouldBeLessThanOrEqualTo(6);
        large.ShouldBe(small);
    }

    [TimedFact]
    public async Task Upsert_TileCap_IsPerBoard()
    {
        var checkout = await CreateBoardAsync("Checkout");
        await SeedTilesAsync([.. Enumerable.Range(0, MetricBoardValidation.MaxTiles).Select(x => Tile($"t{x}", "c", position: x + 1))]);

        var onCheckout = await Command().Upsert(Tile("Checkout volume", "c", position: 0, boardId: checkout), Ct);
        var onDefault = await Command().Upsert(Tile("One too many", "c", position: 0), Ct);

        onCheckout.ShouldNotBeNull().ShouldBeGreaterThan(0);
        onDefault.ShouldBeNull();
        (await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(x => x.Id == onCheckout.Value, Ct)).Position.ShouldBe(1);
    }

    [TimedFact]
    public async Task Upsert_MoveATileOntoAFullBoard_IsRefusedAndTheTileStays()
    {
        var checkout = await CreateBoardAsync("Checkout");
        await SeedTilesAsync([.. Enumerable.Range(0, MetricBoardValidation.MaxTiles).Select(x => Tile($"t{x}", "c", position: x + 1, boardId: checkout))]);
        var id = await Command().Upsert(Tile("Mover", "c"), Ct);
        var move = Tile("Mover", "c", boardId: checkout);
        move.Id = id!.Value;

        var result = await Command().Upsert(move, Ct);

        result.ShouldBeNull();
        (await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(x => x.Id == id.Value, Ct)).BoardId.ShouldBeNull();
    }

    [TimedFact]
    public async Task Upsert_MoveATileToAnotherBoard_ChangesOnlyItsBoard()
    {
        var checkout = await CreateBoardAsync("Checkout");
        var id = await Command().Upsert(Tile("Mover", "c"), Ct);
        var move = Tile("Mover", "c", boardId: checkout);
        move.Id = id!.Value;

        (await Command().Upsert(move, Ct)).ShouldBe(id.Value);

        (await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(Ct)).BoardId.ShouldBe(checkout);
    }

    [TimedFact]
    public async Task Upsert_EditWithoutABoardOfATileOnANamedBoard_KeepsItOnItsBoard()
    {
        // An omitted boardId on an edit is not a move: the tile stays on Checkout (it once fell back to Default).
        var checkout = await CreateBoardAsync("Checkout");
        var id = await Command().Upsert(Tile("Volume", "c", boardId: checkout), Ct);
        var edit = Tile("Checkout volume", "c");
        edit.Id = id!.Value;

        (await Command().Upsert(edit, Ct)).ShouldBe(id.Value);

        var stored = await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(Ct);
        stored.Name.ShouldBe("Checkout volume");
        stored.BoardId.ShouldBe(checkout);
    }

    [TimedFact]
    public async Task Upsert_EditWithTheDefaultBoardsId_MovesTheTileToDefault()
    {
        var defaultId = await Command().EnsureDefaultBoard(Ct);
        var checkout = await CreateBoardAsync("Checkout");
        var id = await Command().Upsert(Tile("Volume", "c", boardId: checkout), Ct);
        var move = Tile("Volume", "c", boardId: defaultId);
        move.Id = id!.Value;

        (await Command().Upsert(move, Ct)).ShouldBe(id.Value);

        (await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(Ct)).BoardId.ShouldBeNull();
        (await Query().GetBoards(Ct)).Select(x => (x.Name, x.TileCount)).ShouldBe([("Default", 1), ("Checkout", 0)]);
    }

    [TimedFact]
    public async Task Upsert_TileOnTheDefaultBoardsId_IsStoredWithoutABoard()
    {
        var defaultId = await Command().EnsureDefaultBoard(Ct);

        var id = await Command().Upsert(Tile("Volume", "c", boardId: defaultId), Ct);

        id.ShouldNotBeNull();
        (await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(Ct)).BoardId.ShouldBeNull();
    }

    [TimedFact]
    public async Task Upsert_TileOnADeletedBoard_ReturnsZeroAndInsertsNothing()
    {
        var checkout = await CreateBoardAsync("Checkout");
        (await Command().DeleteBoard(checkout, "alice", Ct)).ShouldBe(MetricBoardDeleteStatus.Deleted);

        var id = await Command().Upsert(Tile("Volume", "c", boardId: checkout), Ct);

        id.ShouldBe(0);
        (await _fixture.CreateContext().Set<MetricBoardTile>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Seeder_NamedBoards_CreatesBoardsAndTilesAndReseedIsIdempotent()
    {
        static void Configure(MetricBoardSeeds x)
        {
            x.For(null).Tiles.Value("Volume", "c");
            x.For("Checkout").Tiles.Value("Volume", "c").Value("Errors", "e");
            x.For("Payments");
        }

        await SeedFromConfigAsync(Configure);
        await SeedFromConfigAsync(Configure);

        var boards = await Query().GetBoards(Ct);
        boards.Select(x => (x.Name, x.TileCount)).ShouldBe([("Default", 1), ("Checkout", 2), ("Payments", 0)]);
        var tiles = await Query().GetTiles(Ct);
        tiles.Select(x => (x.Name, x.BoardId, x.Position)).ShouldBe([("Volume", null, 1), ("Volume", boards[1].Id, 1), ("Errors", boards[1].Id, 2)]);
    }

    [TimedFact]
    public async Task Seeder_BoardStoredUnderAnotherCase_IsMatchedAndGetsTheTiles()
    {
        var stored = await CreateBoardAsync("checkout");

        await SeedFromConfigAsync(x => x.For("Checkout").Tiles.Value("Volume", "c"));

        (await Query().GetBoards(Ct)).Select(x => x.Name).ShouldBe(["Default", "checkout"]);
        (await Query().GetTiles(Ct)).ShouldHaveSingleItem().BoardId.ShouldBe(stored);
    }

    [TimedFact]
    public async Task Seeder_DashboardEditsOfASeededBoardAndTile_SurviveAReseed()
    {
        static void Configure(MetricBoardSeeds x) => x.For("Checkout").Tiles.Value("Volume", "c", check: MetricTileCheck.AtMost(10));
        await SeedFromConfigAsync(Configure);
        var board = (await Query().GetBoards(Ct)).Single(x => !x.IsDefault);
        (await Command().UpsertBoard(new MetricBoard { Id = board.Id, Name = "Checkout", Description = "Edited" }, "alice", Ct)).Status.ShouldBe(MetricBoardWriteStatus.Saved);
        var tile = await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(Ct);
        tile.Name = "Checkout volume";
        tile.Threshold = 50;
        (await Command().Upsert(tile, Ct)).ShouldBe(tile.Id);

        await SeedFromConfigAsync(Configure);

        var reread = (await Query().GetBoards(Ct)).Single(x => !x.IsDefault);
        reread.Description.ShouldBe("Edited");
        var stored = (await Query().GetTiles(Ct)).ShouldHaveSingleItem();
        stored.Name.ShouldBe("Checkout volume");
        stored.Threshold.ShouldBe(50);
    }

    [TimedFact]
    public async Task Seeder_SeededBoardRenamedInTheDashboard_IsFoundByItsSeedKeyAndNotRecreated()
    {
        static void Configure(MetricBoardSeeds x) => x.For("Checkout").Tiles.Value("Volume", "c").Value("Errors", "e");
        await SeedFromConfigAsync(Configure);
        var board = (await Query().GetBoards(Ct)).Single(x => !x.IsDefault);
        (await Command().UpsertBoard(new MetricBoard { Id = board.Id, Name = "Checkout (EU)" }, "alice", Ct)).Status.ShouldBe(MetricBoardWriteStatus.Saved);

        await SeedFromConfigAsync(Configure);

        var stored = await _fixture.CreateContext().Set<MetricBoard>().Where(x => !x.IsDefault).ToListAsync(Ct);
        var renamed = stored.ShouldHaveSingleItem();
        renamed.Id.ShouldBe(board.Id);
        renamed.Name.ShouldBe("Checkout (EU)");
        renamed.SeedKey.ShouldBe("Checkout");
        (await Query().GetTiles(Ct)).Select(x => (x.Name, x.BoardId)).ShouldBe([("Volume", board.Id), ("Errors", board.Id)]);
    }

    [TimedFact]
    public async Task Seeder_BoardCreatedInTheDashboardUnderTheSeedName_IsAdoptedSoALaterRenameKeepsIt()
    {
        var stored = await CreateBoardAsync("Checkout");
        (await _fixture.CreateContext().Set<MetricBoard>().SingleAsync(x => x.Id == stored, Ct)).SeedKey.ShouldBeNull();
        static void Configure(MetricBoardSeeds x) => x.For("Checkout").Tiles.Value("Volume", "c");
        await SeedFromConfigAsync(Configure);
        (await Command().UpsertBoard(new MetricBoard { Id = stored, Name = "Shop checkout" }, "alice", Ct)).Status.ShouldBe(MetricBoardWriteStatus.Saved);

        await SeedFromConfigAsync(Configure);

        (await Query().GetBoards(Ct)).Select(x => x.Name).ShouldBe(["Default", "Shop checkout"]);
        (await _fixture.CreateContext().Set<MetricBoard>().SingleAsync(x => x.Id == stored, Ct)).SeedKey.ShouldBe("Checkout");
        (await Query().GetTiles(Ct)).ShouldHaveSingleItem().BoardId.ShouldBe(stored);
    }

    [TimedFact]
    public async Task Seeder_AnotherSeededBoardRenamedToTheSeedName_SkipsTheSeedAndKeepsNamesUnique()
    {
        await SeedFromConfigAsync(x => x.For("Payments"));
        var payments = (await Query().GetBoards(Ct)).Single(x => !x.IsDefault).Id;
        (await Command().UpsertBoard(new MetricBoard { Id = payments, Name = "Checkout" }, "alice", Ct)).Status.ShouldBe(MetricBoardWriteStatus.Saved);

        await SeedFromConfigAsync(x =>
        {
            x.For("Payments");
            x.For("Checkout").Tiles.Value("Volume", "c");
        });

        (await Query().GetBoards(Ct)).Select(x => x.Name).ShouldBe(["Default", "Checkout"]);
        (await Query().GetTiles(Ct)).ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Seeder_DefaultBoardRenamedToTheSeedName_SkipsTheSeedAndNeverPutsItsTilesOnDefault()
    {
        // Default renamed to "Checkout": the named seed must not match it by name (its tiles would land on Default with no
        // seed key, and once Default is renamed back the seed creates "Checkout" and seeds them a second time).
        var defaultId = await Command().EnsureDefaultBoard(Ct);
        (await Command().UpsertBoard(new MetricBoard { Id = defaultId, Name = "Checkout" }, "alice", Ct)).Status.ShouldBe(MetricBoardWriteStatus.Saved);
        static void Configure(MetricBoardSeeds x) => x.For("Checkout").Tiles.Value("Volume", "c");
        var logs = new CapturingLoggerProvider();

        await SeedFromConfigAsync(Configure, logs: logs);

        var board = (await Query().GetBoards(Ct)).ShouldHaveSingleItem();
        board.ShouldSatisfyAllConditions(x => x.Id.ShouldBe(defaultId), x => x.Name.ShouldBe("Checkout"), x => x.IsDefault.ShouldBeTrue());
        (await Query().GetTiles(Ct)).ShouldBeEmpty();
        (await _fixture.CreateContext().Set<MetricBoard>().SingleAsync(Ct)).SeedKey.ShouldBeNull();
        var log = logs.Logs.ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Error);
        log.Message.ShouldBe("Metric board 'Checkout' was NOT seeded: the Default board was renamed to that name in the dashboard. Rename one of them.");
    }

    [TimedFact]
    public async Task Seeder_DefaultBoardRenamedBackAfterASkippedSeed_SeedsTheNamedBoardOnce()
    {
        var defaultId = await Command().EnsureDefaultBoard(Ct);
        (await Command().UpsertBoard(new MetricBoard { Id = defaultId, Name = "Checkout" }, "alice", Ct)).Status.ShouldBe(MetricBoardWriteStatus.Saved);
        static void Configure(MetricBoardSeeds x) => x.For("Checkout").Tiles.Value("Volume", "c");
        await SeedFromConfigAsync(Configure);
        (await Command().UpsertBoard(new MetricBoard { Id = defaultId, Name = "Default" }, "alice", Ct)).Status.ShouldBe(MetricBoardWriteStatus.Saved);

        await SeedFromConfigAsync(Configure);
        await SeedFromConfigAsync(Configure);

        var boards = await Query().GetBoards(Ct);
        boards.Select(x => (x.Name, x.TileCount)).ShouldBe([("Default", 0), ("Checkout", 1)]);
        (await Query().GetTiles(Ct)).ShouldHaveSingleItem().BoardId.ShouldBe(boards[1].Id);
    }

    [TimedFact]
    public async Task Seeder_TwoReplicasStartAtOnce_SeedOneNamedBoardAndOneCopyOfItsTiles()
    {
        // The first replica is pinned inside the board lock after it created the board and read the tiles (BarrierSignal,
        // N=2); the second must not read the boards until the first commits, or both create "Checkout".
        static void Configure(MetricBoardSeeds x) => x.For("Checkout").Tiles.Value("Volume", "c").Value("Errors", "e");
        var barrier = new BarrierSignal();

        async Task Pin(CancellationToken ct)
        {
            barrier.Running.Release();
            await barrier.CanFinish.WaitAsync(ct);
        }

        var first = SeedFromConfigAsync(Configure, Pin);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();
        var second = SeedFromConfigAsync(Configure, Pin);

        (await barrier.Running.WaitAsync(TimeSpan.FromMilliseconds(500), Ct)).ShouldBeFalse();
        barrier.CanFinish.Release(2);
        await Task.WhenAll(first, second);

        (await Query().GetBoards(Ct)).Select(x => x.Name).ShouldBe(["Default", "Checkout"]);
        (await Query().GetTiles(Ct)).Select(x => x.Name).ShouldBe(["Volume", "Errors"]);
    }

    [TimedFact]
    public async Task GetBoardsRoute_CreatesTheDefaultBoardOnFirstUse()
    {
        await SeedTilesAsync(Tile("Legacy", "c"));
        await using var host = await CreateDashboardHostAsync();

        var boards = await host.Client.GetFromJsonAsync<JsonElement>("/warp/api/metrics/boards", Ct);

        var board = boards.EnumerateArray().ShouldHaveSingleItem();
        board.GetProperty("name").GetString().ShouldBe("Default");
        board.GetProperty("isDefault").GetBoolean().ShouldBeTrue();
        board.GetProperty("id").GetInt32().ShouldBeGreaterThan(0);
        board.GetProperty("tileCount").GetInt32().ShouldBe(1);
    }

    [TimedFact]
    public async Task PostBoardRoute_CreateRenameAndDelete()
    {
        await using var host = await CreateDashboardHostAsync();

        var created = await host.Client.PostAsJsonAsync("/warp/api/metrics/boards", new { id = 0, name = "Checkout" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
        var renamed = await host.Client.PostAsJsonAsync("/warp/api/metrics/boards", new { id, name = "Checkout v2" }, Ct);
        var deleted = await host.Client.DeleteAsync($"/warp/api/metrics/boards/{id}", Ct);
        var again = await host.Client.DeleteAsync($"/warp/api/metrics/boards/{id}", Ct);
        var editDeleted = await host.Client.PostAsJsonAsync("/warp/api/metrics/boards", new { id, name = "Back" }, Ct);

        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        editDeleted.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await editDeleted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        (await Query().GetBoards(Ct)).ShouldHaveSingleItem().IsDefault.ShouldBeTrue();
    }

    [TimedTheory]
    [InlineData("blank")]
    [InlineData("duplicate")]
    [InlineData("long")]
    public async Task PostBoardRoute_Invalid_ReturnsBadRequestWithAnError(string scenario)
    {
        await CreateBoardAsync("Checkout");
        await using var host = await CreateDashboardHostAsync();
        var name = scenario switch
        {
            "blank" => " ",
            "duplicate" => "CHECKOUT",
            _ => new string('n', MetricBoardValidation.MaxBoardNameLength + 1),
        };

        var response = await host.Client.PostAsJsonAsync("/warp/api/metrics/boards", new { id = 0, name }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        (await _fixture.CreateContext().Set<MetricBoard>().CountAsync(Ct)).ShouldBe(2);
    }

    [TimedFact]
    public async Task DeleteBoardRoute_TheDefaultBoard_ReturnsBadRequest()
    {
        var id = await Command().EnsureDefaultBoard(Ct);
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.DeleteAsync($"/warp/api/metrics/boards/{id}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString().ShouldBe("The Default board cannot be deleted.");
    }

    [TimedTheory]
    [InlineData("abc", HttpStatusCode.BadRequest)]
    [InlineData("-1", HttpStatusCode.BadRequest)]
    [InlineData("999999", HttpStatusCode.NotFound)]
    public async Task GetBoardRoute_BadOrUnknownBoard_Returns400Or404(string board, HttpStatusCode expected)
    {
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.GetAsync($"/warp/api/metrics/board?{Range(Now.AddHours(-1), Now)}&board={board}", Ct);

        response.StatusCode.ShouldBe(expected);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
    }

    [TimedFact]
    public async Task GetBoardRoute_WithABoard_ReadsThatBoard()
    {
        var checkout = await CreateBoardAsync("Checkout");
        await SeedTilesAsync(Tile("Legacy", "c"), Tile("Checkout volume", "c", boardId: checkout));
        await using var host = await CreateDashboardHostAsync();

        var board = await host.Client.GetFromJsonAsync<JsonElement>($"/warp/api/metrics/board?{Range(Now.AddHours(-1), Now)}&board={checkout}", Ct);

        board.GetProperty("board").GetProperty("name").GetString().ShouldBe("Checkout");
        board.GetProperty("tiles").EnumerateArray().ShouldHaveSingleItem().GetProperty("tile").GetProperty("name").GetString().ShouldBe("Checkout volume");
    }

    [TimedFact]
    public async Task PostTileRoute_WithABoardId_PutsTheTileOnThatBoardAndADeletedBoardIs404()
    {
        var checkout = await CreateBoardAsync("Checkout");
        var gone = await CreateBoardAsync("Gone");
        (await Command().DeleteBoard(gone, "alice", Ct)).ShouldBe(MetricBoardDeleteStatus.Deleted);
        await using var host = await CreateDashboardHostAsync();

        var ok = await host.Client.PostAsJsonAsync("/warp/api/metrics/board/tiles", new { id = 0, name = "Volume", instrument = "c", boardId = checkout }, Ct);
        var missing = await host.Client.PostAsJsonAsync("/warp/api/metrics/board/tiles", new { id = 0, name = "Volume", instrument = "c", boardId = gone }, Ct);

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _fixture.CreateContext().Set<MetricBoardTile>().SingleAsync(Ct)).BoardId.ShouldBe(checkout);
    }

    [TimedFact]
    public async Task DashboardOnlyHost_SeedsANamedBoardAndServesItById()
    {
        // AddWarp only (no server, no AddMetrics): the named-board seeder runs, and the board routes serve it.
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddMinutes(-10), 4)]);
        await using var probe = _fixture.CreateContext();
        var connectionString = probe.Database.GetConnectionString()!;
        var isPostgres = probe.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);
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
        builder.Services.AddWarp<TestContext>(x => x.AddMetricBoard("Checkout", b => b.Value("Volume", "c", check: MetricTileCheck.AtMost(10))));
        await using var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(Ct);
        try
        {
            using var client = app.GetDashboardTestClient();

            var boards = await client.GetFromJsonAsync<JsonElement>("/warp/api/metrics/boards", Ct);
            var checkout = boards.EnumerateArray().Single(x => string.Equals(x.GetProperty("name").GetString(), "Checkout", StringComparison.Ordinal)).GetProperty("id").GetInt32();
            var board = await client.GetFromJsonAsync<JsonElement>($"/warp/api/metrics/board?{Range(Now.AddHours(-1), Now)}&board={checkout}", Ct);

            var tile = board.GetProperty("tiles").EnumerateArray().ShouldHaveSingleItem();
            tile.GetProperty("current").GetDouble().ShouldBe(4);
            tile.GetProperty("status").GetInt32().ShouldBe((int)SloState.Healthy);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static MetricBoard Board(string name, int position) => new()
    {
        Name = name,
        Position = position,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static MetricBoardTile Tile(string name, string instrument, int position = 1, int? boardId = null, string? filter = null) => new()
    {
        BoardId = boardId,
        Name = name,
        Instrument = instrument,
        Position = position,
        TagFilter = filter,
        Kind = MetricTileKind.Value,
    };

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

    private static string Range(DateTime from, DateTime to)
        => $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    private MetricBoardQueryService<TestContext> Query()
        => new(_fixture.CreateContext(), Options.Create(new WarpConfiguration()), new FakeTimeProvider(new DateTimeOffset(Now)));

    private MetricBoardCommandService<TestContext> Command()
    {
        var context = _fixture.CreateContext();

        return new(context, TimeProvider.System, NullLogger<MetricBoardCommandService<TestContext>>.Instance, TestTasks.QueriesFor(context));
    }

    private async Task<int> CreateBoardAsync(string name)
    {
        var result = await Command().UpsertBoard(new MetricBoard { Name = name }, "seed", Ct);
        result.Status.ShouldBe(MetricBoardWriteStatus.Saved);

        return result.Id;
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

    private async Task<int> CountBoardStatementsAsync(int boardId)
    {
        var counter = new StatementCounter();
        var options = (DbContextOptions<TestContext>)_fixture.CreateContext().GetService<IDbContextOptions>();
        await using var context = new TestContext(new DbContextOptionsBuilder<TestContext>(options).AddInterceptors(counter).Options);
        var query = new MetricBoardQueryService<TestContext>(context, Options.Create(new WarpConfiguration()), new FakeTimeProvider(new DateTimeOffset(Now)));

        var board = await query.GetBoard(boardId, Now.AddHours(-1), Now, Ct);

        board.ShouldNotBeNull().Tiles.ShouldNotBeEmpty();

        return counter.Count;
    }

    private async Task SeedFromConfigAsync(Action<MetricBoardSeeds> configure, Func<CancellationToken, Task>? beforeSave = null, CapturingLoggerProvider? logs = null)
    {
        var seeds = new MetricBoardSeeds();
        configure(seeds);
        var services = new ServiceCollection();
        services.AddScoped(_ => _fixture.CreateContext());
        services.AddScoped<IMetricQueryService>(_ => new MetricQueryService<TestContext>(_fixture.CreateContext(), Options.Create(new WarpConfiguration())));
        services.AddSingleton(TestTasks.QueriesFor(_fixture.CreateContext()));
        await using var provider = services.BuildServiceProvider();
        var seeder = new MetricBoardSeeder<TestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            seeds,
            TimeProvider.System,
            logs is null ? NullLogger<MetricBoardSeeder<TestContext>>.Instance : new CapturingLoggerFactory(logs).CreateLogger<MetricBoardSeeder<TestContext>>())
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
        builder.Services.AddScoped<ISloQueryService>(_ => new SloQueryService<TestContext>(_fixture.CreateContext()));

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(CancellationToken.None);

        return new DashboardHost(app, app.GetDashboardTestClient());
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
