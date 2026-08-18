using System.Collections;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Queries;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Admin;

/// <summary>
/// Pins the Core-settings merge that <c>AddWarpServer</c> performs. <c>WarpServerConfiguration</c> inherits
/// every <see cref="WarpConfiguration"/> setting, but the two are separate <c>IOptions</c> singletons: in
/// the two-builder shape (<c>AddWarp</c> first with its own lambda, then <c>AddWarpServer</c>) they resolve
/// to different objects, and every server-side reader of an inherited setting — <c>ExpirationCleanup</c>'s
/// retention caps, <c>StatisticRollup</c>'s tiers, the worker's <c>ApplicationName</c> — silently read
/// defaults while Core readers read the configured value.
/// </summary>
[Trait("Category", "NoDb")]
public class WarpConfigurationMergeTests
{
    [Fact]
    public void AddWarpServer_AfterAddWarpConfiguredCoreSettings_ServerOptionsCarryThem()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(opt =>
        {
            opt.ApplicationName = "orders";
            opt.WebhookStuckDeliveryGrace = TimeSpan.FromMinutes(3);
            opt.AdapterCallLogRetentionCount = 25;
        });

        services.AddWarpServer<TestContext>(opt => opt.WorkerCount = 5);

        var server = ServerOptions(services);
        server.ApplicationName.ShouldBe("orders");
        server.WebhookStuckDeliveryGrace.ShouldBe(TimeSpan.FromMinutes(3));
        server.AdapterCallLogRetentionCount.ShouldBe(25);

