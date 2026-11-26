using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.BackgroundServices;
using Warp.Core.CircuitBreaker;
using Warp.Core.ClientObservability;
using Warp.Core.Concurrency;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Metrics;
using Warp.Core.NoRestart;
using Warp.Core.Observability;
using Warp.Core.RateLimit;
using Warp.Core.Retry;
using Warp.Core.Sagas;
using Warp.Core.Slo;
using Warp.Core.Testing;
using Warp.Core.Timeout;
using Warp.Dashboard;
using Warp.Dashboard.Push;
using Warp.Http.Observability;
using Warp.Provider.PostgreSql;
using Warp.Tests.TestData.BackgroundServices;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Testing;

/// <summary>
/// <c>RemoveWarpHostedServices()</c>, for an application's tests that boot the app without exercising Warp. The
/// guard configures every addon that registers a hosted service and checks by assembly NAME, independently of
/// the assembly-metadata rule the implementation uses, so a new Warp hosted service the rule would miss fails here.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class RemoveWarpHostedServicesTests
{
    [TimedFact]
    public void RemoveWarpHostedServices_EveryAddonConfigured_LeavesNoWarpHostedService()
    {
        var services = FullyConfiguredServices();

        services.RemoveWarpHostedServices();

        services
            .Where(x => x.ServiceType == typeof(IHostedService))
            .Select(x => $"{ImplementationOf(x)?.FullName} ({ImplementationOf(x)?.Assembly.GetName().Name})")
            .Where(x => x.Contains("(Warp.", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    [TimedFact]
    public void RemoveWarpHostedServices_EveryAddonConfigured_StartsFromEveryRegistrationShape()
    {
        // Keeps the guard above honest: it must start from hosted services of every package and of both
        // registration shapes (a type, and a factory: dashboard push hosts its broadcaster through one).
        var hosted = FullyConfiguredServices()
            .Where(x => x.ServiceType == typeof(IHostedService))
            .ToList();

        hosted
            .Select(x => ImplementationOf(x)?.Assembly.GetName().Name)
            .Distinct()
            .ShouldBe(["Warp.Core", "Warp.Worker", "Warp.Provider.PostgreSql", "Warp.Dashboard"], ignoreOrder: true);
        hosted.ShouldContain(x => x.ImplementationFactory != null);
    }

    [TimedFact]
    public void WarpPackages_EveryAssemblyWithAHostedService_CarriesTheMarker()
    {
        var unmarked = WarpPackageAssembliesUnderTest()
            .Where(x => !x.GetCustomAttributes<AssemblyMetadataAttribute>().Any(y => string.Equals(y.Key, "Warp.Package", StringComparison.Ordinal)))
            .Where(x => LoadableTypes(x).Any(y => typeof(IHostedService).IsAssignableFrom(y)))
            .Select(x => x.GetName().Name)
            .ToList();

        unmarked.ShouldBeEmpty("A Warp package with a hosted service is missing [AssemblyMetadata(\"Warp.Package\")] (src/core/Directory.Build.props).");
    }

    [TimedFact]
    public void RemoveWarpHostedServices_AppHostedServicesOfEveryShape_AreKept()
    {
        var services = FullyConfiguredServices();
        var instance = new AppHostedService();
        services.AddHostedService<AppHostedService>();
        services.AddSingleton<IHostedService>(instance);
        services.AddSingleton<IHostedService>(_ => new AppHostedService());

        services.RemoveWarpHostedServices();

        services.Count(x => x.ServiceType == typeof(IHostedService)).ShouldBe(3);
    }

    [TimedFact]
    public void RemoveWarpHostedServices_AppFactoryReturningAWarpHostedService_IsRemoved()
    {
        // The application re-hosts one of Warp's services through its own lambda: the declared return type says whose it is.
        IServiceCollection services = new ServiceCollection();
        services.Add(ServiceDescriptor.Singleton<IHostedService, WarpServerRegistration<TestContext>>(x => x.GetRequiredService<WarpServerRegistration<TestContext>>()));

        services.RemoveWarpHostedServices();

        services.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task RemoveWarpHostedServices_AppOnANpgsqlContextWithNoDatabase_HostStartsAndMediatorWorks()
    {
        // The consumer's shape: Program.cs as written (Npgsql, AddWarpServer), no database reachable, controller
        // tests that only need the app to start.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddDbContext<TestContext>(x => x.UseNpgsql("Host=unreachable.invalid;Database=app;Timeout=1"));
        builder.Services.AddWarpServer<TestContext>(x =>
        {
            x.UsePostgreSql();
            x.UseDatabasePush();
        });
        builder.Services.RemoveWarpHostedServices();
        using var host = builder.Build();

        await host.StartAsync(Xunit.TestContext.Current.CancellationToken);

        await using var scope = host.Services.CreateAsyncScope();
        var greeting = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new GetGreetingRequest { Name = "Warp" }, Xunit.TestContext.Current.CancellationToken);
        greeting.ShouldContain("Warp");
        await host.StopAsync(CancellationToken.None);
    }

    [TimedFact]
    public async Task RemoveWarpHostedServices_AppOnAnInMemoryContext_PublishingStagesAJobRowOnThatContext()
    {
        var database = $"remove-hosted-{Guid.NewGuid():N}";
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddDbContext<TestContext>(x => x.UseInMemoryDatabase(database));
        builder.Services.AddWarpServer<TestContext>(x => x.UsePostgreSql());
        builder.Services.RemoveWarpHostedServices();
        using var host = builder.Build();
        await host.StartAsync(Xunit.TestContext.Current.CancellationToken);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPublisher>().Enqueue(new UnitRequest());
            await scope.ServiceProvider.GetRequiredService<TestContext>().SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var job = await scope.ServiceProvider.GetRequiredService<TestContext>().Set<Job>().SingleAsync(Xunit.TestContext.Current.CancellationToken);
            job.CurrentState.ShouldBe(State.Enqueued);
        }

        await host.StopAsync(CancellationToken.None);
    }

    private static ServiceCollection FullyConfiguredServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(x => x.UseInMemoryDatabase($"remove-hosted-{Guid.NewGuid():N}"));
        services.AddWarpServer<TestContext>(opt =>
        {
            opt.ApplicationName = "remove-hosted";
            opt.UsePostgreSql();
            opt.UseDatabasePush();
            opt.AddRetry();
            opt.AddTimeout();
            opt.AddConcurrency();
            opt.AddRateLimit();
            opt.AddCircuitBreaker();
            opt.AddNoRestart();
            opt.AddSagas();
            opt.AddAdapters(x => x.Sink = RecordingSink.Database);
            opt.AddEndpointObservability(x => x.Sink = RecordingSink.Database);
            opt.AddClientObservability(x =>
            {
                x.Sink = RecordingSink.Database;
                x.AddIngestKey("app", "pk");
            });
            opt.AddMetrics(x => x.CollectMeter("remove-hosted"));
            opt.AddMetricBoard(x => x.Value("Volume", "c", check: MetricTileCheck.AtMost(10)));
            opt.AddSlo(x => x.AddObjective(SloKind.SuccessRate, "*", 0.99, name: "Checkout success"));
            opt.AddDashboardPush();
            opt.AddBackgroundService<HealthyResetService>();
        });

        return services;
    }

    // Written independently of the implementation: the concrete type when the registration names one.
    private static Type? ImplementationOf(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType
        ?? descriptor.ImplementationInstance?.GetType()
        ?? (descriptor.ImplementationFactory?.Method.ReturnType is { } returned && returned != typeof(object) && !returned.IsInterface
            ? returned
            : descriptor.ImplementationFactory?.Method.DeclaringType);

    // The packages this test project references, plus any other Warp assembly loaded by now.
    private static IEnumerable<Assembly> WarpPackageAssembliesUnderTest() =>
        new[]
            {
                typeof(IPublisher).Assembly,
                typeof(WarpServerBuilder<>).Assembly,
                typeof(PostgreSqlServiceConfiguration).Assembly,
                typeof(Warp.Provider.SqlServer.SqlServerServiceConfiguration).Assembly,
                typeof(WarpDashboardServiceBuilder).Assembly,
                typeof(Warp.Http.ServiceCollectionExtensions).Assembly,
                typeof(Warp.Adapters.Http.HttpAdapterServiceConfiguration).Assembly,
                typeof(Warp.Adapters.Refit.RefitAdapterServiceConfiguration).Assembly,
            }
            .Concat(AppDomain.CurrentDomain.GetAssemblies())
            .Where(x => x.GetName().Name?.StartsWith("Warp.", StringComparison.Ordinal) == true)
            .Where(x => x.GetName().Name?.StartsWith("Warp.Tests", StringComparison.Ordinal) != true)
            .Distinct();

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private sealed class AppHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
