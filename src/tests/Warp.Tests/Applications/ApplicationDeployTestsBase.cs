using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Diagnostics;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Applications;

/// <summary>
/// Deploy history (#325, §8.23): a deploy is the first instance of an application to register with a version not seen
/// before for that application and environment. Covers the insert-if-absent recorder (including the N=2 race on the
/// unique index, pinned with <see cref="BarrierSignal"/>), both registration paths (server + non-server host), the
/// separate retention that keeps the newest deploy and is untouched by the instance sweeps, the read API and its routes.
/// </summary>
[GenerateDatabaseTests]
public abstract class ApplicationDeployTestsBase : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly IDatabaseFixture _fixture;

    protected ApplicationDeployTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task RecordAsync_NewVersion_InsertsTheDeploy()
    {
        var instanceId = Guid.NewGuid();

        var inserted = await ApplicationDeployRecorder.RecordAsync(
            _fixture.CreateContext(), Config("api", "2.0.0", "prod"), instanceId, "host-1", Now, Ct);

        inserted.ShouldBeTrue();
        var deploy = await _fixture.CreateContext().Set<ApplicationDeploy>().SingleAsync(Ct);
        deploy.Application.ShouldBe("api");
        deploy.Version.ShouldBe("2.0.0");
        deploy.Environment.ShouldBe("prod");
        deploy.FirstSeenAt.ShouldBe(Now);
        deploy.InstanceId.ShouldBe(instanceId);
        deploy.InstanceName.ShouldBe("host-1");
    }

    [TimedFact]
    public async Task RecordAsync_VersionAlreadySeenWithoutEnvironment_KeepsTheFirstRecord()
    {
        // No environment is stored as the empty sentinel, so the unique key still matches the earlier record.
        await SeedDeployAsync("api", "2.0.0", string.Empty, Now.AddHours(-1));

        var inserted = await ApplicationDeployRecorder.RecordAsync(
            _fixture.CreateContext(), Config("api", "2.0.0", environment: null), Guid.NewGuid(), "host-2", Now, Ct);

        inserted.ShouldBeFalse();
        var deploy = await _fixture.CreateContext().Set<ApplicationDeploy>().SingleAsync(Ct);
        deploy.FirstSeenAt.ShouldBe(Now.AddHours(-1));
    }

    [TimedFact]
    public async Task RecordAsync_SameVersionInAnotherEnvironment_IsItsOwnDeploy()
    {
        await SeedDeployAsync("api", "2.0.0", "staging", Now.AddDays(-1));

        var inserted = await ApplicationDeployRecorder.RecordAsync(
            _fixture.CreateContext(), Config("api", "2.0.0", "prod"), Guid.NewGuid(), "host-1", Now, Ct);

        inserted.ShouldBeTrue();
        (await _fixture.CreateContext().Set<ApplicationDeploy>().CountAsync(Ct)).ShouldBe(2);
    }

    [TimedTheory]
    [InlineData(null, "1.0.0")]
    [InlineData("api", null)]
    [InlineData("api", " ")]
    public async Task RecordAsync_NoApplicationOrNoVersion_WritesNothing(string? application, string? version)
    {
        var inserted = await ApplicationDeployRecorder.RecordAsync(
            _fixture.CreateContext(), Config(application, version, "prod"), Guid.NewGuid(), "host-1", Now, Ct);

        inserted.ShouldBeFalse();
        (await _fixture.CreateContext().Set<ApplicationDeploy>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task RecordAsync_TwoInstancesOfANewVersionRegisterAtOnce_RecordsExactlyOneDeploy()
    {
        // Both registrations pass the existence probe before either inserts (pinned on the barrier, N=2), so the
        // unique index decides: one insert wins, the other's duplicate key is read as "already recorded".
        var barrier = new BarrierSignal();
        var configuration = Config("api", "3.0.0", "prod");

        async Task Pin(CancellationToken ct)
        {
            barrier.Running.Release();
            await barrier.CanFinish.WaitAsync(ct);
        }

        var first = ApplicationDeployRecorder.RecordAsync(_fixture.CreateContext(), configuration, Guid.NewGuid(), "host-1", Now, Ct, Pin);
        var second = ApplicationDeployRecorder.RecordAsync(_fixture.CreateContext(), configuration, Guid.NewGuid(), "host-2", Now, Ct, Pin);

        await barrier.Running.WaitAsync(Ct);
        await barrier.Running.WaitAsync(Ct);
        barrier.CanFinish.Release(2);

        var results = await Task.WhenAll(first, second);

        results.Count(x => x).ShouldBe(1);
        (await _fixture.CreateContext().Set<ApplicationDeploy>().CountAsync(Ct)).ShouldBe(1);
    }

    [TimedFact]
    public async Task ServerRegistration_FirstServerOfAVersion_RecordsTheDeployAgainstTheServer()
    {
        var serverId = Guid.NewGuid();
        var registration = CreateServerRegistration(serverId, "api", "2.0.0", "prod");

        await registration.StartAsync(Ct);

        var deploy = await _fixture.CreateContext().Set<ApplicationDeploy>().SingleAsync(Ct);
        deploy.Application.ShouldBe("api");
        deploy.Version.ShouldBe("2.0.0");
        deploy.InstanceId.ShouldBe(serverId);
        (await _fixture.CreateContext().Set<Server>().AnyAsync(x => x.Id == serverId, Ct)).ShouldBeTrue();
    }

    [TimedFact]
    public async Task ServerRegistration_WithoutApplicationName_RecordsNoDeploy()
    {
        var registration = CreateServerRegistration(Guid.NewGuid(), application: null, version: "2.0.0", environment: null);

        await registration.StartAsync(Ct);

        (await _fixture.CreateContext().Set<ApplicationDeploy>().AnyAsync(Ct)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task HeartbeatHost_StartAsync_FirstInstanceOfAVersion_RecordsTheDeploy()
    {
        var host = CreateHost("publisher", "1.4.0", "prod");

        await host.StartAsync(Ct);
        try
        {
            var instance = await _fixture.CreateContext().Set<ApplicationInstance>().SingleAsync(Ct);
            var deploy = await _fixture.CreateContext().Set<ApplicationDeploy>().SingleAsync(Ct);
            deploy.Application.ShouldBe("publisher");
            deploy.Version.ShouldBe("1.4.0");
            deploy.Environment.ShouldBe("prod");
            deploy.InstanceId.ShouldBe(instance.Id);
        }
        finally
        {
            await host.StopAsync(Ct);
        }
    }

    [TimedFact]
    public async Task StaleInstanceSweep_DoesNotTouchDeployHistory()
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<ApplicationInstance>().Add(new ApplicationInstance
        {
            Id = Guid.NewGuid(),
            ApplicationName = "api",
            MachineName = "dead-host",
            StartedAt = Now.AddHours(-1),
            LastHeartbeatAt = Now.AddMinutes(-30),
        });
        await ctx.SaveChangesAsync(Ct);
        await SeedDeployAsync("api", "1.0.0", string.Empty, Now.AddHours(-1));

        var swept = await CreateCleanup(retention: null).CleanupStaleApplicationInstancesAsync(Ct);

        swept.ShouldBe(1);
        (await _fixture.CreateContext().Set<ApplicationDeploy>().CountAsync(Ct)).ShouldBe(1);
    }

    [TimedFact]
    public async Task CleanupExpiredApplicationDeploys_NoRetention_KeepsEverything()
    {
        await SeedDeployAsync("api", "1.0.0", string.Empty, Now.AddDays(-400));
        await SeedDeployAsync("api", "2.0.0", string.Empty, Now.AddDays(-300));

        var deleted = await CreateCleanup(retention: null).CleanupExpiredApplicationDeploysAsync(Ct);

        deleted.ShouldBe(0);
        (await _fixture.CreateContext().Set<ApplicationDeploy>().CountAsync(Ct)).ShouldBe(2);
    }

    [TimedFact]
    public async Task CleanupExpiredApplicationDeploys_PastRetention_DeletesOldDeploysButKeepsTheNewestPerApplication()
    {
        await SeedDeployAsync("api", "1.0.0", "prod", Now.AddDays(-40));
        await SeedDeployAsync("api", "2.0.0", "prod", Now.AddDays(-35));
        await SeedDeployAsync("api", "3.0.0", "prod", Now.AddDays(-1));
        await SeedDeployAsync("worker", "9.0.0", "prod", Now.AddDays(-50));
        await SeedDeployAsync("api", "1.0.0", "staging", Now.AddDays(-45));

        var deleted = await CreateCleanup(retention: TimeSpan.FromDays(30)).CleanupExpiredApplicationDeploysAsync(Ct);

        deleted.ShouldBe(2);
        var remaining = await _fixture.CreateContext().Set<ApplicationDeploy>()
            .OrderBy(x => x.Application)
            .ThenBy(x => x.Environment)
            .Select(x => x.Application + "/" + x.Environment + "/" + x.Version)
            .ToListAsync(Ct);
        remaining.ShouldBe(["api/prod/3.0.0", "api/staging/1.0.0", "worker/prod/9.0.0"]);
    }

    [TimedFact]
    public async Task GetDeploys_ApplicationAndRange_ReturnsMatchingDeploysNewestFirst()
    {
        await SeedDeployAsync("api", "1.0.0", string.Empty, Now.AddDays(-3));
        await SeedDeployAsync("api", "2.0.0", "prod", Now.AddDays(-2));
        await SeedDeployAsync("api", "3.0.0", "prod", Now.AddHours(-1));
        await SeedDeployAsync("worker", "5.0.0", "prod", Now.AddDays(-2));

        var result = await CreateService().GetDeploys("api", Now.AddDays(-3), Now.AddHours(-1), Ct);

        result.Select(x => x.Version).ShouldBe(["2.0.0", "1.0.0"]);
        result[0].Environment.ShouldBe("prod");
        result[1].Environment.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetDeploys_NoApplicationNoRange_ReturnsEveryApplication()
    {
        await SeedDeployAsync("api", "1.0.0", string.Empty, Now.AddDays(-3));
        await SeedDeployAsync("worker", "5.0.0", string.Empty, Now.AddDays(-2));

        var result = await CreateService().GetDeploys(null, null, null, Ct);

        result.Select(x => x.Application).ShouldBe(["worker", "api"]);
    }

    [TimedFact]
    public async Task GetApplicationDeploys_Route_ReturnsTheApplicationsDeploys()
    {
        await SeedDeployAsync("api", "1.0.0", string.Empty, Now.AddDays(-3));
        await SeedDeployAsync("worker", "5.0.0", string.Empty, Now.AddDays(-2));
        var (app, client) = await CreateDashboardHost();
        try
        {
            var response = await client.GetAsync($"/warp/api/applications/{UrlSafeId.Encode("api")}/deploys", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            json.RootElement.GetArrayLength().ShouldBe(1);
            json.RootElement[0].GetProperty("version").GetString().ShouldBe("1.0.0");
        }
        finally
        {
            await DisposeHostAsync(app, client);
        }
    }

    [TimedFact]
    public async Task GetDeploys_Route_FiltersByApplicationNameAndRange()
    {
        await SeedDeployAsync("api", "1.0.0", string.Empty, Now.AddDays(-3));
        await SeedDeployAsync("api", "2.0.0", string.Empty, Now.AddHours(-2));
        await SeedDeployAsync("worker", "5.0.0", string.Empty, Now.AddHours(-2));
        var (app, client) = await CreateDashboardHost();
        try
        {
            var from = Uri.EscapeDataString(Now.AddDays(-1).ToString("O"));
            var to = Uri.EscapeDataString(Now.ToString("O"));

            var response = await client.GetAsync($"/warp/api/applications/deploys?application=api&from={from}&to={to}", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            json.RootElement.GetArrayLength().ShouldBe(1);
            json.RootElement[0].GetProperty("version").GetString().ShouldBe("2.0.0");
        }
        finally
        {
            await DisposeHostAsync(app, client);
        }
    }

    [TimedFact]
    public async Task GetDeploys_Route_FromNotBeforeTo_ReturnsBadRequest()
    {
        var (app, client) = await CreateDashboardHost();
        try
        {
            var instant = Uri.EscapeDataString(Now.ToString("O"));

            var response = await client.GetAsync($"/warp/api/applications/deploys?from={instant}&to={instant}", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            await DisposeHostAsync(app, client);
        }
    }

    private static WarpConfiguration Config(string? application, string? version, string? environment) => new()
    {
        ApplicationName = application,
        ApplicationVersion = version,
        ApplicationEnvironment = environment,
    };

    private static async Task DisposeHostAsync(WebApplication app, HttpClient client)
    {
        client.Dispose();
        await app.DisposeAsync();
    }

    private ApplicationQueryService<TestContext> CreateService()
        => new(_fixture.CreateContext(), new FakeTimeProvider(new DateTimeOffset(Now)), Options.Create(new WarpConfiguration()));

    private async Task SeedDeployAsync(string application, string version, string environment, DateTime firstSeenAt)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<ApplicationDeploy>().Add(new ApplicationDeploy
        {
            Application = application,
            Version = version,
            Environment = environment,
            FirstSeenAt = firstSeenAt,
            InstanceId = Guid.NewGuid(),
            InstanceName = "seed",
        });

        await ctx.SaveChangesAsync(Ct);
    }

    private ExpirationCleanup<TestContext> CreateCleanup(TimeSpan? retention)
    {
        var configuration = new WarpServerConfiguration
        {
            ApplicationInstanceStaleGrace = TimeSpan.FromMinutes(2),
            ApplicationDeployRetention = retention,
        };

        return new ExpirationCleanup<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            new FakeTimeProvider(new DateTimeOffset(Now)),
            Options.Create(configuration),
            TestNotifiers.EmptyDispatcher());
    }

    private WarpServerRegistration<TestContext> CreateServerRegistration(
        Guid serverId,
        string? application,
        string? version,
        string? environment)
    {
        var configuration = new WarpServerConfiguration
        {
            ServerId = serverId,
            WorkerCount = 1,
            ApplicationName = application,
            ApplicationVersion = version,
            ApplicationEnvironment = environment,
        };

        var services = new ServiceCollection();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new WarpServerRegistration<TestContext>(
            Options.Create(configuration),
            Options.Create<WarpConfiguration>(configuration),
            scopeFactory,
            TimeProvider.System,
            new PauseStateHolder(),
            new ServerRegistrationState());
    }

    private ApplicationHeartbeatHost<TestContext> CreateHost(string applicationName, string version, string environment)
    {
        var services = new ServiceCollection();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var configuration = Config(applicationName, version, environment);
        configuration.ApplicationHeartbeatInterval = TimeSpan.FromHours(1);

        return new ApplicationHeartbeatHost<TestContext>(
            scopeFactory,
            Options.Create(configuration),
            TimeProvider.System,
            new ProcessCpuTracker(TimeProvider.System),
            NullLogger<ApplicationHeartbeatHost<TestContext>>.Instance,
            []);
    }

    private async Task<(WebApplication App, HttpClient Client)> CreateDashboardHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        builder.Services.AddScoped<IApplicationQueryService>(_ => CreateService());

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);

        await app.StartAsync(CancellationToken.None);

        return (app, app.GetTestClient());
    }
}
