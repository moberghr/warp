using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Queries;
using Warp.Core.Handlers;
using Warp.Core.Notifications;
using Warp.Core.Services;
using Warp.Tests.TestData.BackgroundServices;
using Warp.UI.Endpoints;
using Warp.UI.UIMiddleware;
using Warp.Worker;
using Warp.Worker.BackgroundServices;
using Warp.Worker.Services;

namespace Warp.Tests.Admin;

/// <summary>
/// Smoke tests that pin the DI wiring of each supported deployment shape. They catch
/// "service X was registered in the wrong layer Y" bugs — the kind that previously broke
/// dashboard-only deployments when <c>IBackgroundServiceQueryService</c> was mis-registered
/// into <c>AddWarpServer</c>. Cheaper than per-service registration tests and stronger
/// because the assertions run against the real service collection produced by
/// <c>AddWarp</c> / <c>AddWarpServer</c>.
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
    // Registers the minimum scaffolding any AddWarp/AddWarpServer call needs. Provider
    // packages contribute these in production via UsePostgreSql() / UseSqlServer(); for a
    // NoDb smoke test we substitute Mock.Of<>. IWarpLockProvider is required even by
    // AddWarp-only deployments because IRecurringJobPublisher depends on it.
    private static void RegisterMinimalDependencies(IServiceCollection services)
    {
        services.AddLogging();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"shape-{Guid.NewGuid():N}"));
        services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());

        // Provider packages register IWarpServerContextConfigurator in production (UsePostgreSql /
        // UseSqlServer). For a NoDb shape test, point the server context at InMemory so AddWarpServer's
        // WarpServerContext registration can build.
        services.AddSingleton<IWarpServerContextConfigurator>(new InMemoryServerContextConfigurator());
    }

    private sealed class InMemoryServerContextConfigurator : IWarpServerContextConfigurator
    {
        private readonly string _database = $"shape-server-{Guid.NewGuid():N}";

        public void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider applicationServices)
        {
            optionsBuilder.UseInMemoryDatabase(_database);
        }
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
    public void WorkerAndDashboardShape_AddWarpAndAddWarpServer_PublicApiResolves()
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);
        services.AddWarpServer<TestContext>();

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

    // Pins the full server shape (worker on by default): AddWarpServer registers the worker hosts,
    // the six job-only server tasks, AND the server infrastructure + background-service host.
    [TimedFact]
    public void ServerWithWorkerShape_AddWarpServer_RegistersWorkerAndBgServices()
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);

        // CountingService depends on CountingServiceState; ExpirationCleanup injects
        // IEnumerable<WarpBackgroundService>, so resolving IServerTask below materialises it.
        services.AddSingleton<CountingServiceState>();
        services.AddWarpServer<TestContext>(opt => opt.AddBackgroundService<CountingService>());

        // Server infra + background-service host.
        HasHostedService<WarpServerRegistration<TestContext>>(services).ShouldBeTrue();
        HasHostedService<ServerTaskHost<TestContext>>(services).ShouldBeTrue();
        HasHostedService<BackgroundServiceHost<TestContext>>(services).ShouldBeTrue();

        // Worker hosts run when the worker is enabled (the default).
        HasHostedService<WarpDispatcherHost<TestContext>>(services).ShouldBeTrue(
            "the job worker runs by default — WarpDispatcherHost must be registered.");
        HasHostedService<WarpSingleWorkerHost<TestContext>>(services).ShouldBeTrue(
            "the job worker runs by default — WarpSingleWorkerHost must be registered.");

        services.Any(d => d.ServiceType == typeof(WarpBackgroundService)).ShouldBeTrue(
            "AddBackgroundService<T>() should register the WarpBackgroundService discovery alias.");

        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();

        var taskTypes = scope.ServiceProvider.GetServices<IServerTask>().Select(x => x.GetType()).ToList();
        taskTypes.ShouldContain(typeof(Orchestrator<TestContext>));
        taskTypes.ShouldContain(typeof(MessageRouter<TestContext>));
    }

    // The service-only shape: opt.DisableWorker() leaves the server infrastructure + background
    // services but NO job worker hosts and NONE of the six job-only server tasks.
    [TimedFact]
    public void ServiceOnlyShape_AddWarpServerDisableWorker_OmitsWorker()
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);
        services.AddWarpServer<TestContext>(opt => opt.DisableWorker());

        // Server infra + background-service host still present.
        HasHostedService<WarpServerRegistration<TestContext>>(services).ShouldBeTrue();
        HasHostedService<ServerTaskHost<TestContext>>(services).ShouldBeTrue();
        HasHostedService<BackgroundServiceHost<TestContext>>(services).ShouldBeTrue();

        // Worker hosts must NOT be registered.
        HasHostedService<WarpDispatcherHost<TestContext>>(services).ShouldBeFalse(
            "WarpDispatcherHost must NOT be registered when the worker is disabled.");
        HasHostedService<WarpSingleWorkerHost<TestContext>>(services).ShouldBeFalse(
            "WarpSingleWorkerHost must NOT be registered when the worker is disabled.");

        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();

        ResolvesCoreApi(scope.ServiceProvider);
        scope.ServiceProvider.GetRequiredService<IBackgroundServiceStateService>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IBackgroundServiceLeaseCoordinator>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IBackgroundServiceLogStore>().ShouldNotBeNull();

        // DispatcherRegistry must resolve even with the worker disabled: UseDatabasePush() hosts
        // NotificationListenerTask, which injects it, on a service-only server too.
        scope.ServiceProvider.GetRequiredService<DispatcherRegistry>().ShouldNotBeNull();

        var taskTypes = scope.ServiceProvider.GetServices<IServerTask>().Select(x => x.GetType()).ToList();

        // The three server-infrastructure tasks run.
        taskTypes.ShouldContain(typeof(Heartbeat<TestContext>));
        taskTypes.ShouldContain(typeof(ServerCleanup<TestContext>));
        taskTypes.ShouldContain(typeof(ExpirationCleanup<TestContext>));

        // None of the six job-only tasks.
        taskTypes.ShouldNotContain(typeof(Orchestrator<TestContext>));
        taskTypes.ShouldNotContain(typeof(MessageRouter<TestContext>));
        taskTypes.ShouldNotContain(typeof(ScheduledJobActivation<TestContext>));
        taskTypes.ShouldNotContain(typeof(RecurringJobScheduler<TestContext>));
        taskTypes.ShouldNotContain(typeof(StaleJobRecovery<TestContext>));
        taskTypes.ShouldNotContain(typeof(CounterAggregator<TestContext>));
    }

    // Regression guard: a service-only server with UseDatabasePush() still hosts
    // NotificationListenerTask, which injects DispatcherRegistry. DispatcherRegistry must be
    // registered for every server (not gated behind the worker), or this graph fails to construct
    // at startup. Materializing the hosted services forces NotificationListenerTask construction.
    [TimedFact]
    public void ServiceOnlyShape_WithDatabasePush_NotificationListenerResolves()
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);

        // UseDatabasePush fails fast unless a provider registered a transport factory; a real
        // provider isn't needed for this DI-shape check, so substitute a mock.
        services.AddSingleton(Mock.Of<IWarpNotificationTransportFactory>());

        services.AddWarpServer<TestContext>(opt =>
        {
            opt.DisableWorker();
            opt.UseDatabasePush();
        });

        // Override the provider transport (whose factory path needs a real connection string) with
        // a stub so NotificationListenerTask can be constructed in this NoDb test. Last registration wins.
        services.AddSingleton(Mock.Of<IWarpNotificationTransport>());

        HasHostedService<NotificationListenerTask<TestContext>>(services).ShouldBeTrue(
            "UseDatabasePush must register the notification listener on a service-only server.");

        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Forces construction of every hosted service — including NotificationListenerTask, which
        // throws here if DispatcherRegistry isn't resolvable on a service-only server.
        var hosted = sp.GetServices<IHostedService>().ToList();
        hosted.OfType<NotificationListenerTask<TestContext>>().Any().ShouldBeTrue();
    }

    // The contradictory "run the worker, but with zero workers" shape must fail fast at
    // registration rather than silently produce a server that orchestrates jobs but never executes
    // them.
    [TimedFact]
    public void AddWarpServer_RunWorkerWithZeroWorkers_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);

        Should.Throw<InvalidOperationException>(() =>
            services.AddWarpServer<TestContext>(opt => opt.WorkerCount = 0));

        // DisableWorker() is the supported way to express "no worker" and must NOT throw.
        var ok = new ServiceCollection();
        RegisterMinimalDependencies(ok);
        Should.NotThrow(() => ok.AddWarpServer<TestContext>(opt => opt.DisableWorker()));
    }

    private static bool HasHostedService<T>(IServiceCollection services)
        where T : IHostedService
        => services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T));

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
