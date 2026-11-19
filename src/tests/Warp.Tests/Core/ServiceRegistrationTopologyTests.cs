using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Queries;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Services;
using Warp.Core.Slo;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Core;

/// <summary>
/// Pins which <see cref="IMetricPointSink"/> each process shape resolves whatever order <c>AddMetrics</c> and
/// <c>AddWarpServer</c> ran in, and which <see cref="IServerTask"/>s each server shape registers. The cluster-locked aggregation
/// tasks (counter folding, error grouping, statistic rollup, SLO evaluation) fold rows any process
/// writes, so a service-only server runs them. The service-only job-only-absent list is owned by
/// <c>DeploymentShapeTests</c>.
/// </summary>
[Trait("Category", "NoDb")]
public class ServiceRegistrationTopologyTests
{
    [TimedFact]
    public void AddWarpServer_DisableWorker_RegistersAggregationTasks()
    {
        var taskTypes = ResolveServerTaskTypes(BuildServices(x => x.DisableWorker()));

        taskTypes.ShouldContain(typeof(CounterAggregator<TestContext>));
        taskTypes.ShouldContain(typeof(ErrorGroupAggregator<TestContext>));
        taskTypes.ShouldContain(typeof(SloEvaluator<TestContext>));
    }

    [TimedFact]
    public void AddWarpServer_DisableWorker_StillRegistersStatisticRollup()
    {
        var taskTypes = ResolveServerTaskTypes(BuildServices(x => x.DisableWorker()));

        taskTypes.ShouldContain(typeof(StatisticRollup<TestContext>));
    }

    [TimedFact]
    public void AddWarpServer_DisableWorker_RegistersMetricRollup()
    {
        var taskTypes = ResolveServerTaskTypes(BuildServices(x => x.DisableWorker()));

        taskTypes.Count(x => x == typeof(MetricRollup<TestContext>)).ShouldBe(1);
    }

    [TimedFact]
    public void AddWarpServer_DefaultWorker_RegistersMetricRollup()
    {
        var taskTypes = ResolveServerTaskTypes(BuildServices());

        taskTypes.Count(x => x == typeof(MetricRollup<TestContext>)).ShouldBe(1);
    }

    [TimedFact]
    public void AddWarpServer_DefaultWorker_RegistersExpectedTaskSetExactlyOnce()
    {
        var taskTypes = ResolveServerTaskTypes(BuildServices());

        var expected = new[]
        {
            typeof(Heartbeat<TestContext>),
            typeof(ServerCleanup<TestContext>),
            typeof(ExpirationCleanup<TestContext>),
            typeof(StatisticRollup<TestContext>),
            typeof(MetricRollup<TestContext>),
            typeof(CounterAggregator<TestContext>),
            typeof(ErrorGroupAggregator<TestContext>),
            typeof(SloEvaluator<TestContext>),
            typeof(StaleJobRecovery<TestContext>),
            typeof(BacklogSampler<TestContext>),
            typeof(RecurringJobScheduler<TestContext>),
            typeof(ScheduledJobActivation<TestContext>),
            typeof(Orchestrator<TestContext>),
            typeof(MessageRouter<TestContext>),
        };

        taskTypes.OrderBy(x => x.FullName, StringComparer.Ordinal)
            .ShouldBe(expected.OrderBy(x => x.FullName, StringComparer.Ordinal));
    }

    [TimedFact]
    public void AddMetrics_InsideTheAddWarpServerLambda_ResolvesTheServerSink()
    {
        var services = BuildServices(x => x.AddMetrics(y => y.CollectMeter(MeterName())));

        ResolveSinkType(services).ShouldBe(typeof(ServerMetricPointSink));
    }

    [TimedFact]
    public void AddMetrics_AddWarpBeforeAddWarpServer_ResolvesTheServerSink()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(x => x.AddMetrics(y => y.CollectMeter(MeterName())));
        services.AddWarpServer<TestContext>(_ => { });

