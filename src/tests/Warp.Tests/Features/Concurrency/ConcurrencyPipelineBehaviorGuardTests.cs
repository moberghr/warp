using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features.Concurrency;

/// <summary>
/// The guards are load-bearing since messages joined the policy axes: the behaviour must skip
/// in-memory sends (no job row — a Skip outcome would silently swallow the caller's result) and
/// policy-exempt handlers (saga proxies already hold their own per-correlation mutex). Also pins the
/// handler-axis <c>[Semaphore]</c> stamp — the one arm whose fields differ from the mutex arm.
/// </summary>
[Trait("Category", "NoDb")]
public class ConcurrencyPipelineBehaviorGuardTests
{
    [TimedFact]
    public async Task InMemoryRequest_WithMutexMetadata_PassesThroughWithoutAcquiring()
    {
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        ctx.Metadata["ConcurrencyKey"] = "guard-key";
        var semaphore = new Mock<IWarpSemaphoreProvider>(MockBehavior.Strict);
        var behavior = Build<GetGreetingRequest, string>(ctx, semaphore.Object);

        var result = await behavior.HandleAsync(
            new GetGreetingRequest { Name = "x" },
            (req, ct) => Task.FromResult("ok"),
            CancellationToken.None);

        result.ShouldBe("ok");
        ctx.Outcome.ShouldBeNull();
        semaphore.VerifyNoOtherCalls();
    }

    [TimedFact]
    public async Task InMemorySendOfJobShapedType_NoJobRow_PassesThroughWithoutAcquiring()
    {
        // `mediator.Send(new ReconcileLedger())` where the type is an IJob carrying a contract [Mutex]:
        // there is no row to Skip or requeue, so gating it would turn the caller's result into a silent
        // `default!`. The scoped JobContext of a non-worker scope has no JobId — that is the discriminator.
        var ctx = new JobContext();
        var semaphore = new Mock<IWarpSemaphoreProvider>(MockBehavior.Strict);
        var behavior = Build<ContractMutexJob, Unit>(ctx, semaphore.Object);

        var handlerRan = false;
        var result = await behavior.HandleAsync(
            new ContractMutexJob(),
            (req, ct) =>
            {
                handlerRan = true;
                return Task.FromResult(Unit.Value);
            },
            CancellationToken.None);

        result.ShouldBe(Unit.Value);
        handlerRan.ShouldBeTrue();
        ctx.Outcome.ShouldBeNull();
        ctx.Metadata.ShouldBeEmpty();
        semaphore.VerifyNoOtherCalls();
    }

    [TimedFact]
    public async Task PolicyExemptHandler_WithMutexMetadata_PassesThroughWithoutAcquiring()
    {
        // Saga proxies serialize on their own per-correlation mutex — an outer concurrency slot on
        // top would double-lock the execution.
        var ctx = new JobContext { JobId = Guid.NewGuid(), HandlerType = typeof(ExemptHandler) };
        ctx.Metadata["ConcurrencyKey"] = "guard-key";
        var semaphore = new Mock<IWarpSemaphoreProvider>(MockBehavior.Strict);
        var behavior = Build<ExemptShapedMessage, Unit>(ctx, semaphore.Object);

        var result = await behavior.HandleAsync(
            new ExemptShapedMessage(),
            (req, ct) => Task.FromResult(Unit.Value),
            CancellationToken.None);

        result.ShouldBe(Unit.Value);
        ctx.Outcome.ShouldBeNull();
        semaphore.VerifyNoOtherCalls();
    }

    [TimedFact]
    public async Task HandlerDeclaredSemaphore_StampsTrioAndAcquiresWithDeclaredLimit()
    {
        // The semaphore arm of StampResolvedAttribute is the one whose fields differ from the mutex
        // arm — Limit comes from the attribute, not the fixed 1. All three fields must stamp
        // together and the acquisition must carry the declared limit.
        var ctx = new JobContext { JobId = Guid.NewGuid(), HandlerType = typeof(SemaphoreHandler) };
        var semaphore = new Mock<IWarpSemaphoreProvider>();
        semaphore
            .Setup(x => x.TryAcquireAsync("warp:concurrency:guard-sem", 2, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        var behavior = Build<UnitRequest, Unit>(ctx, semaphore.Object);

        var result = await behavior.HandleAsync(
            new UnitRequest(),
            (req, ct) => Task.FromResult(Unit.Value),
            CancellationToken.None);

        result.ShouldBe(Unit.Value);
        ctx.Outcome.ShouldBeNull();
        ctx.Metadata["ConcurrencyKey"].ShouldBe("guard-sem");
        ctx.Metadata["ConcurrencyLimit"].ShouldBe(2);
        ctx.Metadata["ConcurrencyMode"].ShouldBe(ConcurrencyMode.Wait);
        semaphore.Verify(x => x.TryAcquireAsync("warp:concurrency:guard-sem", 2, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ConcurrencyPipelineBehavior<TRequest, TResponse> Build<TRequest, TResponse>(JobContext ctx, IWarpSemaphoreProvider semaphore)
        where TRequest : IRequest<TResponse>
    {
        var manager = new Mock<IConcurrencyLimitManager>();
        manager.Setup(x => x.GetLimit(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((ConcurrencyLimitInfo?)null);

        return new ConcurrencyPipelineBehavior<TRequest, TResponse>(
            ctx,
            semaphore,
            new ConcurrencyLimitResolver(manager.Object),
            TimeProvider.System);
    }

    private sealed class ExemptHandler : IPolicyExemptHandler;

    private sealed class ExemptShapedMessage : IMessage;

    [Semaphore("guard-sem", 2)]
    private sealed class SemaphoreHandler;

    [Mutex("ledger")]
    private sealed class ContractMutexJob : IJob;
}
