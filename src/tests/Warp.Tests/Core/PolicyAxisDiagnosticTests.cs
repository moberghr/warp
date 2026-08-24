using Microsoft.CodeAnalysis;
using Shouldly;

namespace Warp.Tests.Core;

/// <summary>
/// Build-time half of the addon policy-axis validation (<c>PolicyAxisValidator</c>, WARP001-003).
/// The runtime half is covered by <see cref="AddonAttributeHandlerValidationTests"/> and is NOT
/// replaced — these two suites overlap deliberately, because the runtime check is still the backstop
/// for handlers the generator cannot see.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class PolicyAxisDiagnosticTests
{
    private const string Preamble = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Warp.Core.CircuitBreaker;
        using Warp.Core.Concurrency;
        using Warp.Core.Handlers;
        using Warp.Core.RateLimit;
        using Warp.Core.Timeout;

        namespace Worker;

        """;

    [TimedFact]
    public void SameAttributeOnContractAndHandler_ReportsBothAxesConflict()
    {
        var diagnostics = Run("""
            [Mutex("k")]
            public sealed class MutexJob : IJob;

            [Mutex("k")]
            public sealed class MutexJobHandler : IJobHandler<MutexJob>
            {
                public Task HandleAsync(MutexJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP001"]);

        // Both ends of the conflict must be named — the whole point is that the reader can find the
        // other declaration, which by definition is in a different file.
        var message = diagnostics[0].GetMessage();
        message.ShouldContain("MutexJob");
        message.ShouldContain("MutexJobHandler");
    }

    [TimedFact]
    public void MutexOnContractAndSemaphoreOnHandler_ReportsOneFamilyConflict()
    {
        // Cross-attribute, same family: both write the IConcurrencyMetadata slot, so the contract
        // [Mutex] would silently shadow the handler [Semaphore]. One conflict, not two.
        var diagnostics = Run("""
            [Mutex("k")]
            public sealed class FamilyJob : IJob;

            [Semaphore("k", 3)]
            public sealed class FamilyJobHandler : IJobHandler<FamilyJob>
            {
                public Task HandleAsync(FamilyJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP001"]);
        diagnostics[0].GetMessage().ShouldContain("Mutex/Semaphore");
    }

    [TimedFact]
    public void RetryOnContractAndHandler_ReportsBothAxesConflict()
    {
        // Retry's handler side was legal-but-dead before the policy axis; it now joins the conflict
        // check even though it is NOT rejected on unsupported shapes.
        var diagnostics = Run("""
            [Retry(2)]
            public sealed class RetryJob : IJob;

            [Retry(3)]
            public sealed class RetryJobHandler : IJobHandler<RetryJob>
            {
                public Task HandleAsync(RetryJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP001"]);
    }

    [TimedFact]
    public void ContractInReferencedAssembly_StillDetectsConflict()
    {
        // The contract's attribute is read from METADATA, not syntax — the shared-contracts layout
        // (Contracts.dll + Worker.dll) is exactly where a shadowed handler policy is hardest to spot.
        const string contracts = """
            using Warp.Core.Concurrency;
            using Warp.Core.Handlers;

            namespace Contracts;

            [Mutex("k")]
            public sealed class RemoteJob : IJob;
            """;

        var diagnostics = Run(
            """
            using Contracts;

            [Mutex("k")]
            public sealed class RemoteJobHandler : IJobHandler<RemoteJob>
            {
                public Task HandleAsync(RemoteJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """,
            contracts);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP001"]);
    }

    [TimedFact]
    public void MutexOnInMemoryRequestHandler_ReportsUnsupportedShape()
    {
        // The #242 failure mode: the policy behaviours early-return for a non-job-backed request, so
        // the attribute is dead code wherever it sits on this handler.
        var diagnostics = Run("""
            public sealed class PlainRequest : IRequest<string>;

            [Mutex("k")]
            public sealed class PlainRequestHandler : IRequestHandler<PlainRequest, string>
            {
                public Task<string> HandleAsync(PlainRequest request, CancellationToken cancellationToken) => Task.FromResult("x");
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP002"]);
        diagnostics[0].GetMessage().ShouldContain("[Mutex]");
    }

    [TimedFact]
    public void RateLimitOnStreamHandler_ReportsUnsupportedShape()
    {
        var diagnostics = Run("""
            public sealed class Stream : IStreamRequest<string>;

            [RateLimit("k", count: 1, perSeconds: 60)]
            public sealed class StreamHandler : IStreamRequestHandler<Stream, string>
            {
                public IAsyncEnumerable<string> HandleAsync(Stream request, CancellationToken cancellationToken) => null!;
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP002"]);
    }

    [TimedFact]
    public void RetryOnInMemoryRequestHandler_IsTolerated()
    {
        // Retry and CircuitBreaker have always been tolerated (as dead code) on non-job handlers.
        // Rejecting them here now would be an unspecced breaking change — the runtime table says the
        // same thing, and this test is what keeps the two tables in step.
        var diagnostics = Run("""
            public sealed class TolerantRequest : IRequest<string>;

            [Retry(3)]
            [CircuitBreaker(Threshold = 3, DurationSeconds = 30)]
            public sealed class TolerantRequestHandler : IRequestHandler<TolerantRequest, string>
            {
                public Task<string> HandleAsync(TolerantRequest request, CancellationToken cancellationToken) => Task.FromResult("x");
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void TotalScopedTimeoutOnHandler_ReportsTotalScopeError()
    {
        // A Total budget is measured from enqueue, so its deadline must be stamped at publish — before
        // any handler is known.
        var diagnostics = Run("""
            public sealed class TotalJob : IJob;

            [Timeout(30, Scope = TimeoutScope.Total)]
            public sealed class TotalJobHandler : IJobHandler<TotalJob>
            {
                public Task HandleAsync(TotalJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP003"]);
    }

    [TimedFact]
    public void PerAttemptTimeoutOnHandler_IsAccepted()
    {
        var diagnostics = Run("""
            public sealed class PerAttemptJob : IJob;

            [Timeout(30)]
            public sealed class PerAttemptJobHandler : IJobHandler<PerAttemptJob>
            {
                public Task HandleAsync(PerAttemptJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void PolicyOnHandlerAxisAlone_IsAccepted()
    {
        // The shape the policy axis exists to allow: the handler is the code touching the resource.
        var diagnostics = Run("""
            public sealed class CleanJob : IJob;

            public sealed class CleanMessage : IMessage;

            [Mutex("k")]
            public sealed class CleanJobHandler : IJobHandler<CleanJob>
            {
                public Task HandleAsync(CleanJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            [RateLimit("k", count: 1, perSeconds: 60)]
            public sealed class CleanMessageHandler : IMessageHandler<CleanMessage>
            {
                public Task HandleAsync(CleanMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void SelfHandlingJobWithPolicy_IsExempt()
    {
        // Handler type == request type: there is only one axis, so there is nothing to shadow.
        var diagnostics = Run("""
            [Mutex("k")]
            public sealed class SelfHandling : IJob, IJobHandler<SelfHandling>
            {
                public Task HandleAsync(SelfHandling message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    private static IReadOnlyList<Diagnostic> Run(string source, string? referencedSource = null) =>
        MediatorGeneratorTestHarness.RunAndGetGeneratorDiagnostics(Preamble + source, referencedSource);
}
