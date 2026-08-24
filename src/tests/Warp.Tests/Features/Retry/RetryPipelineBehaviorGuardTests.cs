using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.Handlers;
using Warp.Core.Retry;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features.Retry;

/// <summary>
/// SC16 (addon policy axis): dropping the compile-time <c>IJob</c> constraint means DI now composes
/// <see cref="RetryPipelineBehavior{TRequest, TResponse}"/> into every pipeline — the runtime guard
/// is what keeps in-memory sends and policy-exempt handlers (saga proxies) free of retry outcomes.
/// </summary>
[Trait("Category", "NoDb")]
public class RetryPipelineBehaviorGuardTests
{
    [TimedFact]
    public async Task InMemoryRequest_HandlerThrows_NoRetryOutcome()
    {
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        var behavior = Build<GetGreetingRequest, string>(ctx, maxRetries: 5);

        await Should.ThrowAsync<InvalidOperationException>(
            behavior.HandleAsync(
                new GetGreetingRequest { Name = "x" },
                (req, ct) => throw new InvalidOperationException("boom"),
                CancellationToken.None));

        ctx.Outcome.ShouldBeNull();
        ctx.Metadata.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task PolicyExemptHandler_HandlerThrows_NoRetryOutcome()
    {
        // Saga proxies own their busy/version-conflict requeue logic; an outer retry outcome would
        // fight the proxy's own rescheduling.
        var ctx = new JobContext { JobId = Guid.NewGuid(), HandlerType = typeof(ExemptHandler) };
        var behavior = Build<ExemptShapedMessage, Unit>(ctx, maxRetries: 5);

        await Should.ThrowAsync<InvalidOperationException>(
            behavior.HandleAsync(
                new ExemptShapedMessage(),
                (req, ct) => throw new InvalidOperationException("boom"),
                CancellationToken.None));

        ctx.Outcome.ShouldBeNull();
        ctx.Metadata.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task MessageRequest_HandlerThrows_GetsRetryOutcome()
    {
        // The counter-case: an ordinary routed message child IS retried per the global options.
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        var behavior = Build<ExemptShapedMessage, Unit>(ctx, maxRetries: 5);

        await Should.ThrowAsync<InvalidOperationException>(
            behavior.HandleAsync(
                new ExemptShapedMessage(),
                (req, ct) => throw new InvalidOperationException("boom"),
                CancellationToken.None));

        ctx.Outcome.ShouldNotBeNull();

        // §8.14: a routed message child keeps HandlerType on the retry requeue.
        ctx.Outcome!.ClearHandlerType.ShouldBeFalse();
    }

    private static RetryPipelineBehavior<TRequest, TResponse> Build<TRequest, TResponse>(JobContext ctx, int maxRetries)
        where TRequest : IRequest<TResponse>
    {
        return new RetryPipelineBehavior<TRequest, TResponse>(
            ctx,
            Options.Create(new RetryOptions { MaxRetries = maxRetries, Delays = [] }),
            TimeProvider.System);
    }

    private sealed class ExemptHandler : IPolicyExemptHandler;

    private sealed class ExemptShapedMessage : IMessage;
}
