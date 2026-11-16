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
        verdict.Window.To.ShouldBe(Now);
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
    public async Task GetVerdict_DeployYoungerThanTheSettleWindow_IsPendingEvenWhenBreaching()
    {
        await SeedDeployAsync("api", "2.0.0", Now.AddMinutes(-3));
        await SeedMetricsAsync(Point(Now.AddMinutes(-5), 100));
        await SeedTilesAsync(Tile("Errors", threshold: 10));

        var verdict = await Service().GetVerdict(Query("api"), Ct);

        verdict.ShouldNotBeNull();
        verdict.Verdict.ShouldBe(DeployVerdicts.Pending);
        verdict.Window.Settled.ShouldBeFalse();
        verdict.Window.SettlesAt.ShouldBe(Now.AddMinutes(2));
        verdict.Checks.ShouldHaveSingleItem().State.ShouldBe("breaching");
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

    private static DeployVerdictQuery Query(string application, string? version = null, string? environment = null) => new()
    {
        Application = application,
        Version = version,
        Environment = environment,
    };

    private static MetricBoardTile Tile(string name, double? threshold, string? application = null, int position = 1) => new()
    {
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
            time);
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

    private async Task SeedTilesAsync(params MetricBoardTile[] tiles)
    {
        var context = _fixture.CreateContext();
        context.Set<MetricBoardTile>().AddRange(tiles);
        await context.SaveChangesAsync(Ct);
    }

    private async Task SeedSloAsync(string name, string? application, SloState state)
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
            LastEvaluatedAt = Now,
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
