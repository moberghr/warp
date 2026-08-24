using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Handlers;
using Warp.Core.RateLimit;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features.RateLimit;

/// <summary>
/// Guard coverage for the widened rate-limit behaviour: in-memory sends and policy-exempt handlers
/// (saga proxies, which own their busy/version-conflict reschedules) must pass through without
/// touching the lock provider or the store — a Skip outcome here would silently swallow the caller's
/// result.
/// </summary>
[Trait("Category", "NoDb")]
public class RateLimitPipelineBehaviorGuardTests
{
    [TimedFact]
    public async Task InMemoryRequest_WithRateLimitMetadata_PassesThroughWithoutLocking()
    {
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        ctx.Metadata["RateLimitKey"] = "guard-rl";
        ctx.Metadata["RateLimitCount"] = 1;
        ctx.Metadata["RateLimitWindowSeconds"] = 60;
        var lockProvider = new Mock<IWarpLockProvider>(MockBehavior.Strict);
        var behavior = Build<GetGreetingRequest, string>(ctx, lockProvider.Object);

        var result = await behavior.HandleAsync(
            new GetGreetingRequest { Name = "x" },
            (req, ct) => Task.FromResult("ok"),
            CancellationToken.None);

        result.ShouldBe("ok");
        ctx.Outcome.ShouldBeNull();
        lockProvider.VerifyNoOtherCalls();
    }

    [TimedFact]
    public async Task PolicyExemptHandler_WithRateLimitMetadata_PassesThroughWithoutLocking()
    {
        var ctx = new JobContext { JobId = Guid.NewGuid(), HandlerType = typeof(ExemptHandler) };
        ctx.Metadata["RateLimitKey"] = "guard-rl";
        ctx.Metadata["RateLimitCount"] = 1;
        ctx.Metadata["RateLimitWindowSeconds"] = 60;
        var lockProvider = new Mock<IWarpLockProvider>(MockBehavior.Strict);
        var behavior = Build<ExemptShapedMessage, Unit>(ctx, lockProvider.Object);

        var result = await behavior.HandleAsync(
            new ExemptShapedMessage(),
            (req, ct) => Task.FromResult(Unit.Value),
            CancellationToken.None);

        result.ShouldBe(Unit.Value);
        ctx.Outcome.ShouldBeNull();
        lockProvider.VerifyNoOtherCalls();
    }

    private static RateLimitPipelineBehavior<TRequest, TResponse> Build<TRequest, TResponse>(JobContext ctx, IWarpLockProvider lockProvider)
        where TRequest : IRequest<TResponse>
    {
        var manager = new Mock<IRateLimitManager>();
        manager.Setup(x => x.GetLimit(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((RateLimitInfo?)null);

        return new RateLimitPipelineBehavior<TRequest, TResponse>(
            ctx,
            lockProvider,
            Mock.Of<IRateLimitStore>(),
            new RateLimitResolver(manager.Object),
            TimeProvider.System);
    }

    private sealed class ExemptHandler : IPolicyExemptHandler;

    private sealed class ExemptShapedMessage : IMessage;
}
