using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.CircuitBreaker;
using Warp.Core.Concurrency;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.RateLimit;
using Warp.Core.Retry;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

/// <summary>
/// Pins the DI-composition half of the constraint-split for all four DbContext-backed policy addons:
/// the shims must compose into job and message pipelines and be absent from in-memory request pipelines.
/// </summary>
[Trait("Category", "NoDb")]
public class PolicyBehaviorCompositionTests
{
    [TimedFact]
    public Task InMemoryRequest_ComposesNoRetryOrCircuitBreaker()
    {
        var behaviors = ResolveBehaviors<GetGreetingRequest, string>();

        behaviors.ShouldNotContain(x => x is RetryPipelineBehavior<GetGreetingRequest, string>);
        behaviors.ShouldNotContain(x => x is CircuitBreakerPipelineBehavior<GetGreetingRequest, string>);
        behaviors.ShouldNotContain(x => x is ConcurrencyPipelineBehavior<GetGreetingRequest, string>);
        behaviors.ShouldNotContain(x => x is RateLimitPipelineBehavior<GetGreetingRequest, string>);

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task JobRequest_ComposesRetryAndCircuitBreaker()
    {
        var behaviors = ResolveBehaviors<UnitRequest, Unit>();

        behaviors.ShouldContain(x => x is RetryPipelineBehavior<UnitRequest, Unit>);
        behaviors.ShouldContain(x => x is CircuitBreakerPipelineBehavior<UnitRequest, Unit>);
        behaviors.ShouldContain(x => x is ConcurrencyPipelineBehavior<UnitRequest, Unit>);
        behaviors.ShouldContain(x => x is RateLimitPipelineBehavior<UnitRequest, Unit>);

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task MessageRequest_ComposesRetryAndCircuitBreaker()
    {
        var behaviors = ResolveBehaviors<SingleHandlerMessage, Unit>();

        behaviors.ShouldContain(x => x is RetryPipelineBehavior<SingleHandlerMessage, Unit>);
        behaviors.ShouldContain(x => x is CircuitBreakerPipelineBehavior<SingleHandlerMessage, Unit>);
        behaviors.ShouldContain(x => x is ConcurrencyPipelineBehavior<SingleHandlerMessage, Unit>);
        behaviors.ShouldContain(x => x is RateLimitPipelineBehavior<SingleHandlerMessage, Unit>);

        return Task.CompletedTask;
    }

    private static IPipelineBehavior<TRequest, TResponse>[] ResolveBehaviors<TRequest, TResponse>()
        where TRequest : IRequest<TResponse>
    {
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddScoped<TestContext>(_ => null!);
        services.AddSingleton(Mock.Of<Warp.Core.Data.IDatabaseExceptionClassifier>());
        services.AddSingleton(Mock.Of<IWarpSemaphoreProvider>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());

        var builder = new WarpBuilder<TestContext>(services);
        builder.AddRetry();
        builder.AddCircuitBreaker();
        builder.AddConcurrency();
        builder.AddRateLimit();

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        return [.. scope.ServiceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>()];
    }
}
