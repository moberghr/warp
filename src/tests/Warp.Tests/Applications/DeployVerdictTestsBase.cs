using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Applications;

/// <summary>
/// Database coverage for the post-deploy verdict: pass / warn / fail from a board tile over [deploy, now), acknowledged
/// noise that never fails, pending inside the settle window, noData, the latest deploy vs an explicit (superseded) version
/// with its previous version, application scoping of tiles and SLOs, the route with its 404s and 400s, and a dashboard-only
/// host (AddWarp alone). "Now" is a fixed instant; deploys and metric rows are seeded relative to it.
/// </summary>
[GenerateDatabaseTests]
public abstract class DeployVerdictTestsBase : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly IDatabaseFixture _fixture;

    protected DeployVerdictTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedTheory]
    [InlineData(4.0, DeployVerdicts.Pass, "healthy")]
    [InlineData(9.0, DeployVerdicts.Warn, "warning")]
    [InlineData(20.0, DeployVerdicts.Fail, "breaching")]
    public async Task GetVerdict_ThresholdTileOverTheDeployWindow_GivesTheVerdict(double sinceDeploy, string expected, string state)
    {
        await SeedDeployAsync("api", "1.0.0", Now.AddDays(-2));
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(
            Point(Now.AddMinutes(-30), sinceDeploy),
            Point(Now.AddMinutes(-90), 1000));
        await SeedTilesAsync(Tile("Errors", threshold: 10));

        var verdict = await Service().GetVerdict(Query("api"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(expected);
        verdict.Deploy.Version.ShouldBe("2.0.0");
        verdict.Deploy.PreviousVersion.ShouldBe("1.0.0");
        verdict.Deploy.SupersededBy.ShouldBeNull();
        verdict.Window.From.ShouldBe(Now.AddHours(-1));
        verdict.Window.To.ShouldBe(Now.AddMinutes(-5));
        var check = verdict.Checks.ShouldHaveSingleItem();
        check.State.ShouldBe(state);
        check.Value.ShouldBe(sinceDeploy);
        check.Target.ShouldBe(10);
    }

    [TimedFact]
    public async Task GetVerdict_BreachingTileWithActiveAcknowledgedNoise_PassesAndMarksTheCheck()
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(Point(Now.AddMinutes(-30), 20));
        await SeedTilesAsync(Tile("Errors", threshold: 10));
        await SeedAsync(new MetricAnnotation
        {
            Target = MetricAnnotationTarget.Instrument,
            Instrument = "c",
            Note = "Known retry storm from a partner.",
            Author = "ops",
            StartsAt = Now.AddHours(-2),
            AcknowledgedNoise = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        });

        var verdict = await Service().GetVerdict(Query("api"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.Pass);
        var check = verdict.Checks.ShouldHaveSingleItem();
        check.Acknowledged.ShouldBeTrue();
        check.State.ShouldBe("acknowledged");
        check.UnacknowledgedState.ShouldBe("breaching");
    }

    [TimedFact]
    public async Task GetVerdict_DeployLateInABucket_IsPendingAndLeavesThePreDeployBucketOut()
    {
        // Deployed 11:49:50, 10 s before the 11:50 boundary, with a 10-minute settle window. The 11:45 bucket holds almost
        // only pre-deploy traffic (100 events) and is never counted; the clean window is 11:50-11:55 (the 11:55 bucket is
        // still filling), 20 events, over the threshold. 10 minutes of clean data are complete at 12:00:30.
        await SeedDeployAsync("api", "2.0.0", Now.AddMinutes(-10).AddSeconds(-10));
        await SeedMetricsAsync(Point(Now.AddMinutes(-15), 100), Point(Now.AddMinutes(-10), 20));
        await SeedTilesAsync(Tile("Errors", threshold: 10));

        var verdict = await Service().GetVerdict(new DeployVerdictQuery { Application = "api", MinWindow = TimeSpan.FromMinutes(10) }, Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.Pending);
        verdict.Window.From.ShouldBe(Now.AddMinutes(-10));
        verdict.Window.To.ShouldBe(Now.AddMinutes(-5));
        verdict.Window.Settled.ShouldBeFalse();
        verdict.Window.SettlesAt.ShouldBe(Now.AddSeconds(30));
        var check = verdict.Checks.ShouldHaveSingleItem();
        check.State.ShouldBe("breaching");
        check.Value.ShouldNotBeNull().ShouldBe(20, 1e-9);
    }

    [TimedFact]
    public async Task GetVerdict_DeployInTheFirstSecondsOfABucket_SkipsThatBucketAndSettlesOnTheNext()
    {
        // Deployed 11:45:10, 10 s into the 11:45 bucket: that bucket still holds 10 s of the old release (its 100 events
        // would fail the tile), so the window starts at 11:50. 5 minutes of clean data were complete at 11:55:30.
        await SeedDeployAsync("api", "2.0.0", Now.AddMinutes(-15).AddSeconds(10));
        await SeedMetricsAsync(Point(Now.AddMinutes(-15), 100), Point(Now.AddMinutes(-10), 4));
        await SeedTilesAsync(Tile("Errors", threshold: 10));

        var verdict = await Service().GetVerdict(Query("api"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.Pass);
        verdict.Window.From.ShouldBe(Now.AddMinutes(-10));
        verdict.Window.SettlesAt.ShouldBe(Now.AddMinutes(-4).AddSeconds(-30));
        verdict.Checks.ShouldHaveSingleItem().Value.ShouldBe(4);
    }

    [TimedFact]
    public async Task GetVerdict_EventsOnlyInTheStillFillingBucket_DoNotCountYet()
    {
        // #328: the bucket holding now - flush interval is still being written, so it is left out of the window (and of
        // the baselines alike); a deploy whose only events are there has no complete data.
        await SeedDeployAsync("api", "2.0.0", Now.AddMinutes(-30));
        await SeedMetricsAsync(Point(Now.AddMinutes(-5), 100));
        await SeedTilesAsync(Tile("Errors", threshold: 10));

        var verdict = await Service().GetVerdict(Query("api"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Checks.ShouldHaveSingleItem().State.ShouldBe("noData");
        verdict.Verdict.ShouldBe(DeployVerdicts.NoData);
    }

    [TimedTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task GetVerdict_DeployAtNowOrInTheFuture_IsPendingWithAnEmptyWindow(int minutesAhead)
    {
        // FirstSeenAt = now (the deploy is this instant) or now + 1 min (clock skew between the deploying host and this
        // one): the window is empty, the board is not read, and nothing throws.
        await SeedDeployAsync("api", "2.0.0", Now.AddMinutes(minutesAhead));
        await SeedMetricsAsync(Point(Now.AddMinutes(-5), 100));
        await SeedTilesAsync(Tile("Errors", threshold: 10));

        var verdict = await Service().GetVerdict(Query("api"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.Pending);
        verdict.Window.From.ShouldBe(verdict.Window.To);
        verdict.Window.Settled.ShouldBeFalse();
        verdict.Checks.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task GetVerdict_SupersededInsideTheSettleWindow_IsJudgedNotPending()
    {
        // v2 at 11:00, v3 at 11:07, 5-minute settle window: v2's clean window is the one bucket 11:00-11:05 (v3 rounded
        // down), which can never grow, so it is judged over that.
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedDeployAsync("api", "3.0.0", Now.AddHours(-1).AddMinutes(7));
        await SeedMetricsAsync(Point(Now.AddHours(-1), 50), Point(Now.AddHours(-1).AddMinutes(5), 1000));
        await SeedTilesAsync(Tile("Errors", threshold: 10));

        var verdict = await Service().GetVerdict(Query("api", version: "2.0.0"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.Fail);
        verdict.Deploy.SupersededBy.ShouldBe("3.0.0");
        verdict.Window.Settled.ShouldBeTrue();
        verdict.Window.SettlesAt.ShouldBe(Now.AddHours(-1).AddMinutes(5).AddSeconds(30));
        verdict.Window.To.ShouldBe(Now.AddHours(-1).AddMinutes(5));
        var check = verdict.Checks.ShouldHaveSingleItem();
        check.State.ShouldBe("breaching");
        check.Value.ShouldBe(50);
    }

    [TimedFact]
    public async Task GetVerdict_SupersededBeforeACompleteBucket_IsNoDataNotPending()
    {
        // v2 at 11:00:10, v3 at 11:04:50: no whole bucket between them, so there is nothing of v2's alone to judge.
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1).AddSeconds(10));
        await SeedDeployAsync("api", "3.0.0", Now.AddHours(-1).AddMinutes(4).AddSeconds(50));
        await SeedMetricsAsync(Point(Now.AddHours(-1), 50));
        await SeedTilesAsync(Tile("Errors", threshold: 10));

        var verdict = await Service().GetVerdict(Query("api", version: "2.0.0"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.NoData);
        verdict.Window.Settled.ShouldBeTrue();
        verdict.Window.To.ShouldBe(verdict.Window.From);
        verdict.Summary.ShouldContain("superseded at 2026-11-16T11:04:50Z, before a complete metric bucket after the deploy");
        verdict.Checks.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task GetVerdict_MinCoverageAboveTheShareWithData_IsNoData()
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(Point(Now.AddMinutes(-30), 4));
        await SeedTilesAsync(Tile("Errors", threshold: 10));
        await SeedSloAsync("Evaluated before the deploy", "api", SloState.Healthy, evaluatedAt: Now.AddHours(-2));

        var verdict = await Service().GetVerdict(Query("api", minCoverage: 0.6), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.NoData);
        verdict.ChecksWithData.ShouldBe(1);
        verdict.ChecksTotal.ShouldBe(2);
        verdict.Summary.ShouldStartWith("noData (1 of 2 checks had data, below minCoverage 0.6)");
    }

    [TimedFact]
    public async Task GetVerdict_ChartTileBesideAThresholdTile_IsNotACheck()
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(Point(Now.AddMinutes(-30), 4));
        var chart = new MetricBoardTile
        {
            Name = "Errors by version",
            Instrument = "c",
            Position = 2,
            Kind = MetricTileKind.Chart,
            ChartQuery = "breakdown=service.version",
            StatusRule = MetricTileStatusRule.None,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        await SeedTilesAsync(Tile("Errors", threshold: 10), chart);

        var verdict = await Service().GetVerdict(Query("api"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.Pass);
        verdict.ChecksTotal.ShouldBe(1);
        verdict.Checks.ShouldHaveSingleItem().Name.ShouldBe("Errors");
    }

    [TimedFact]
    public async Task GetVerdict_TileWithoutDataAndNoSlos_IsNoData()
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(Point(Now.AddDays(-3), 5));
        await SeedTilesAsync(Tile("Errors", threshold: 10), Tile("No rule", threshold: null, position: 2));

        var verdict = await Service().GetVerdict(Query("api"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.NoData);
        verdict.Checks.ShouldHaveSingleItem().State.ShouldBe("noData");
    }

    [TimedFact]
    public async Task GetVerdict_ExplicitSupersededVersion_JudgesItsOwnLifetimeWithoutSlos()
    {
        await SeedDeployAsync("api", "1.0.0", Now.AddDays(-2));
        await SeedDeployAsync("api", "1.1.0", Now.AddHours(-5));
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(
            Point(Now.AddHours(-3), 20),
            Point(Now.AddMinutes(-30), 1));
        await SeedTilesAsync(Tile("Errors", threshold: 10));
        await SeedSloAsync("Success", application: null, SloState.Healthy);

        var verdict = await Service().GetVerdict(Query("api", version: "1.1.0"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.Fail);
        verdict.Deploy.PreviousVersion.ShouldBe("1.0.0");
        verdict.Deploy.SupersededBy.ShouldBe("2.0.0");
        verdict.Window.From.ShouldBe(Now.AddHours(-5));
        verdict.Window.To.ShouldBe(Now.AddHours(-1));
        verdict.Checks.ShouldHaveSingleItem().Source.ShouldBe(DeployVerdictSources.Tile);
    }

    [TimedFact]
    public async Task GetVerdict_Environment_PicksThatEnvironmentsLatestDeploy()
    {
        await SeedDeployAsync("api", "1.0.0", Now.AddDays(-2), "prod");
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-3), "prod");
        await SeedDeployAsync("api", "3.0.0", Now.AddHours(-1), "staging");

        var verdict = await Service().GetVerdict(Query("api", environment: "prod"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Deploy.Version.ShouldBe("2.0.0");
        verdict.Deploy.Environment.ShouldBe("prod");
        verdict.Deploy.PreviousVersion.ShouldBe("1.0.0");
    }

    [TimedFact]
    public async Task GetVerdict_TilesAndSlosOfOtherApplications_DoNotCount()
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(
            Point(Now.AddMinutes(-30), 1, application: "api"),
            Point(Now.AddMinutes(-30), 50, application: "admin"));
        await SeedTilesAsync(
            Tile("Api errors", threshold: 10, application: "api"),
            Tile("Admin errors", threshold: 10, application: "admin", position: 2));
        await SeedSloAsync("Admin success", "admin", SloState.Breaching);
        await SeedSloAsync("Api success", "api", SloState.Warning);
        await SeedSloAsync("Platform success", null, SloState.Healthy);

        var verdict = await Service().GetVerdict(Query("api"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.Warn);
        verdict.Checks.Select(x => (x.Source, x.Name, x.State)).ShouldBe(
        [
            (DeployVerdictSources.Tile, "Api errors", "healthy"),
            (DeployVerdictSources.Slo, "Api success", "warning"),
            (DeployVerdictSources.Slo, "Platform success", "healthy"),
        ]);
    }

    [TimedFact]
    public async Task GetVerdict_WithoutBoard_CountsTheTilesOfEveryBoard()
    {
        // Resolved question 2: no board = every board, so moving a tile to a named board never drops it from a CI check.
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(Point(Now.AddMinutes(-30), 20));
        var checkout = await SeedBoardAsync("Checkout");
        await SeedTilesAsync(
            Tile("Default errors", threshold: 100),
            Tile("Checkout errors", threshold: 10, boardId: checkout));

        var verdict = await Service().GetVerdict(Query("api"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.Fail);
        verdict.Board.ShouldBeNull();
        verdict.Checks.Select(x => (x.Name, x.State)).ShouldBe([("Default errors", "healthy"), ("Checkout errors", "breaching")], ignoreOrder: true);
    }

    [TimedTheory]
    [InlineData("Checkout", DeployVerdicts.Fail, "Checkout errors", "Checkout")]
    [InlineData("checkout", DeployVerdicts.Fail, "Checkout errors", "Checkout")]
    [InlineData("Default", DeployVerdicts.Pass, "Default errors", "Default")]
    public async Task GetVerdict_WithBoard_CountsOnlyThatBoardsTilesAndKeepsTheSlos(string board, string expected, string tile, string resolved)
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(Point(Now.AddMinutes(-30), 20));
        var checkout = await SeedBoardAsync("Checkout");
        await SeedTilesAsync(
            Tile("Default errors", threshold: 100),
            Tile("Checkout errors", threshold: 10, boardId: checkout));
        await SeedSloAsync("Api success", "api", SloState.Healthy);

        var verdict = await Service().GetVerdict(Query("api", board: board), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(expected);
        verdict.Board.ShouldBe(resolved);
        verdict.Checks.Select(x => (x.Source, x.Name)).ShouldBe([(DeployVerdictSources.Tile, tile), (DeployVerdictSources.Slo, "Api success")]);
    }

    [TimedTheory]
    [InlineData("Default")]
    [InlineData("default")]
    public async Task GetVerdict_BoardDefaultAfterTheDefaultBoardWasRenamed_IsStillTheDefaultBoard(string board)
    {
        // The seeder maps the name Default to the IsDefault board whatever it is called now; the verdict does the same
        // when no board currently has that name.
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(Point(Now.AddMinutes(-30), 20));
        await SeedAsync(new MetricBoard { Name = "Platform", IsDefault = true, CreatedAt = Now, UpdatedAt = Now });
        var checkout = await SeedBoardAsync("Checkout");
        await SeedTilesAsync(
            Tile("Default errors", threshold: 100),
            Tile("Checkout errors", threshold: 10, boardId: checkout));

        var verdict = await Service().GetVerdict(Query("api", board: board), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.Pass);
        verdict.Board.ShouldBe("Platform");
        verdict.Checks.ShouldHaveSingleItem().Name.ShouldBe("Default errors");
    }

    [TimedFact]
    public async Task GetVerdict_BoardDefaultWhileAnotherBoardIsNamedDefault_IsThatBoard()
    {
        // A current name wins: the fallback to the Default board only applies when no board is called Default.
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(Point(Now.AddMinutes(-30), 20));
        await SeedAsync(new MetricBoard { Name = "Platform", IsDefault = true, CreatedAt = Now, UpdatedAt = Now });
        var named = await SeedBoardAsync("Default");
        await SeedTilesAsync(
            Tile("Platform errors", threshold: 100),
            Tile("Named errors", threshold: 10, boardId: named));

        var verdict = await Service().GetVerdict(Query("api", board: "default"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.Fail);
        verdict.Board.ShouldBe("Default");
        verdict.Checks.ShouldHaveSingleItem().Name.ShouldBe("Named errors");
    }

    [TimedFact]
    public async Task GetVerdict_UnknownBoard_Throws()
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));

        await Should.ThrowAsync<MetricBoardNotFoundException>(() => Service().GetVerdict(Query("api", board: "Nope"), Ct));
    }

    [TimedFact]
    public async Task GetVerdict_Route_UnknownBoard_ReturnsNotFoundWithAnError()
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.GetAsync("/warp/api/applications/verdict?application=api&board=Nope", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("error").GetString().ShouldBe("No metric board is named 'Nope'.");
    }

    [TimedFact]
    public async Task GetVerdict_Route_Board_NarrowsTheTileChecks()
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(Point(Now.AddMinutes(-30), 20));
        var checkout = await SeedBoardAsync("Checkout");
        await SeedTilesAsync(
            Tile("Default errors", threshold: 10),
            Tile("Checkout errors", threshold: 100, boardId: checkout));
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.GetAsync("/warp/api/applications/verdict?application=api&board=Checkout", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("verdict").GetString().ShouldBe("pass");
        json.RootElement.GetProperty("board").GetString().ShouldBe("Checkout");
        json.RootElement.GetProperty("checks").EnumerateArray().ShouldHaveSingleItem().GetProperty("name").GetString().ShouldBe("Checkout errors");
    }

    [TimedFact]
    public async Task GetVerdict_GaugeAndLatencySlos_ReportTheShareAgainstTheRequiredShareWithTheThresholdInTheReason()
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedAsync(new SloDefinition
        {
            Name = "Heap",
            Kind = SloKind.MetricGaugeThreshold,
            Dimension = "process.heap",
            TargetValue = 600,
            Percentile = 95,
            Comparison = SloComparison.AtMost,
            GaugeAggregate = SloGaugeAggregate.Max,
            WindowSeconds = 3600,
        });
        await SeedAsync(new SloDefinition
        {
            Name = "Latency",
            Kind = SloKind.MetricLatency,
            Dimension = "http.duration",
            TargetValue = 2,
            Percentile = 95,
            WindowSeconds = 3600,
        });
        var definitions = await _fixture.CreateContext().Set<SloDefinition>()
            .AsNoTracking()
            .ToListAsync(Ct);
        foreach (var definition in definitions)
        {
            await SeedAsync(new SloEvaluation
            {
                SloDefinitionId = definition.Id,
                Attainment = string.Equals(definition.Name, "Heap", StringComparison.Ordinal) ? 0.9917 : 0.9874,
                BudgetRemaining = string.Equals(definition.Name, "Heap", StringComparison.Ordinal) ? 0.834 : 0.748,
                State = SloState.Healthy,
                LastEvaluatedAt = Now,
            });
        }

        var verdict = await Service().GetVerdict(Query("api"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.Pass);
        var heap = verdict.Checks.Single(x => string.Equals(x.Name, "Heap", StringComparison.Ordinal));
        heap.Value.ShouldBe(0.9917);
        heap.Target.ShouldBe(0.95);
        heap.Threshold.ShouldBe(600);
        heap.Reason.ShouldBe("Healthy: observed 99.17% compliant vs target max ≤ 600 in 95%, budget remaining 83%.");
        var latency = verdict.Checks.Single(x => string.Equals(x.Name, "Latency", StringComparison.Ordinal));
        latency.Value.ShouldBe(0.9874);
        latency.Target.ShouldBe(0.95);
        latency.Threshold.ShouldBe(2);
        latency.Reason.ShouldBe("Healthy: observed 98.74% ≤ 2 vs target p95 ≤ 2, budget remaining 75%.");
    }

    [TimedTheory]
    [InlineData("nope", null)]
    [InlineData("api", "9.9.9")]
    public async Task GetVerdict_UnknownApplicationOrVersion_ReturnsNull(string application, string? version)
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));

        var verdict = await Service().GetVerdict(Query(application, version), Ct);

        verdict.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetVerdict_Route_ReturnsTheVerdictAsStrings()
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(Point(Now.AddMinutes(-30), 20));
        await SeedTilesAsync(Tile("Errors", threshold: 10));
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.GetAsync("/warp/api/applications/verdict?application=api&version=2.0.0&minWindowMinutes=10", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("verdict").GetString().ShouldBe("fail");
        json.RootElement.GetProperty("deploy").GetProperty("version").GetString().ShouldBe("2.0.0");
        json.RootElement.GetProperty("window").GetProperty("minWindowSeconds").GetInt32().ShouldBe(600);
        json.RootElement.GetProperty("checksWithData").GetInt32().ShouldBe(1);
        json.RootElement.GetProperty("board").ValueKind.ShouldBe(JsonValueKind.Null);
        json.RootElement.GetProperty("checksTotal").GetInt32().ShouldBe(1);
        json.RootElement.GetProperty("summary").GetString().ShouldBe("fail (1 of 1 checks had data): 1 breaching");
        var check = json.RootElement.GetProperty("checks").EnumerateArray().ShouldHaveSingleItem();
        check.GetProperty("source").GetString().ShouldBe("tile");
        check.GetProperty("state").GetString().ShouldBe("breaching");
    }

    [TimedTheory]
    [InlineData("application=nope")]
    [InlineData("application=api&version=9.9.9")]
    [InlineData("application=api&environment=qa")]
    public async Task GetVerdict_Route_UnknownApplicationVersionOrEnvironment_ReturnsNotFound(string queryString)
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.GetAsync($"/warp/api/applications/verdict?{queryString}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [TimedTheory]
    [InlineData("")]
    [InlineData("application=")]
    [InlineData("application=api&minWindowMinutes=-1")]
    [InlineData("application=api&minWindowMinutes=99999")]
    [InlineData("application=api&minWindowMinutes=soon")]
    [InlineData("application=api&minCoverage=1.5")]
    [InlineData("application=api&minCoverage=-0.5")]
    [InlineData("application=api&minCoverage=most")]
    [InlineData("application=api&board=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task GetVerdict_Route_BadParameters_ReturnsBadRequest(string queryString)
    {
        await using var host = await CreateDashboardHostAsync();

        var response = await host.Client.GetAsync($"/warp/api/applications/verdict?{queryString}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TimedFact]
    public async Task DashboardOnlyHost_AddWarpAlone_ServesTheVerdict()
    {
        // No server, no AddMetrics, no AddSlo: AddWarp registers the verdict and every read service it composes.
        await SeedDeployAsync("api", "2.0.0", Now.AddHours(-1));
        await SeedMetricsAsync(Point(Now.AddMinutes(-30), 4));
        await SeedTilesAsync(Tile("Errors", threshold: 10));
        await SeedSloAsync("Success", "api", SloState.Healthy);
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
        builder.Services.AddWarp<TestContext>(_ => { });
        await using var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(Ct);
        try
        {
            using var client = app.GetTestClient();

            var response = await client.GetAsync("/warp/api/applications/verdict?application=api", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            json.RootElement.GetProperty("verdict").GetString().ShouldBe("pass");
            json.RootElement.GetProperty("checks").GetArrayLength().ShouldBe(2);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static DeployVerdictQuery Query(string application, string? version = null, string? environment = null, double minCoverage = 0, string? board = null) => new()
    {
        Application = application,
        Board = board,
        Version = version,
        Environment = environment,
        MinCoverage = minCoverage,
    };

    private static MetricBoardTile Tile(string name, double? threshold, string? application = null, int position = 1, int? boardId = null) => new()
    {
        BoardId = boardId,
        Name = name,
        Instrument = "c",
        Position = position,
        Application = application,
        Kind = MetricTileKind.Value,
        StatusRule = threshold is null ? MetricTileStatusRule.None : MetricTileStatusRule.Threshold,
        Comparison = threshold is null ? null : SloComparison.AtMost,
        Threshold = threshold,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static MetricPoint Point(DateTime bucket, double sum, string? application = null) => new()
    {
        Instrument = "c",
        Application = application,
        Tags = "-",
        Tier = MetricTier.Fine,
        BucketStart = bucket,
        Count = 1,
        Sum = sum,
    };

    private DeployVerdictService Service()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(Now));
        var options = Options.Create(new WarpConfiguration());

        return new DeployVerdictService(
            new ApplicationQueryService<TestContext>(_fixture.CreateContext(), time, options),
            new MetricBoardQueryService<TestContext>(_fixture.CreateContext(), options, time),
            new SloQueryService<TestContext>(_fixture.CreateContext()),
            time,
            options);
    }

    private async Task SeedDeployAsync(string application, string version, DateTime firstSeenAt, string environment = "")
        => await SeedAsync(new ApplicationDeploy
        {
            Application = application,
            Version = version,
            Environment = environment,
            FirstSeenAt = firstSeenAt,
            InstanceId = Guid.NewGuid(),
            InstanceName = "seed",
        });

    private async Task SeedMetricsAsync(params MetricPoint[] points)
    {
        var context = _fixture.CreateContext();
        context.Set<MetricInstrument>().Add(new MetricInstrument
        {
            Name = "c",
            Meter = "test",
            Kind = MetricKind.Counter,
            FirstSeenAt = Now.AddDays(-10),
            LastSeenAt = Now,
        });
        context.Set<MetricPoint>().AddRange(points);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<int> SeedBoardAsync(string name)
    {
        var board = new MetricBoard { Name = name, Position = 1, CreatedAt = Now, UpdatedAt = Now };
        await SeedAsync(board);

        return board.Id;
    }

    private async Task SeedTilesAsync(params MetricBoardTile[] tiles)
    {
        var context = _fixture.CreateContext();
        context.Set<MetricBoardTile>().AddRange(tiles);
        await context.SaveChangesAsync(Ct);
    }

    private async Task SeedSloAsync(string name, string? application, SloState state, DateTime? evaluatedAt = default)
    {
        var definition = new SloDefinition
        {
            Name = name,
            Kind = SloKind.SuccessRate,
            Application = application,
            TargetValue = 0.99,
            WindowSeconds = 3600,
        };
        await SeedAsync(definition);
        await SeedAsync(new SloEvaluation
        {
            SloDefinitionId = definition.Id,
            Attainment = 0.995,
            BudgetRemaining = 0.5,
            State = state,
            LastEvaluatedAt = evaluatedAt ?? Now,
        });
    }

    private async Task SeedAsync<T>(T entity)
        where T : class
    {
        var context = _fixture.CreateContext();
        context.Set<T>().Add(entity);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<DashboardHost> CreateDashboardHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        builder.Services.AddScoped<IDeployVerdictService>(_ => Service());

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
