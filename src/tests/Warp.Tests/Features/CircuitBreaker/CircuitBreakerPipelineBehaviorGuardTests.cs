using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Warp.Core.CircuitBreaker;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features.CircuitBreaker;

/// <summary>
/// Guard coverage for the widened circuit breaker: in-memory sends and policy-exempt handlers must
/// pass through without touching the store (the DI shims keep in-memory pipelines from composing the
/// behaviour at all — these tests pin the runtime defense for direct construction). Also pins the
/// message path: a routed message child rescheduled by an open circuit keeps <c>HandlerType</c>
/// (§8.14 — <c>ClearHandlerType</c> defaults to false, an invariant a future edit must not break).
/// </summary>
[Trait("Category", "NoDb")]
public class CircuitBreakerPipelineBehaviorGuardTests
{
    [TimedFact]
    public async Task InMemoryRequest_PassesThroughWithoutTouchingStore()
    {
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        var store = new Mock<ICircuitBreakerStore>(MockBehavior.Strict);
        var behavior = Build<GetGreetingRequest, string>(ctx, store.Object);

        var result = await behavior.HandleAsync(
            new GetGreetingRequest { Name = "x" },
            (req, ct) => Task.FromResult("ok"),
            CancellationToken.None);

        result.ShouldBe("ok");
        store.VerifyNoOtherCalls();
    }

    [TimedFact]
    public async Task PolicyExemptHandler_PassesThroughWithoutTouchingStore()
    {
        var ctx = new JobContext { JobId = Guid.NewGuid(), HandlerType = typeof(ExemptHandler) };
        var store = new Mock<ICircuitBreakerStore>(MockBehavior.Strict);
        var behavior = Build<ExemptShapedMessage, Unit>(ctx, store.Object);

        var result = await behavior.HandleAsync(
            new ExemptShapedMessage(),
            (req, ct) => Task.FromResult(Unit.Value),
            CancellationToken.None);

        result.ShouldBe(Unit.Value);
        store.VerifyNoOtherCalls();
    }

    [TimedFact]
    public async Task MessageChild_OpenCircuit_ReschedulesAndKeepsHandlerType()
    {
        var ctx = new JobContext { JobId = Guid.NewGuid(), HandlerType = typeof(PlainHandler) };
        var store = new Mock<ICircuitBreakerStore>();
        store
            .Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CircuitBreakerState
            {
                GroupKey = nameof(ExemptShapedMessage),
                State = CircuitState.Open,
                OpenUntil = DateTime.UtcNow.AddMinutes(5),
                FailureCount = 3,
            });
        var behavior = Build<ExemptShapedMessage, Unit>(ctx, store.Object);

        var handlerRan = false;
        var result = await behavior.HandleAsync(
            new ExemptShapedMessage(),
            (req, ct) =>
            {
                handlerRan = true;
                return Task.FromResult(Unit.Value);
            },
            CancellationToken.None);

        result.ShouldBe(default(Unit));
        handlerRan.ShouldBeFalse();
        ctx.Outcome.ShouldNotBeNull();
        ctx.Outcome!.Reason.ShouldBe(OutcomeReason.CircuitBreaker);

        // §8.14: the reschedule must NOT clear HandlerType — for a routed message child it IS the
        // routing decision, and re-discovery would fail (messages have no IJobHandler registration).
        ctx.Outcome.ClearHandlerType.ShouldBeFalse();
    }

    private static CircuitBreakerPipelineBehavior<TRequest, TResponse> Build<TRequest, TResponse>(JobContext ctx, ICircuitBreakerStore store)
        where TRequest : IRequest<TResponse>
    {
        return new CircuitBreakerPipelineBehavior<TRequest, TResponse>(
            ctx,
            Options.Create(new CircuitBreakerOptions()),
            TimeProvider.System,
            store);
    }

    private sealed class ExemptHandler : IPolicyExemptHandler;

    private sealed class ExemptShapedMessage : IMessage;

    private sealed class PlainHandler;
}
