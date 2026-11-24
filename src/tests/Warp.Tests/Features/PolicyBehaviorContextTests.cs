using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.CircuitBreaker;
using Warp.Core.Concurrency;
using Warp.Core.Data;
using Warp.Core.Data.Queries;
using Warp.Core.Handlers;
using Warp.Core.RateLimit;
using Warp.Tests.TestData;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features;

[Trait("Category", "NoDb")]
public class PolicyBehaviorContextTests
{
    [TimedFact]
    public void ResolvePipeline_WithEveryDatabaseBackedPolicyAddon_ConstructsNoApplicationContext()
    {
        // Concurrency, rate limit and circuit breaker keep state in Warp's tables. Their pipeline
        // behaviours are built for every job, with or without a policy, so building them must not
        // construct the application's DbContext (rule 6.17): their stores open Warp's own context
        // only when a job with that policy asks. Before, each one took a service built on TContext,
        // so every job paid for a full application context it never used.
        var constructed = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"policy-{Guid.NewGuid():N}"));
        services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());
        services.AddSingleton(Mock.Of<IWarpSemaphoreProvider>());
        services.AddSingleton(Mock.Of<IDatabaseExceptionClassifier>());
        services.AddWarp<TestContext>(x =>
        {
            x.AddConcurrency();
            x.AddRateLimit();
            x.AddCircuitBreaker();
        });

        // Count every TestContext the container builds, whoever asks for it.
        var descriptor = services.Single(x => x.ServiceType == typeof(TestContext));
        services.Remove(descriptor);
        services.AddScoped(sp =>
        {
            Interlocked.Increment(ref constructed);

            return ActivatorUtilities.CreateInstance<TestContext>(sp);
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Act
        var behaviors = scope.ServiceProvider.GetServices<IPipelineBehavior<UnitRequest, Unit>>().ToList();

        // Assert
        behaviors.ShouldContain(x => x is ConcurrencyPipelineBehavior<UnitRequest, Unit>);
        behaviors.ShouldContain(x => x is RateLimitPipelineBehavior<UnitRequest, Unit>);
        behaviors.ShouldContain(x => x is CircuitBreakerPipelineBehavior<UnitRequest, Unit>);
        constructed.ShouldBe(0);
    }
}
