using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Warp.Core;
using Warp.Core.BackgroundServices;

namespace Warp.Tests.BackgroundServices;

[Trait("Category", "NoDb")]
public class WarpBackgroundServiceBuilderTests
{
    private const string DummyConnectionString = "Host=x;Database=x;Username=x;Password=x";

    private static WarpBuilder<TestContext> CreateBuilder()
    {
        var services = new ServiceCollection();
        return new WarpBuilder<TestContext>(services);
    }

    // Pins the dashboard-only / publisher-only deployment path: AddWarp<TContext> alone
    // (without AddWarpWorker) must register IBackgroundServiceQueryService so the
    // /api/services endpoints can resolve it. Regression guard — a previous iteration
    // registered the query service inside AddWarpWorker, silently breaking dashboard-only
    // processes whose endpoints inject IBackgroundServiceQueryService non-nullably.
    [TimedFact]
    public void AddWarp_RegistersBackgroundServiceQueryService_WithoutAddWarpWorker()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseNpgsql(DummyConnectionString));

        services.AddWarp<TestContext>();

        services.ShouldContain(
            d => d.ServiceType == typeof(IBackgroundServiceQueryService)
                && d.Lifetime == ServiceLifetime.Scoped);
    }

    [TimedFact]
    public void AddBackgroundService_RegistersTypeAsSingleton()
    {
        var builder = CreateBuilder();

        builder.AddBackgroundService<TestService>();

        var sp = builder.Services.BuildServiceProvider();
        var first = sp.GetRequiredService<TestService>();
        var second = sp.GetRequiredService<TestService>();

        first.ShouldBeSameAs(second);
    }

    [TimedFact]
    public void AddBackgroundService_ResolvesAsWarpBackgroundService()
    {
        var builder = CreateBuilder();

        builder.AddBackgroundService<TestService>();

        var sp = builder.Services.BuildServiceProvider();
        var concrete = sp.GetRequiredService<TestService>();
        var alias = sp.GetServices<WarpBackgroundService>().Single();

        alias.ShouldBeSameAs(concrete);
    }

    [TimedFact]
    public void AddBackgroundService_CalledTwiceForSameType_IsIdempotent()
    {
        var builder = CreateBuilder();

        builder.AddBackgroundService<TestService>();
        builder.AddBackgroundService<TestService>();

        var sp = builder.Services.BuildServiceProvider();
        var aliases = sp.GetServices<WarpBackgroundService>()
            .Where(x => x is TestService)
            .ToList();

        aliases.Count.ShouldBe(1);
    }

    [TimedFact]
    public void AddBackgroundService_TwoDifferentServices_BothResolveViaAlias()
    {
        var builder = CreateBuilder();

        builder.AddBackgroundService<TestService>();
        builder.AddBackgroundService<AnotherTestService>();

        var sp = builder.Services.BuildServiceProvider();
        var aliases = sp.GetServices<WarpBackgroundService>().ToList();

        aliases.Count.ShouldBe(2);
        aliases.ShouldContain(x => x is TestService);
        aliases.ShouldContain(x => x is AnotherTestService);
    }

    [TimedFact]
    public void WarpBackgroundService_NameDefault_IsTypeName()
    {
        var service = new TestService();

        service.Name.ShouldBe(nameof(TestService));
    }

    [TimedFact]
    public void WarpBackgroundService_ScopeDefault_IsPerServer()
    {
        var service = new TestService();

        service.Scope.ShouldBe(ServiceScope.PerServer);
    }

    [TimedFact]
    public void WarpBackgroundService_MinLogLevelDefault_IsInformation()
    {
        var service = new TestService();

        service.MinLogLevel.ShouldBe(LogLevel.Information);
    }

    [TimedFact]
    public void WarpBackgroundService_NameCustomOverride_Honored()
    {
        var service = new CustomNameService();

        service.Name.ShouldBe("my-custom-name");
    }

    public sealed class TestService : WarpBackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
    }

    public sealed class AnotherTestService : WarpBackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
    }

    public sealed class CustomNameService : WarpBackgroundService
    {
        public override string Name => "my-custom-name";

        protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
