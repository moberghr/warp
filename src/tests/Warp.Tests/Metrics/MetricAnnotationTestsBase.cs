using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
using Warp.Tests.Observability;

namespace Warp.Tests.Metrics;

/// <summary>
/// Database coverage for metric annotations (#333): CRUD with the author and the structured audit log, the validator's
/// registry and objective checks, the window read (ongoing notes included) with its target filters and cap, the SLO
/// association (deleting the objective deletes its notes), the board association (notes attached per tile in the batched
/// read) and acknowledged noise, the routes with their 400s and 404s and the author taken from the authenticated user,
/// and a dashboard-only host. "Now" is a fixed instant; rows are seeded relative to it.
/// </summary>
[GenerateDatabaseTests]
public abstract class MetricAnnotationTestsBase : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly IDatabaseFixture _fixture;

    protected MetricAnnotationTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Upsert_Insert_StoresTheAuthorAndWritesTheAuditLog()
    {
        var logs = new CapturingLoggerProvider();

        var id = await Command(logs).Upsert(Note("c", "Known duplicate race", filter: "error=duplicate"), "alice", Ct);

        var row = await _fixture.CreateContext().Set<MetricAnnotation>().SingleAsync(Ct);
        row.Id.ShouldBe(id!.Value);
        row.Author.ShouldBe("alice");
        row.Note.ShouldBe("Known duplicate race");
        row.TagFilter.ShouldBe("error=duplicate");
        row.CreatedAt.ShouldBe(Now);
        var audit = logs.Logs.ShouldHaveSingleItem();
        audit.Level.ShouldBe(LogLevel.Information);
        audit.Fields["Id"].ShouldBe(id.Value);
        audit.Fields["Author"].ShouldBe("alice");
        audit.Fields["TargetName"].ShouldBe("c");
        audit.Message.ShouldNotContain("Known duplicate race");
    }

    [TimedFact]
    public async Task Upsert_Update_KeepsTheAuthor_ReplacesTheFields_AndAuditsTheEditor()
    {
        var id = await Command().Upsert(Note("c", "First", filter: "a=b", acknowledged: true), "alice", Ct);
        var logs = new CapturingLoggerProvider();
        var update = Note("c", "Second", endsAt: Now.AddHours(1));
        update.Id = id!.Value;

        var updated = await Command(logs).Upsert(update, "bob", Ct);

        updated.ShouldBe(id);
        var row = await _fixture.CreateContext().Set<MetricAnnotation>().SingleAsync(Ct);
        row.Author.ShouldBe("alice");
        row.Note.ShouldBe("Second");
        row.TagFilter.ShouldBeNull();
        row.AcknowledgedNoise.ShouldBeFalse();
        row.EndsAt.ShouldBe(Now.AddHours(1));
        logs.Logs.ShouldHaveSingleItem().Fields["Author"].ShouldBe("bob");
    }

    [TimedFact]
    public async Task Upsert_UpdateOfAnUnknownId_ReturnsNullAndAddsNothing()
    {
        var update = Note("c", "Ghost");
        update.Id = 999;

        (await Command().Upsert(update, "alice", Ct)).ShouldBeNull();

        (await _fixture.CreateContext().Set<MetricAnnotation>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Delete_ExistingThenUnknown_RemovesItAndAuditsOnlyTheDeletion()
    {
        var id = await Command().Upsert(Note("c", "Gone soon"), "alice", Ct);
        var logs = new CapturingLoggerProvider();

        (await Command(logs).Delete(id!.Value, "bob", Ct)).ShouldBeTrue();
        (await Command(logs).Delete(id.Value, "bob", Ct)).ShouldBeFalse();

        (await _fixture.CreateContext().Set<MetricAnnotation>().AnyAsync(Ct)).ShouldBeFalse();
        var audit = logs.Logs.ShouldHaveSingleItem();
        audit.Fields["Author"].ShouldBe("bob");
        audit.Message.ShouldContain("deleted");
    }

    [TimedTheory]
    [InlineData("registered-instrument", true)]
    [InlineData("unknown-instrument", false)]
    [InlineData("existing-slo", true)]
    [InlineData("unknown-slo", false)]
    public async Task Validator_ChecksTheTargetExists(string scenario, bool expected)
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], []);
        var sloId = await SeedSloAsync();
        var annotation = scenario switch
        {
            "registered-instrument" => Note("c", "n"),
            "unknown-instrument" => Note("nope", "n"),
            "existing-slo" => SloNote(sloId, "n"),
            _ => SloNote(sloId + 1000, "n"),
        };

        var (ok, error) = await MetricAnnotationValidator.ValidateAsync(annotation, Metrics(), new SloQueryService<TestContext>(_fixture.CreateContext()), Ct);

        ok.ShouldBe(expected, error);
        if (!expected)
        {
            error.ShouldNotBeNull().ShouldContain(scenario.EndsWith("slo", StringComparison.Ordinal) ? "SLO" : "not registered");
        }
    }

    [TimedFact]
    public async Task GetAnnotations_Window_ReturnsTheOverlappingNotesIncludingOngoingOnes()
    {
        var from = Now.AddHours(-2);
        await SeedNotesAsync(
            Note("c", "ended before", startsAt: from.AddHours(-3), endsAt: from.AddHours(-1)),
            Note("c", "ended exactly at the start", startsAt: from.AddHours(-3), endsAt: from),
            Note("c", "straddles the start", startsAt: from.AddHours(-1), endsAt: from.AddMinutes(30)),
            Note("c", "inside", startsAt: from.AddMinutes(10), endsAt: from.AddMinutes(20)),
            Note("c", "ongoing from before", startsAt: from.AddDays(-30)),
            Note("c", "starts at the end", startsAt: Now),
            Note("c", "ongoing from after", startsAt: Now.AddHours(1)));

        var result = await Query().GetAnnotations(new MetricAnnotationQuery { Instrument = "c", From = from, To = Now }, Ct);

        result.Items.Select(x => x.Note).ShouldBe(["inside", "straddles the start", "ongoing from before"]);
        result.IsTruncated.ShouldBeFalse();
    }

    [TimedFact]
    public async Task GetAnnotations_ByTarget_ReadsOnlyThatTarget_AnInstrumentWithEveryFilter()
    {
        var sloId = await SeedSloAsync();
        await SeedNotesAsync(
            Note("c", "whole instrument"),
            Note("c", "one status", filter: "status=500"),
            Note("other", "other instrument"),
            SloNote(sloId, "objective note"));

        var byInstrument = await Query().GetAnnotations(new MetricAnnotationQuery { Instrument = "c" }, Ct);
        var bySlo = await Query().GetAnnotations(new MetricAnnotationQuery { SloDefinitionId = sloId }, Ct);
        var all = await Query().GetAnnotations(new MetricAnnotationQuery(), Ct);

        byInstrument.Items.Select(x => x.Note).Order(StringComparer.Ordinal).ShouldBe(["one status", "whole instrument"]);
        bySlo.Items.ShouldHaveSingleItem().Note.ShouldBe("objective note");
        all.Items.Count.ShouldBe(4);
    }

    [TimedFact]
    public async Task GetAnnotations_MoreThanTheCap_ReturnsTheNewestAndFlagsTruncation()
    {
        await SeedNotesAsync([.. Enumerable.Range(0, MetricAnnotationValidation.MaxPerRead + 1).Select(x => Note("c", $"n{x}", startsAt: Now.AddMinutes(-x)))]);

        var result = await Query().GetAnnotations(new MetricAnnotationQuery { Instrument = "c" }, Ct);

        result.IsTruncated.ShouldBeTrue();
        result.Items.Count.ShouldBe(MetricAnnotationValidation.MaxPerRead);
        result.Items[0].Note.ShouldBe("n0");
        result.Items.ShouldNotContain(x => string.Equals(x.Note, $"n{MetricAnnotationValidation.MaxPerRead}", StringComparison.Ordinal));
    }

    [TimedFact]
    public async Task GetAnnotations_BothTargetsOrAnInvertedWindow_Throws()
    {
        await Should.ThrowAsync<MetricQueryValidationException>(() => Query().GetAnnotations(new MetricAnnotationQuery { Instrument = "c", SloDefinitionId = 1 }, Ct));
        await Should.ThrowAsync<MetricQueryValidationException>(() => Query().GetAnnotations(new MetricAnnotationQuery { From = Now, To = Now }, Ct));
    }

    [TimedFact]
    public async Task SloDelete_RemovesTheObjectivesNotesOnly()
    {
        var sloId = await SeedSloAsync();
        var otherSloId = await SeedSloAsync("Payments");
        await SeedNotesAsync(SloNote(sloId, "goes"), SloNote(otherSloId, "other objective stays"), Note("c", "stays"));

        var deleted = await new SloCommandService<TestContext>(_fixture.CreateContext(), TimeProvider.System, NullLogger<SloCommandService<TestContext>>.Instance)
            .Delete(sloId, Ct);

        deleted.ShouldBeTrue();
        var rows = await _fixture.CreateContext().Set<MetricAnnotation>().OrderBy(x => x.Note).Select(x => x.Note).ToListAsync(Ct);
        rows.ShouldBe(["other objective stays", "stays"]);
        (await _fixture.CreateContext().Set<MetricAnnotation>().Where(x => x.Note == "other objective stays").Select(x => x.SloDefinitionId).SingleAsync(Ct))
            .ShouldBe(otherSloId);
    }

    [TimedFact]
    public async Task GetBoard_AttachesToEachTileTheNotesThatApplyAndOverlapTheWindow()
    {
        await SeedAsync([Instrument("http", MetricKind.Counter), Instrument("db", MetricKind.Counter)], [Point("http", Now.AddMinutes(-10), sum: 3)]);
        await SeedTilesAsync(Tile("5xx", "http", position: 1, filter: "status=5*"), Tile("4xx", "http", position: 2, filter: "status=4*"), Tile("DB", "db", position: 3));
        await SeedNotesAsync(
            Note("http", "whole instrument", startsAt: Now.AddHours(-3)),
            Note("http", "server errors", filter: "status=503", startsAt: Now.AddMinutes(-30)),
            Note("http", "long gone", startsAt: Now.AddDays(-3), endsAt: Now.AddDays(-2)));

        var board = await Board().GetBoard(Now.AddHours(-1), Now, Ct);

        var notes = board.Tiles.ToDictionary(x => x.Tile.Name, x => x.Annotations.Select(y => y.Note).ToList(), StringComparer.Ordinal);
        notes["5xx"].ShouldBe(["server errors", "whole instrument"]);
        notes["4xx"].ShouldBe(["whole instrument"]);
        notes["DB"].ShouldBeEmpty();
    }

    [TimedTheory]
    [InlineData("acknowledged-ongoing", SloState.Acknowledged)]
    [InlineData("acknowledged-ended", SloState.Breaching)]
    [InlineData("plain-note", SloState.Breaching)]
    [InlineData("acknowledged-other-filter", SloState.Breaching)]
    [InlineData("acknowledged-same-filter", SloState.Acknowledged)]
    [InlineData("acknowledged-wider-prefix", SloState.Acknowledged)]
    public async Task GetBoard_BreachingTile_ReadsAcknowledgedOnlyUnderAnActiveAcknowledgedNoiseNote(string scenario, SloState expected)
    {
        await SeedAsync([Instrument("http", MetricKind.Counter)], [Point("http", Now.AddMinutes(-10), sum: 7, tags: "status=500")]);
        await SeedTilesAsync(Tile("5xx", "http", filter: "status=5*", rule: MetricTileStatusRule.Threshold, threshold: 5, comparison: SloComparison.AtMost));
        await SeedNotesAsync(scenario switch
        {
            "acknowledged-ongoing" => Note("http", "known", startsAt: Now.AddDays(-1), acknowledged: true),
            "acknowledged-ended" => Note("http", "known", startsAt: Now.AddHours(-2), endsAt: Now.AddMinutes(-5), acknowledged: true),
            "plain-note" => Note("http", "known", startsAt: Now.AddDays(-1)),
            "acknowledged-same-filter" => Note("http", "known", filter: "status=5*", startsAt: Now.AddDays(-1), acknowledged: true),
            "acknowledged-wider-prefix" => Note("http", "known", filter: "status=*", startsAt: Now.AddDays(-1), acknowledged: true),
            _ => Note("http", "known", filter: "status=4*", startsAt: Now.AddDays(-1), acknowledged: true),
        });

        var tile = (await Board().GetBoard(Now.AddHours(-1), Now, Ct)).Tiles.ShouldHaveSingleItem();

        tile.Status.ShouldBe(expected);
        tile.UnacknowledgedStatus.ShouldBe(SloState.Breaching);
        tile.Current.ShouldBe(7);
    }

    [TimedTheory]
    [InlineData("status=503", "status=5*")]
    [InlineData("route=/pay", "status=5*")]
    [InlineData("outcome=error", null)]
    public async Task GetBoard_AcknowledgedNoteOnOnlyPartOfTheTile_IsShownButDoesNotAcknowledge(string noteFilter, string? tileFilter)
    {
        // The note overlaps the tile (it is listed on it) but does not cover everything the tile reads: a known problem in
        // one slice must not excuse a breach in the rest.
        await SeedAsync([Instrument("http", MetricKind.Counter)], [Point("http", Now.AddMinutes(-10), sum: 7, tags: "status=500")]);
        await SeedTilesAsync(Tile("Errors", "http", filter: tileFilter, rule: MetricTileStatusRule.Threshold, threshold: 5, comparison: SloComparison.AtMost));
        await SeedNotesAsync(Note("http", "known", filter: noteFilter, startsAt: Now.AddDays(-1), acknowledged: true));

        var tile = (await Board().GetBoard(Now.AddHours(-1), Now, Ct)).Tiles.ShouldHaveSingleItem();

        tile.Status.ShouldBe(SloState.Breaching);
        tile.Annotations.ShouldHaveSingleItem().AcknowledgedNoise.ShouldBeTrue();
    }

    [TimedFact]
    public async Task GetBoard_MoreNotesThanTheCap_KeepsTheOngoingAcknowledgedNoteAndFlagsTruncation()
    {
        // 200 newer, ended notes and one OLD ongoing acknowledged-noise note: newest-first alone would drop the one note
        // that changes the verdict.
        await SeedAsync([Instrument("http", MetricKind.Counter)], [Point("http", Now.AddMinutes(-10), sum: 7)]);
        await SeedTilesAsync(Tile("All", "http", rule: MetricTileStatusRule.Threshold, threshold: 5, comparison: SloComparison.AtMost));
        await SeedNotesAsync(
        [
            Note("http", "known noise", startsAt: Now.AddDays(-30), acknowledged: true),
            .. Enumerable.Range(0, MetricAnnotationValidation.MaxPerRead)
                .Select(x => Note("http", $"n{x}", startsAt: Now.AddMinutes(-50).AddSeconds(x), endsAt: Now.AddMinutes(-20))),
        ]);

        var board = await Board().GetBoard(Now.AddHours(-1), Now, Ct);

        board.AnnotationsTruncated.ShouldBeTrue();
        var tile = board.Tiles.ShouldHaveSingleItem();
        tile.Annotations.Count.ShouldBe(MetricAnnotationValidation.MaxPerRead);
        tile.Annotations.ShouldContain(x => string.Equals(x.Note, "known noise", StringComparison.Ordinal));
        tile.Annotations.ShouldNotContain(x => string.Equals(x.Note, "n0", StringComparison.Ordinal));
        tile.Annotations[0].Note.ShouldBe($"n{MetricAnnotationValidation.MaxPerRead - 1}");
        tile.Status.ShouldBe(SloState.Acknowledged);
    }

    [TimedFact]
    public async Task GetBoard_MoreOngoingNotesThanTheCap_KeepsTheActiveAcknowledgedNoteFirst()
    {
        // 200 newer open-ended notes and one OLD acknowledged-noise note that ends after the window (so not open-ended):
        // ongoing-first alone would fill the cap with the open-ended notes and drop the one note that acknowledges the
        // breach. Active acknowledged noise comes first, then the other active notes, then newest.
        await SeedAsync([Instrument("http", MetricKind.Counter)], [Point("http", Now.AddMinutes(-10), sum: 7)]);
        await SeedTilesAsync(Tile("All", "http", rule: MetricTileStatusRule.Threshold, threshold: 5, comparison: SloComparison.AtMost));
        await SeedNotesAsync(
        [
            Note("http", "known noise", startsAt: Now.AddDays(-30), endsAt: Now.AddHours(2), acknowledged: true),
            Note("http", "ended noise", startsAt: Now.AddDays(-29), endsAt: Now.AddMinutes(-40), acknowledged: true),
            .. Enumerable.Range(0, MetricAnnotationValidation.MaxPerRead)
                .Select(x => Note("http", $"n{x}", startsAt: Now.AddMinutes(-50).AddSeconds(x))),
        ]);

        var board = await Board().GetBoard(Now.AddHours(-1), Now, Ct);

        board.AnnotationsTruncated.ShouldBeTrue();
        var tile = board.Tiles.ShouldHaveSingleItem();
        tile.Annotations.Count.ShouldBe(MetricAnnotationValidation.MaxPerRead);
        tile.Annotations.ShouldContain(x => string.Equals(x.Note, "known noise", StringComparison.Ordinal));
        tile.Annotations.ShouldNotContain(x => string.Equals(x.Note, "ended noise", StringComparison.Ordinal));
        tile.Annotations.ShouldNotContain(x => string.Equals(x.Note, "n0", StringComparison.Ordinal));
        tile.Status.ShouldBe(SloState.Acknowledged);
    }

    [TimedFact]
    public async Task GetBoard_NotesWithinTheCap_AreNotTruncated()
    {
        await SeedAsync([Instrument("http", MetricKind.Counter)], []);
        await SeedTilesAsync(Tile("All", "http"));
        await SeedNotesAsync(Note("http", "one", startsAt: Now.AddMinutes(-30)));

        var board = await Board().GetBoard(Now.AddHours(-1), Now, Ct);

        board.AnnotationsTruncated.ShouldBeFalse();
        board.Tiles.ShouldHaveSingleItem().Annotations.ShouldHaveSingleItem();
    }

    [TimedFact]
    public async Task GetBoard_HealthyTileUnderAcknowledgedNoise_StaysHealthy()
    {
        await SeedAsync([Instrument("http", MetricKind.Counter)], [Point("http", Now.AddMinutes(-10), sum: 1)]);
        await SeedTilesAsync(Tile("All", "http", rule: MetricTileStatusRule.Threshold, threshold: 5, comparison: SloComparison.AtMost));
        await SeedNotesAsync(Note("http", "known", startsAt: Now.AddDays(-1), acknowledged: true));

        var tile = (await Board().GetBoard(Now.AddHours(-1), Now, Ct)).Tiles.ShouldHaveSingleItem();

        tile.Status.ShouldBe(SloState.Healthy);
        tile.Annotations.ShouldHaveSingleItem();
    }

    [TimedFact]
    public async Task PostAnnotation_AuthenticatedUser_IsTheAuthor()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], []);
        await using var host = await CreateDashboardHostAsync(user: "carol");

        var response = await host.Client.PostAsJsonAsync(
            "/warp/api/annotations",
            new { id = 0, note = "Known race", target = (int)MetricAnnotationTarget.Instrument, instrument = "c", startsAt = Now.AddHours(-1), author = "mallory" },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var row = await _fixture.CreateContext().Set<MetricAnnotation>().SingleAsync(Ct);
        row.Author.ShouldBe("carol");
        row.EndsAt.ShouldBeNull();
    }

    [TimedFact]
    public async Task PostAnnotation_OpenDashboard_IsAuthoredByAnonymous()
    {
        var sloId = await SeedSloAsync();
        await using var host = await CreateDashboardHostAsync(user: null);

        var response = await host.Client.PostAsJsonAsync(
            "/warp/api/annotations",
            new { id = 0, note = "Planned maintenance", target = (int)MetricAnnotationTarget.Slo, sloDefinitionId = sloId, startsAt = Now, endsAt = Now.AddHours(2) },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var row = await _fixture.CreateContext().Set<MetricAnnotation>().SingleAsync(Ct);
        row.Author.ShouldBe(MetricAnnotationValidation.AnonymousAuthor);
        row.SloDefinitionId.ShouldBe(sloId);
    }

    [TimedTheory]
    [InlineData("unknown-instrument")]
    [InlineData("unknown-slo")]
    [InlineData("too-long")]
    [InlineData("end-before-start")]
    [InlineData("bad-filter")]
    [InlineData("no-note")]
    public async Task PostAnnotation_Invalid_ReturnsBadRequestAndPersistsNothing(string scenario)
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], []);
        await using var host = await CreateDashboardHostAsync(user: "carol");
        object body = scenario switch
        {
            "unknown-instrument" => new { id = 0, note = "n", instrument = "nope", startsAt = Now },
            "unknown-slo" => new { id = 0, note = "n", target = (int)MetricAnnotationTarget.Slo, sloDefinitionId = 4242, startsAt = Now },
            "too-long" => new { id = 0, note = new string('x', MetricAnnotationValidation.MaxNoteLength + 1), instrument = "c", startsAt = Now },
            "end-before-start" => new { id = 0, note = "n", instrument = "c", startsAt = Now, endsAt = Now.AddMinutes(-1) },
            "bad-filter" => new { id = 0, note = "n", instrument = "c", tagFilter = "status", startsAt = Now },
            _ => new { id = 0, note = string.Empty, instrument = "c", startsAt = Now },
        };

        var response = await host.Client.PostAsJsonAsync("/warp/api/annotations", body, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _fixture.CreateContext().Set<MetricAnnotation>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task PostAnnotation_UpdateOfAnUnknownId_ReturnsNotFound()
    {
        await SeedAsync([Instrument("c", MetricKind.Counter)], []);
        await using var host = await CreateDashboardHostAsync(user: "carol");

        var response = await host.Client.PostAsJsonAsync("/warp/api/annotations", new { id = 777, note = "n", instrument = "c", startsAt = Now }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("error").GetString().ShouldBe("The annotation no longer exists: it was deleted while you edited it.");
    }

    [TimedFact]
    public async Task DeleteAnnotation_Route_RemovesItThenReportsNotFound()
    {
        var id = await Command().Upsert(Note("c", "n"), "alice", Ct);
        await using var host = await CreateDashboardHostAsync(user: "carol");

        var first = await host.Client.DeleteAsync($"/warp/api/annotations/{id}", Ct);
        var second = await host.Client.DeleteAsync($"/warp/api/annotations/{id}", Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [TimedFact]
    public async Task GetAnnotations_Route_ReturnsTheWindowsNotes()
    {
        await SeedNotesAsync(Note("c", "inside", startsAt: Now.AddMinutes(-30)), Note("c", "old", startsAt: Now.AddDays(-3), endsAt: Now.AddDays(-2)));
        await using var host = await CreateDashboardHostAsync(user: null);

        var json = await host.Client.GetFromJsonAsync<JsonElement>($"/warp/api/annotations?instrument=c&{Range(Now.AddHours(-1), Now)}", Ct);

        var item = json.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("note").GetString().ShouldBe("inside");
        item.GetProperty("endsAt").ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("isTruncated").GetBoolean().ShouldBeFalse();
    }

    [TimedTheory]
    [InlineData("slo=abc")]
    [InlineData("slo=0")]
    [InlineData("from=nonsense")]
    [InlineData("to=2026-11-13")]
    [InlineData("from=2026-11-13T10:00:00Z&to=2026-11-13T09:00:00Z")]
    [InlineData("instrument=c&slo=1")]
    public async Task GetAnnotations_InvalidQuery_ReturnsBadRequest(string queryString)
    {
        await using var host = await CreateDashboardHostAsync(user: null);

        var response = await host.Client.GetAsync($"/warp/api/annotations?{queryString}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TimedFact]
    public async Task DashboardOnlyHost_AddWarpWithoutAddMetrics_EditsAndShowsAnnotations()
    {
        // No worker, no server, no AddMetrics: the annotation services come from AddWarp alone, and the board attaches the
        // note written through the route to the tile on the same instrument.
        await SeedAsync([Instrument("c", MetricKind.Counter)], [Point("c", Now.AddMinutes(-10), sum: 4)]);
        await SeedTilesAsync(Tile("Volume", "c"));
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
        builder.Services.AddWarp<TestContext>(_ => { });
        await using var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(Ct);
        try
        {
            app.Services.GetService<IMetricsMarker>().ShouldBeNull();
            using var client = app.GetTestClient();

            var post = await client.PostAsJsonAsync("/warp/api/annotations", new { id = 0, note = "Backfill running", instrument = "c", startsAt = Now.AddHours(-2) }, Ct);
            var list = await client.GetFromJsonAsync<JsonElement>("/warp/api/annotations?instrument=c", Ct);
            var board = await client.GetFromJsonAsync<JsonElement>($"/warp/api/metrics/board?{Range(Now.AddHours(-1), Now)}", Ct);

            post.StatusCode.ShouldBe(HttpStatusCode.OK);
            list.GetProperty("items").EnumerateArray().ShouldHaveSingleItem().GetProperty("author").GetString().ShouldBe(MetricAnnotationValidation.AnonymousAuthor);
            var tile = board.GetProperty("tiles").EnumerateArray().ShouldHaveSingleItem();
            tile.GetProperty("annotations").EnumerateArray().ShouldHaveSingleItem().GetProperty("note").GetString().ShouldBe("Backfill running");
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static MetricAnnotation Note(
        string instrument,
        string note,
        string? filter = null,
        DateTime? startsAt = null,
        DateTime? endsAt = null,
        bool acknowledged = false)
        => new()
        {
            Target = MetricAnnotationTarget.Instrument,
            Instrument = instrument,
            TagFilter = filter,
            Note = note,
            StartsAt = startsAt ?? Now.AddHours(-1),
            EndsAt = endsAt,
            AcknowledgedNoise = acknowledged,
        };

    private static MetricAnnotation SloNote(int sloId, string note)
        => new()
        {
            Target = MetricAnnotationTarget.Slo,
            SloDefinitionId = sloId,
            Note = note,
            StartsAt = Now.AddHours(-1),
        };

    private static MetricBoardTile Tile(
        string name,
        string instrument,
        int position = 1,
        string? filter = null,
        MetricTileStatusRule rule = MetricTileStatusRule.None,
        double? threshold = null,
        SloComparison? comparison = null)
        => new()
        {
            Name = name,
            Instrument = instrument,
            Position = position,
            TagFilter = filter,
            Kind = MetricTileKind.Value,
            StatusRule = rule,
            Threshold = threshold,
            Comparison = comparison,
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

    private static MetricPoint Point(string instrument, DateTime bucket, double sum, string tags = "-")
        => new()
        {
            Instrument = instrument,
            Tags = tags,
            Tier = MetricTier.Fine,
            BucketStart = bucket,
            Count = 1,
            Sum = sum,
        };

    private static string Range(DateTime from, DateTime to)
        => $"from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    private MetricAnnotationQueryService<TestContext> Query() => new(_fixture.CreateContext());

    private MetricAnnotationCommandService<TestContext> Command(CapturingLoggerProvider? logs = null)
    {
        var logger = logs is null
            ? NullLogger<MetricAnnotationCommandService<TestContext>>.Instance
            : new CapturingLoggerFactory(logs).CreateLogger<MetricAnnotationCommandService<TestContext>>();

        return new MetricAnnotationCommandService<TestContext>(_fixture.CreateContext(), new FakeTimeProvider(new DateTimeOffset(Now)), logger);
    }

    private MetricQueryService<TestContext> Metrics() => new(_fixture.CreateContext(), Options.Create(new WarpConfiguration()));

    private MetricBoardQueryService<TestContext> Board()
        => new(_fixture.CreateContext(), Options.Create(new WarpConfiguration()), new FakeTimeProvider(new DateTimeOffset(Now)));

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

    private async Task SeedNotesAsync(params MetricAnnotation[] notes)
    {
        var context = _fixture.CreateContext();
        foreach (var note in notes)
        {
            MetricAnnotationValidation.TryValidate(note, out var error).ShouldBeTrue(error);
            note.Author = "seed";
            note.CreatedAt = Now;
            note.UpdatedAt = Now;
        }

        context.Set<MetricAnnotation>().AddRange(notes);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<int> SeedSloAsync(string name = "Checkout")
    {
        var context = _fixture.CreateContext();
        var definition = new SloDefinition
        {
            Name = name,
            Kind = SloKind.SuccessRate,
            Dimension = name,
            TargetValue = 0.99,
            WindowSeconds = 3600,
            Enabled = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        context.Set<SloDefinition>().Add(definition);
        await context.SaveChangesAsync(Ct);

        return definition.Id;
    }

    private async Task<DashboardHost> CreateDashboardHostAsync(string? user)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        builder.Services.AddScoped<IMetricQueryService>(_ => Metrics());
        builder.Services.AddScoped<ISloQueryService>(_ => new SloQueryService<TestContext>(_fixture.CreateContext()));
        builder.Services.AddScoped<IMetricAnnotationQueryService>(_ => Query());
        builder.Services.AddScoped<IMetricAnnotationCommandService>(_ => Command());

        var app = builder.Build();

        // Stands in for whichever gate the host put in front of the dashboard: an authenticated principal with a name.
        if (user is not null)
        {
            app.Use((context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test"));

                return next(context);
            });
        }

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