        // The server builder's own settings are untouched by the merge.
        server.WorkerCount.ShouldBe(5);
    }

    [Fact]
    public void AddWarpServer_AfterBareAddWarp_KeepsItsOwnCoreSettings()
    {
        // The mirror case: shared setup calls AddWarp with no lambda, so the Core builder is all defaults
        // and has nothing to contribute. A Core setting configured in the AddWarpServer lambda must survive
        // — overwriting it with the other builder's default would silently discard an explicit value.
        var services = NewServices();
        services.AddWarp<TestContext>();
        services.AddWarpServer<TestContext>(opt => opt.Schema = "custom");

        ServerOptions(services).Schema.ShouldBe("custom");
    }

    [Fact]
    public void AddWarpServer_SameCoreSettingConfiguredInBothLambdas_Throws()
    {
        // Nothing distinguishes which lambda the author meant to win, so picking one silently would make
        // the runtime disagree with half the source. Fail at registration instead.
        var services = NewServices();
        services.AddWarp<TestContext>(opt => opt.Schema = "from-core");

        var ex = Should.Throw<InvalidOperationException>(
            () => services.AddWarpServer<TestContext>(opt => opt.Schema = "from-server"));

        ex.Message.ShouldContain("Schema");
        ex.Message.ShouldContain("from-core");
        ex.Message.ShouldContain("from-server");
    }

    [Fact]
    public void AddWarpServer_SameCoreSettingConfiguredIdenticallyInBothLambdas_DoesNotThrow()
    {
        var services = NewServices();
        services.AddWarp<TestContext>(opt => opt.Schema = "shared");

        Should.NotThrow(() => services.AddWarpServer<TestContext>(opt => opt.Schema = "shared"));
    }

    [Fact]
    public void ApplyCoreSettings_EveryWritableCoreSetting_IsCarried()
    {
        // Rot guard: the merge is reflection-driven, so a newly added WarpConfiguration property is picked
        // up for free — this test fails only if someone gives the merge an exclusion it should not have.
        var source = new WarpConfiguration();
        var target = new WarpServerConfiguration();

        var settings = typeof(WarpConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.CanRead && x.CanWrite)
            .ToList();

        settings.ShouldNotBeEmpty();

        foreach (var setting in settings)
        {
            setting.SetValue(source, NonDefaultValue(setting));
        }

        WarpConfigurationMerge.ApplyCoreSettings(source, target);

        foreach (var setting in settings)
        {
            var carried = setting.GetValue(target);
            var expected = setting.GetValue(source);

            if (carried is IEnumerable carriedItems and not string && expected is IEnumerable expectedItems and not string)
            {
                carriedItems.Cast<object?>().ShouldBe(expectedItems.Cast<object?>(), $"{setting.Name} was not carried");

                continue;
            }

            carried.ShouldBe(expected, $"{setting.Name} was not carried");
        }
    }

    // AddWarp requires the user's DbContext to be registered; the merge runs before anything touches it,
    // so an in-memory provider is enough scaffolding.
    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"merge-{Guid.NewGuid():N}"));

        return services;
    }

    // Resolved from a built provider rather than read off the ServiceDescriptor: what matters is the value
    // a server task actually receives, not how the registration is shaped.
    [Fact]
    public void AddWarp_AfterAddWarpServer_ReverseOrder_CoreSettingsAreNotLost()
    {
        // The mirror shape. AddWarp's own TryAdd for IOptions<WarpConfiguration> no-ops because
        // AddWarpServer already registered one, so without the symmetric merge every field set in THIS
        // lambda is silently dropped — while the addons the same lambda registered still take effect,
        // which makes the loss look like a partial application of the block rather than a no-op.
        var services = NewServices();
        services.AddWarpServer<TestContext>(opt => opt.WorkerCount = 5);
        services.AddWarp<TestContext>(opt =>
        {
            opt.ApplicationName = "orders";
            opt.EndpointCallLogRetentionCount = 99;
        });

        var core = CoreOptions(services);
        core.ApplicationName.ShouldBe("orders");
        core.EndpointCallLogRetentionCount.ShouldBe(99);

        // Both views agree — that is the whole point of merging rather than picking a winner.
        var server = ServerOptions(services);
        server.ApplicationName.ShouldBe("orders");
        server.EndpointCallLogRetentionCount.ShouldBe(99);
        server.WorkerCount.ShouldBe(5);
    }

    [Fact]
    public void AddWarp_AfterAddWarpServer_ReverseOrderConflict_Throws()
    {
        var services = NewServices();
        services.AddWarpServer<TestContext>(opt => opt.Schema = "from-server");

        var ex = Should.Throw<InvalidOperationException>(
            () => services.AddWarp<TestContext>(opt => opt.Schema = "from-core"));

        ex.Message.ShouldContain("Schema");
    }

    [Fact]
    public void AddWarpServer_Alone_NeedsNoMergeAndCarriesItsOwnCoreSettings()
    {
        // Single-builder shape: AddWarpServer registers its builder for BOTH options, so the two views are
        // the same object and the merge short-circuits on reference equality. This is the common shape —
        // if it ever started throwing, every single-builder host would fail at startup.
        var services = NewServices();
        services.AddWarpServer<TestContext>(opt =>
        {
            opt.ApplicationName = "solo";
            opt.WorkerCount = 3;
        });

        ServerOptions(services).ApplicationName.ShouldBe("solo");
        CoreOptions(services).ApplicationName.ShouldBe("solo");
    }

    [Fact]
    public void AddWarpServer_CollectionSettingLeftAtItsDefault_IsNotReportedAsAConflict()
    {
        // InAppNamespaceDenylist's default is a freshly built list, so a new instance on each builder is
        // never reference-equal to the other's. Comparing those by identity would make EVERY two-builder
        // registration throw on a setting nobody touched — the merge sequence-compares for exactly this.
        var services = NewServices();
        services.AddWarp<TestContext>(opt => opt.ApplicationName = "orders");

        Should.NotThrow(() => services.AddWarpServer<TestContext>(opt => opt.WorkerCount = 2));

        ServerOptions(services).InAppNamespaceDenylist.ShouldBe(new WarpConfiguration().InAppNamespaceDenylist);
    }

    [Fact]
    public void AddWarpServer_CoreOptionsRegisteredByFactory_SkipsTheMergeWithoutThrowing()
    {
        // A host that registers IOptions<WarpConfiguration> itself (factory, not instance) leaves nothing
        // for the merge to read. Degrading to today's unmerged behavior is acceptable; failing to start is
        // not, so pin that this stays a silent skip rather than a NullReference or a spurious conflict.
        var services = NewServices();
        services.AddSingleton<IOptions<WarpConfiguration>>(_ => Options.Create(new WarpConfiguration { Schema = "factory" }));

        Should.NotThrow(() => services.AddWarpServer<TestContext>(opt => opt.WorkerCount = 4));

        ServerOptions(services).WorkerCount.ShouldBe(4);
    }

    [Fact]
    public void AddWarpServer_TwoBuilderShape_ServerTasksResolveWithTheMergedConfiguration()
    {
        // End of the chain: the real container builds the real server tasks, and the options instance they
        // are constructed with carries a value that was only ever set in the AddWarp lambda. Asserting on
        // the ServiceCollection alone would not prove the graph can even be built in this shape.
        var services = NewServices();
        services.AddWarp<TestContext>(opt => opt.ErrorGroupRetention = TimeSpan.FromDays(3));
        services.AddWarpServer<TestContext>(opt => opt.ExpirationCleanupInterval = TimeSpan.FromMinutes(2));

        // The scaffolding a provider package contributes in production (UsePostgreSql / UseSqlServer),
        // stubbed so the server tasks can actually be activated — same substitution DeploymentShapeTests makes.
        services.AddLogging();
        services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());
        services.AddSingleton<IWarpServerContextConfigurator>(new InMemoryServerContextConfigurator());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<IServerTask>()
            .OfType<ExpirationCleanup<TestContext>>()
            .ShouldHaveSingleItem();

        var configuration = scope.ServiceProvider.GetRequiredService<IOptions<WarpServerConfiguration>>().Value;
        configuration.ErrorGroupRetention.ShouldBe(TimeSpan.FromDays(3));
        configuration.ExpirationCleanupInterval.ShouldBe(TimeSpan.FromMinutes(2));
    }

    private static WarpServerConfiguration ServerOptions(IServiceCollection services)
    {
        return services.BuildServiceProvider().GetRequiredService<IOptions<WarpServerConfiguration>>().Value;
    }

    private static WarpConfiguration CoreOptions(IServiceCollection services)
    {
        return services.BuildServiceProvider().GetRequiredService<IOptions<WarpConfiguration>>().Value;
    }

    // A value guaranteed to differ from the property's default, so the merge has something to carry.
    private static object? NonDefaultValue(PropertyInfo property)
    {
        var current = property.GetValue(new WarpConfiguration());
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(string))
        {
            return $"merge-probe-{property.Name}";
        }

        if (type == typeof(bool))
        {
            return !(bool)(current ?? false);
        }

        if (type == typeof(int))
        {
            return (int)(current ?? 0) + 12_345;
        }

        if (type == typeof(TimeSpan))
        {
            return (TimeSpan)(current ?? TimeSpan.Zero) + TimeSpan.FromMinutes(7);
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type).Cast<object>().First(x => !Equals(x, current));
        }

        if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
        {
            return new List<string> { $"Probe.{property.Name}" };
        }

        throw new NotSupportedException(
            $"WarpConfiguration.{property.Name} is a {type.Name}, which this test cannot generate a "
            + "non-default value for. Extend NonDefaultValue so the merge stays covered.");
    }

    private sealed class InMemoryServerContextConfigurator : IWarpServerContextConfigurator
    {
        private readonly string _database = $"merge-server-{Guid.NewGuid():N}";

        public void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider applicationServices)
        {
            optionsBuilder.UseInMemoryDatabase(_database);
        }
    }
}
