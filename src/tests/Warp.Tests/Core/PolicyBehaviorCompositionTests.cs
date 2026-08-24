using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.CircuitBreaker;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Retry;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

/// <summary>
/// Pins the DI-composition half of the Retry/CircuitBreaker constraint-split: the shims
/// (<c>RetryJobPipelineBehavior</c>/<c>RetryMessagePipelineBehavior</c> and the breaker pair) carry
/// the <c>IJob</c>/<c>IMessage</c> constraints, so the container must compose the behaviours into
/// job and message pipelines and EXCLUDE them from in-memory request pipelines entirely — the
/// design's cost rationale (never resolving <c>ICircuitBreakerStore</c> for an in-memory
/// <c>Send</c>) rests on exactly this. The runtime guards are defense-in-depth; this is the
/// mechanism.
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

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task JobRequest_ComposesRetryAndCircuitBreaker()
    {
        var behaviors = ResolveBehaviors<UnitRequest, Unit>();

        behaviors.ShouldContain(x => x is RetryPipelineBehavior<UnitRequest, Unit>);
        behaviors.ShouldContain(x => x is CircuitBreakerPipelineBehavior<UnitRequest, Unit>);

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task MessageRequest_ComposesRetryAndCircuitBreaker()
    {
        var behaviors = ResolveBehaviors<SingleHandlerMessage, Unit>();

        behaviors.ShouldContain(x => x is RetryPipelineBehavior<SingleHandlerMessage, Unit>);
        behaviors.ShouldContain(x => x is CircuitBreakerPipelineBehavior<SingleHandlerMessage, Unit>);

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

        var builder = new WarpBuilder<TestContext>(services);
        builder.AddRetry();
        builder.AddCircuitBreaker();

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        return [.. scope.ServiceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>()];
    }
}
