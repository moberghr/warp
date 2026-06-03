using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Queries;
using Warp.Core.Handlers;
using Warp.Core.Notifications;
using Warp.Core.Services;
using Warp.UI.Endpoints;
using Warp.UI.UIMiddleware;
using Warp.Worker;
using Warp.Worker.BackgroundServices;

namespace Warp.Tests.Admin;

/// <summary>
/// Smoke tests that pin the DI wiring of each supported deployment shape. They catch
/// "service X was registered in the wrong layer Y" bugs — the kind that previously broke
/// dashboard-only deployments when <c>IBackgroundServiceQueryService</c> was mis-registered
/// into <c>AddWarpWorker</c>. Cheaper than per-service registration tests and stronger
/// because the assertions run against the real service collection produced by
/// <c>AddWarp</c> / <c>AddWarpWorker</c>.
/// <para>
/// Implementation note: we deliberately avoid <c>ValidateOnBuild = true</c>. The Warp
/// source generator auto-registers every <c>IJobHandler</c> / <c>IMessageHandler</c> in
/// referenced assemblies, which pulls test-only handlers (<c>BarrierCommand</c>,
/// <c>CounterCommand</c>, …) into the graph. Their constructor deps
/// (<c>BarrierSignal</c>, <c>CounterService</c>) are registered lazily by integration
/// fixtures — failing a global graph validation here would be a false positive. Instead
/// we resolve the specific services this shape promises to expose.
/// </para>
/// </summary>
[Trait("Category", "NoDb")]
public class DeploymentShapeTests
{
    // Registers the minimum scaffolding any AddWarp/AddWarpWorker call needs. Provider
    // packages contribute these in production via UsePostgreSql() / UseSqlServer(); for a
    // NoDb smoke test we substitute Mock.Of<>. IWarpLockProvider is required even by
    // AddWarp-only deployments because IRecurringJobPublisher depends on it.
    private static void RegisterMinimalDependencies(IServiceCollection services)
    {
        services.AddLogging();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"shape-{Guid.NewGuid():N}"));
        services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());
    }

    // Pins the dashboard-only / publisher-only path: AddWarp<TContext> alone must build a
    // valid DI graph and the BG-services read endpoints must resolve their dependencies
    // (not 500 with "Unable to resolve service"). Regression guard for the
    // "IBackgroundServiceQueryService was registered in the wrong layer" bug.
    [TimedFact]
    public async Task DashboardOnlyShape_AddWarpAlone_ResolvesAndServesEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        RegisterMinimalDependencies(builder.Services);
        builder.Services.AddWarp<TestContext>();

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpUIOptions(), []);

        await app.StartAsync(CancellationToken.None);
        var client = app.GetTestClient();

        try
        {
            // Empty in-memory DB returns 200 with empty payload — that's fine and is the
            // signal we want. ShouldBe(OK) (not ShouldNotBe(500)) because ASP.NET surfaces a
            // missing required [FromServices] parameter as 400 in some pipelines and 500 in
            // others — strict 200 makes the test catch both.
            foreach (var path in new[]
            {
                "/warp/api/addons",
                "/warp/api/services",
            })
            {
                var response = await client.GetAsync(path, CancellationToken.None);
                response.StatusCode.ShouldBe(
                    HttpStatusCode.OK,
                    $"GET {path} did not return 200 — indicates a missing DI registration or other wiring break.");
            }
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    // Pins the combined worker + dashboard shape. Resolves every public-API service the
    // shape promises so any future drift in layer assignment surfaces here.
    [TimedFact]
    public void WorkerAndDashboardShape_AddWarpAndAddWarpWorker_PublicApiResolves()
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);
        services.AddWarpWorker<TestContext>();

        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();

        // Core (AddWarp) services.
        ResolvesCoreApi(scope.ServiceProvider);

        // Worker-side services. These would fail in a Dashboard-only deployment but must
        // resolve here.
        scope.ServiceProvider.GetRequiredService<IBackgroundServiceStateService>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IBackgroundServiceLeaseCoordinator>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IBackgroundServiceLogStore>().ShouldNotBeNull();
    }

    // Pins the publisher-only shape — the application calls AddWarp to publish jobs but
    // doesn't host the dashboard. Same DI surface as Dashboard-only; kept as a distinct
    // test so a future drift that adds an endpoint-only dependency into AddWarp doesn't
    // pass silently.
    [TimedFact]
    public void PublisherOnlyShape_AddWarpAlone_PublicApiResolves()
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);
        services.AddWarp<TestContext>();

        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();

        ResolvesCoreApi(scope.ServiceProvider);

        // Worker-side services must NOT be registered here — if they leak out of AddWarp
        // into Core, dashboard-only deployments would start failing later (the production
        // worker-fetch loop would try to instantiate handlers it has no business owning).
        // Use GetService (nullable) for the negative assertions.
        scope.ServiceProvider.GetService<IBackgroundServiceStateService>().ShouldBeNull(
            "IBackgroundServiceStateService leaked into AddWarp — it's worker-only.");
        scope.ServiceProvider.GetService<IBackgroundServiceLeaseCoordinator>().ShouldBeNull(
            "IBackgroundServiceLeaseCoordinator leaked into AddWarp — it's worker-only.");
    }

    // Asserts the contract that AddWarp<TContext> alone must satisfy: the read + publish
    // surface every Warp-using process (worker, dashboard, publisher-only) depends on.
    private static void ResolvesCoreApi(IServiceProvider scoped)
    {
        scoped.GetRequiredService<TestContext>().ShouldNotBeNull();
        scoped.GetRequiredService<IPublisher>().ShouldNotBeNull();
        scoped.GetRequiredService<IBatchPublisher>().ShouldNotBeNull();
        scoped.GetRequiredService<IMediator>().ShouldNotBeNull();
        scoped.GetRequiredService<IJobCommandService>().ShouldNotBeNull();
        scoped.GetRequiredService<IJobQueryService>().ShouldNotBeNull();
        scoped.GetRequiredService<IJobGroupQueryService>().ShouldNotBeNull();
        scoped.GetRequiredService<IRecurringJobService>().ShouldNotBeNull();
        scoped.GetRequiredService<IRecurringJobPublisher>().ShouldNotBeNull();
        scoped.GetRequiredService<IDashboardStatsService>().ShouldNotBeNull();
        scoped.GetRequiredService<IServerCommandService>().ShouldNotBeNull();
        scoped.GetRequiredService<IBackgroundServiceQueryService>().ShouldNotBeNull();
        scoped.GetRequiredService<IJobContext>().ShouldNotBeNull();
        scoped.GetRequiredService<IWarpNotificationTransport>().ShouldNotBeNull();
        scoped.GetRequiredService<TimeProvider>().ShouldNotBeNull();
    }
}