        ResolveSinkType(services).ShouldBe(typeof(ServerMetricPointSink));
    }

    [TimedFact]
    public void AddMetrics_AddWarpServerBeforeAddWarp_ResolvesTheServerSink()
    {
        var services = NewServices();
        services.AddWarpServer<TestContext>(_ => { });
        services.AddWarp<TestContext>(x => x.AddMetrics(y => y.CollectMeter(MeterName())));

        ResolveSinkType(services).ShouldBe(typeof(ServerMetricPointSink));
    }

    [TimedFact]
    public void AddMetrics_AddWarpOnly_ResolvesTheContextSink()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(x => x.AddMetrics(y => y.CollectMeter(MeterName())));

        ResolveSinkType(services).ShouldBe(typeof(ContextMetricPointSink<TestContext>));
    }

    [TimedFact]
    public void AddMetrics_CalledTwice_RegistersOneFlusherAndOneSink()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(x =>
        {
            x.AddMetrics(y => y.CollectMeter(MeterName()));
            x.AddMetrics(y => y.CollectMeter(MeterName()));
        });

        services.Count(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(MetricFlusher)).ShouldBe(1);
        services.Count(x => x.ServiceType == typeof(IMetricPointSink)).ShouldBe(1);
    }

    [TimedFact]
    public void AddMetrics_NonPositiveFlushInterval_ThrowsAtRegistration()
    {
        var services = NewServices();

        Should.Throw<InvalidOperationException>(() =>
            services.AddWarp<TestContext>(x =>
            {
                x.MetricFlushInterval = TimeSpan.Zero;
                x.AddMetrics(y => y.CollectMeter(MeterName()));
            }));
    }

    [TimedFact]
    public void AddWarpServer_NonPositiveMetricRollupInterval_ThrowsAtRegistration()
    {
        Should.Throw<InvalidOperationException>(() => BuildServices(x => x.MetricRollupInterval = TimeSpan.Zero));
    }

    [TimedFact]
    public void AddWarpServer_NullMetricRollupInterval_IsAccepted()
    {
        Should.NotThrow(() => BuildServices(x => x.MetricRollupInterval = null));
    }

    [TimedFact]
    public void AddMetrics_NegativeFlushInterval_ThrowsAtRegistration()
    {
        var services = NewServices();

        Should.Throw<InvalidOperationException>(() =>
            services.AddWarp<TestContext>(x =>
            {
                x.MetricFlushInterval = TimeSpan.FromSeconds(-1);
                x.AddMetrics(y => y.CollectMeter(MeterName()));
            }));
    }

    [TimedFact]
    public void AddWarpServer_NegativeMetricRollupInterval_ThrowsAtRegistration()
    {
        Should.Throw<InvalidOperationException>(() => BuildServices(x => x.MetricRollupInterval = TimeSpan.FromSeconds(-1)));
    }

    [TimedTheory]
    [InlineData(nameof(WarpMetricsOptions.MaxTagsPerInstrument), 0)]
    [InlineData(nameof(WarpMetricsOptions.MaxTagsPerInstrument), -1)]
    [InlineData(nameof(WarpMetricsOptions.MaxValuesPerTag), 0)]
    [InlineData(nameof(WarpMetricsOptions.MaxValuesPerTag), -1)]
    [InlineData(nameof(WarpMetricsOptions.MaxSeriesPerInstrument), 0)]
    [InlineData(nameof(WarpMetricsOptions.MaxSeriesPerInstrument), -1)]
    [InlineData(nameof(WarpMetricsOptions.MaxInstruments), 0)]
    [InlineData(nameof(WarpMetricsOptions.MaxInstruments), -1)]
    public void AddMetrics_NonPositiveCap_ThrowsAtRegistration(string cap, int value)
    {
        var services = NewServices();

        var error = Should.Throw<InvalidOperationException>(() =>
            services.AddWarp<TestContext>(x => x.AddMetrics(y => SetCap(y.CollectMeter(MeterName()), cap, value))));

        error.Message.ShouldStartWith(cap);
    }

    [TimedFact]
    public void AddWarp_Only_ResolvesTheMetricQueryService()
    {
        // No AddMetrics and no hand registration: a dashboard-only host must still serve /api/metrics (R19).
        var services = NewServices();
        services.AddWarp<TestContext>(_ => { });

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetService<IMetricQueryService>().ShouldNotBeNull();
    }

    [TimedFact]
    public void AddWarp_Only_ResolvesTheMetricBoardServicesWithoutAnyHostedSeeder()
    {
        // A dashboard-only host serves the health board (#326) with no AddMetrics and no AddMetricBoard.
        var services = NewServices();
        services.AddWarp<TestContext>(_ => { });

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetService<IMetricBoardQueryService>().ShouldNotBeNull();
        scope.ServiceProvider.GetService<IMetricBoardCommandService>().ShouldNotBeNull();
        services.ShouldNotContain(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(MetricBoardSeeder<TestContext>));
    }

    [TimedFact]
    public void AddWarp_Only_ResolvesTheDeployVerdictService()
    {
        // A dashboard-only (or CI-facing) host serves the post-deploy verdict with no server, no AddMetrics and no AddSlo.
        var services = NewServices();
        services.AddWarp<TestContext>(_ => { });

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetService<IDeployVerdictService>().ShouldBeOfType<DeployVerdictService>();
        services.Count(x => x.ServiceType == typeof(IDeployVerdictService)).ShouldBe(1);
    }

    [TimedFact]
    public void AddWarp_Only_ResolvesTheMetricAnnotationServices()
    {
        // A dashboard-only host edits and shows annotations (#333) with no AddMetrics and no server.
        var services = NewServices();
        services.AddWarp<TestContext>(_ => { });

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetService<IMetricAnnotationQueryService>().ShouldNotBeNull();
        scope.ServiceProvider.GetService<IMetricAnnotationCommandService>().ShouldNotBeNull();
        services.Count(x => x.ServiceType == typeof(IMetricAnnotationCommandService)).ShouldBe(1);
    }

    [TimedFact]
    public void AddMetricBoard_CalledTwice_RegistersOneSeederAndOneMergedBoard()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(x =>
        {
            x.AddMetricBoard(y => y.Value("A", "a"));
            x.AddMetricBoard(y => y.Value("B", "b"));
        });

        services.Count(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(MetricBoardSeeder<TestContext>)).ShouldBe(1);
        services.Count(x => x.ServiceType == typeof(IMetricsMarker)).ShouldBe(0);
        var seeds = services
            .Where(x => x.ServiceType == typeof(MetricBoardSeeds))
            .Select(x => x.ImplementationInstance)
            .OfType<MetricBoardSeeds>()
            .ShouldHaveSingleItem();
        var board = seeds.Boards.ShouldHaveSingleItem();
        board.Name.ShouldBeNull();
        board.Tiles.Seeded.Select(x => x.Name).ShouldBe(["A", "B"]);
    }

    [TimedFact]
    public void AddSlo_AfterAddMetricBoard_StartsTheSloSeederBeforeTheBoardSeeder()
    {
        // A board's SLO tile resolves its objective at seed time: objectives are seeded first, whatever the call order.
        var services = NewServices();
        services.AddWarp<TestContext>(x =>
        {
            x.AddMetricBoard(y => y.Slo("Checkout success"));
            x.AddSlo(y => y.AddObjective(SloKind.SuccessRate, "*", 0.99, name: "Checkout success"));
        });

        SeederOrder(services).ShouldBe([typeof(SloSeeder<TestContext>), typeof(MetricBoardSeeder<TestContext>)]);
    }

    [TimedFact]
    public void AddSlo_BeforeAddMetricBoard_StartsTheSloSeederBeforeTheBoardSeeder()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(x =>
        {
            x.AddSlo(y => y.AddObjective(SloKind.SuccessRate, "*", 0.99, name: "Checkout success"));
            x.AddMetricBoard(y => y.Slo("Checkout success"));
        });

        SeederOrder(services).ShouldBe([typeof(SloSeeder<TestContext>), typeof(MetricBoardSeeder<TestContext>)]);
    }

    [TimedFact]
    public void AddMetricBoard_NamedBoards_MergeByNameIgnoringCaseAndDefaultIsTheUnnamedBoard()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(x =>
        {
            x.AddMetricBoard("Checkout", y => y.Value("A", "a"));
            x.AddMetricBoard("checkout", y => y.Value("B", "b"));
            x.AddMetricBoard("Payments", _ => { });
            x.AddMetricBoard("default", y => y.Value("C", "c"));
            x.AddMetricBoard(y => y.Value("D", "d"));
        });

        services.Count(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(MetricBoardSeeder<TestContext>)).ShouldBe(1);
        var seeds = services
            .Where(x => x.ServiceType == typeof(MetricBoardSeeds))
            .Select(x => x.ImplementationInstance)
            .OfType<MetricBoardSeeds>()
            .ShouldHaveSingleItem();
        seeds.Boards.Select(x => x.Name).ShouldBe(["Checkout", "Payments", null]);
        seeds.Boards[0].Tiles.Seeded.Select(x => x.Name).ShouldBe(["A", "B"]);
        seeds.Boards[1].Tiles.Seeded.ShouldBeEmpty();
        seeds.Boards[2].Tiles.Seeded.Select(x => x.Name).ShouldBe(["C", "D"]);
    }

    [TimedFact]
    public void AddMetricBoard_TheSameTileNameOnTwoBoards_IsAllowed()
    {
        var services = NewServices();

        services.AddWarp<TestContext>(x =>
        {
            x.AddMetricBoard("Checkout", y => y.Value("Volume", "a"));
            x.AddMetricBoard("Payments", y => y.Value("Volume", "a"));
        });

        services.ShouldContain(x => x.ServiceType == typeof(MetricBoardSeeds));
    }

    [TimedTheory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddMetricBoard_BlankName_Throws(string name)
    {
        var services = NewServices();

        Should.Throw<ArgumentException>(() => services.AddWarp<TestContext>(x => x.AddMetricBoard(name, y => y.Value("A", "a"))));
    }

    [TimedFact]
    public void AddSlo_CalledTwice_RegistersOneSeederAndOneMergedSetOfObjectives()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(x =>
        {
            x.AddSlo(y => y.AddObjective(SloKind.SuccessRate, "SendEmail", 0.99));
            x.AddSlo();
            x.AddSlo(y => y.AddMetricThreshold("5xx", "http.server.request.duration", 0));
        });

        services.Count(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(SloSeeder<TestContext>)).ShouldBe(1);
        services.Count(x => x.ServiceType == typeof(ISloMarker)).ShouldBe(1);
        var options = services
            .Where(x => x.ServiceType == typeof(SloOptions))
            .Select(x => x.ImplementationInstance)
            .OfType<SloOptions>()
            .ShouldHaveSingleItem();
        options.Seeded.Select(x => x.Name).ShouldBe(["SuccessRate SendEmail", "5xx"]);
    }

    [TimedFact]
    public void AddSlo_DuplicateMetricNameAcrossTwoCalls_Throws()
    {
        // The duplicate-name check spans the merged set: a repeat in a second AddSlo would otherwise seed only one.
        var services = NewServices();

        Should.Throw<ArgumentException>(() => services.AddWarp<TestContext>(x =>
        {
            x.AddSlo(y => y.AddMetricThreshold("5xx", "http.server.request.duration", 0));
            x.AddSlo(y => y.AddMetricThreshold("5XX", "http.server.duration", 0));
        }));
    }

    [TimedFact]
    public void AddSlo_NoObjectives_RegistersTheMarkerButNoSeeder()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(x => x.AddSlo());

        services.Count(x => x.ServiceType == typeof(ISloMarker)).ShouldBe(1);
        services.ShouldNotContain(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(SloSeeder<TestContext>));
        services.ShouldNotContain(x => x.ServiceType == typeof(SloOptions));
    }

    [TimedFact]
    public void AddMetricBoard_NoTiles_RegistersNoSeeder()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(x => x.AddMetricBoard(_ => { }));

        services.ShouldNotContain(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(MetricBoardSeeder<TestContext>));
    }

    [TimedFact]
    public void AddMetrics_CollectorHost_IsRegisteredBeforeTheFlusher()
    {
        // Hosted services stop in reverse registration order: the flusher must stop first, so its final drain still
        // runs while the collector's listener feeds the aggregator.
        var services = NewServices();
        services.AddWarp<TestContext>(x => x.AddMetrics(y => y.CollectMeter(MeterName())));

        var hosted = services
            .Where(x => x.ServiceType == typeof(IHostedService))
            .Select(x => x.ImplementationType)
            .ToList();
        var collector = hosted.IndexOf(typeof(MetricCollectorHost));
        var flusher = hosted.IndexOf(typeof(MetricFlusher));

        collector.ShouldBeGreaterThanOrEqualTo(0);
        flusher.ShouldBeGreaterThanOrEqualTo(0);
        collector.ShouldBeLessThan(flusher);
    }

    [TimedFact]
    public void MetricRollup_NullMetricRollupInterval_HasNoDefaultInterval()
    {
        var rollup = ResolveMetricRollup(BuildServices(x => x.MetricRollupInterval = null));

        rollup.DefaultInterval.ShouldBeNull();
    }

    [TimedFact]
    public void MetricRollup_ConfiguredMetricRollupInterval_IsItsDefaultInterval()
    {
        var rollup = ResolveMetricRollup(BuildServices(x => x.MetricRollupInterval = TimeSpan.FromMinutes(3)));

        rollup.DefaultInterval.ShouldBe(TimeSpan.FromMinutes(3));
    }

    [TimedFact]
    public void MetricRollup_Identity_IsTheMetricRollupLockUnderATransactionScopedLock()
    {
        var rollup = ResolveMetricRollup(BuildServices());

        rollup.LockKey.ShouldBe("warp:metric-rollup");

        // Read through the interface: the task inherits the default member, so this is its effective value.
        ((IServerTask)rollup).LocksWithTransaction.ShouldBeTrue();
    }

    private static string MeterName() => $"topology.{Guid.NewGuid():N}";

    private static void SetCap(WarpMetricsOptions options, string cap, int value)
    {
        switch (cap)
        {
            case nameof(WarpMetricsOptions.MaxTagsPerInstrument):
                options.MaxTagsPerInstrument = value;
                break;
            case nameof(WarpMetricsOptions.MaxValuesPerTag):
                options.MaxValuesPerTag = value;
                break;
            case nameof(WarpMetricsOptions.MaxSeriesPerInstrument):
                options.MaxSeriesPerInstrument = value;
                break;
            case nameof(WarpMetricsOptions.MaxInstruments):
                options.MaxInstruments = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cap), cap, "Unknown cap.");
        }
    }

    private static MetricRollup<TestContext> ResolveMetricRollup(ServiceCollection services)
    {
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();

        return scope.ServiceProvider
            .GetServices<IServerTask>()
            .OfType<MetricRollup<TestContext>>()
            .Single();
    }

    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(x => x.UseInMemoryDatabase($"topology-{Guid.NewGuid():N}"));
        services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());
        services.AddSingleton<IWarpServerContextConfigurator>(new InMemoryServerContextConfigurator());

        return services;
    }

    private static ServiceCollection BuildServices(Action<WarpServerBuilder<TestContext>>? configure = null)
    {
        var services = NewServices();
        services.AddWarpServer<TestContext>(x => configure?.Invoke(x));

        return services;
    }

    private static Type ResolveSinkType(ServiceCollection services)
    {
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();

        return scope.ServiceProvider
            .GetRequiredService<IMetricPointSink>()
            .GetType();
    }

    private static List<Type> ResolveServerTaskTypes(ServiceCollection services)
    {
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();

        return
        [
            .. scope.ServiceProvider
                .GetServices<IServerTask>()
                .Select(x => x.GetType()),
        ];
    }

    // The seeders in hosted-service registration order, which is the order the host starts them in.
    private static List<Type?> SeederOrder(IServiceCollection services)
        =>
        [
            .. services
                .Where(x => x.ServiceType == typeof(IHostedService))
                .Select(x => x.ImplementationType)
                .Where(x => x == typeof(SloSeeder<TestContext>) || x == typeof(MetricBoardSeeder<TestContext>)),
        ];

    private sealed class InMemoryServerContextConfigurator : IWarpServerContextConfigurator
    {
        private readonly string _database = $"topology-server-{Guid.NewGuid():N}";

        public void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider applicationServices)
        {
            optionsBuilder.UseInMemoryDatabase(_database);
        }
    }
}
