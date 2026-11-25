using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.RateLimit;
using Warp.Tests.TestData;

namespace Warp.Tests.Features;

// #349: a host that registers its own limit manager decides a job's limit through it, as in 7.x.
// Warp's own registration keeps reading its tables directly, without resolving the manager.
[Trait("Category", "NoDb")]
public class LimitManagerReplacementTests
{
    [TimedFact]
    public async Task GetLimit_HostReplacedTheConcurrencyManager_ReturnsTheManagersLimit()
    {
        // Arrange
        var manager = new Mock<IConcurrencyLimitManager>();
        manager
            .Setup(x => x.GetLimit("orders", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConcurrencyLimitInfo("orders", 7, DateTime.UtcNow));
        var services = BuildServices();
        services.AddScoped(_ => manager.Object);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ConcurrencyLimitResolver>();

        // Act
        var limit = await resolver.GetLimit("orders", CancellationToken.None);

        // Assert
        limit.ShouldBe(7);
    }

    [TimedFact]
    public async Task GetOverride_HostReplacedTheRateLimitManager_ReturnsTheManagersLimit()
    {
        // Arrange
        var manager = new Mock<IRateLimitManager>();
        manager
            .Setup(x => x.GetLimit("orders", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitInfo("orders", 5, 60, DateTime.UtcNow));
        var services = BuildServices();
        services.AddScoped(_ => manager.Object);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<RateLimitResolver>();

        // Act
        var info = await resolver.GetOverride("orders", CancellationToken.None);

        // Assert
        info.ShouldNotBeNull();
        info.Count.ShouldBe(5);
        info.WindowSeconds.ShouldBe(60);
    }

    [TimedFact]
    public async Task GetLimit_HostReplacedTheManager_ResolvesItOnlyWhenAJobAsks()
    {
        // Arrange: building the resolver (as every job's pipeline does) must not build the manager.
        var built = 0;
        var services = BuildServices();
        services.AddScoped(_ =>
        {
            Interlocked.Increment(ref built);

            return Mock.Of<IConcurrencyLimitManager>();
        });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        // Act
        scope.ServiceProvider.GetRequiredService<ConcurrencyLimitResolver>();

        // Assert
        built.ShouldBe(0);
    }

    [TimedFact]
    public async Task GetLimit_ManagerRegisteredBeforeAddWarp_ReadsTheStoredLimitAndNotTheManager()
    {
        // Arrange: Warp's registration comes last, so the container resolves Warp's own manager and the
        // resolver reads the override row on Warp's context (rule 6.17) instead of asking a manager.
        var hostManager = new Mock<IConcurrencyLimitManager>(MockBehavior.Strict);
        var services = new ServiceCollection();
        services.AddScoped(_ => hostManager.Object);
        AddWarp(services, $"limits-{Guid.NewGuid():N}");
        services.AddScoped<IWarpServerContext>(sp => new TestServerContext(sp.GetRequiredService<TestContext>()));
        await using var provider = services.BuildServiceProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seed = seedScope.ServiceProvider.GetRequiredService<TestContext>();
            seed.Add(new ConcurrencyLimit { Name = "orders", Limit = 3, UpdatedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        await using var scope = provider.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ConcurrencyLimitResolver>();

        // Act
        var limit = await resolver.GetLimit("orders", CancellationToken.None);

        // Assert
        limit.ShouldBe(3);
        hostManager.VerifyNoOtherCalls();
    }

    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        AddWarp(services, $"limits-{Guid.NewGuid():N}");

        return services;
    }

    private static void AddWarp(IServiceCollection services, string databaseName)
    {
        services.AddLogging();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase(databaseName));
        services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());
        services.AddSingleton(Mock.Of<IWarpSemaphoreProvider>());
        services.AddSingleton(Mock.Of<IDatabaseExceptionClassifier>());
        services.AddWarp<TestContext>(x =>
        {
            x.AddConcurrency();
            x.AddRateLimit();
        });
    }

    private sealed class TestServerContext(DbContext context) : IWarpServerContext
    {
        public DbContext Context { get; } = context;
    }
}
