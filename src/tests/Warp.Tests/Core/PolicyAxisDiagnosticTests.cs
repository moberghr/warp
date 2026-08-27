using Microsoft.CodeAnalysis;
using Shouldly;

namespace Warp.Tests.Core;

/// <summary>
/// Build-time half of the policy axis (§8.8): WARP001 (policy on a handler shape that never reaches a
/// policy behaviour) and WARP002 (Total-scoped timeout on a handler). Handlers outside the compilation
/// are <see cref="PolicyResolverTests"/>'s backstop, which fails the job rather than the process.
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
    public void SameFamilyOnBothAxes_IsAccepted()
    {
        // Used to be a build error: the contract value was stamped at publish and shadowed the handler.
        var diagnostics = Run("""
            [Mutex("contract")]
            public sealed class BothAxesJob : IJob;

            [Semaphore("handler", 3)]
            public sealed class BothAxesJobHandler : IJobHandler<BothAxesJob>
            {
                public Task HandleAsync(BothAxesJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void MutexOnInMemoryRequestHandler_ReportsUnsupportedShape()
    {
        var diagnostics = Run("""
            public sealed class PlainRequest : IRequest<string>;

            [Mutex("k")]
            public sealed class PlainRequestHandler : IRequestHandler<PlainRequest, string>
            {
                public Task<string> HandleAsync(PlainRequest request, CancellationToken cancellationToken) => Task.FromResult("x");
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP001"]);
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

        diagnostics.Select(x => x.Id).ShouldBe(["WARP001"]);
    }

    [TimedFact]
    public void RetryOnInMemoryRequestHandler_IsTolerated()
    {
        // Retry/CircuitBreaker have always been tolerated (dead) on non-job shapes; rejecting them
        // there now would be an unspecced break.
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
        var diagnostics = Run("""
            public sealed class TotalJob : IJob;

            [Timeout(30, Scope = TimeoutScope.Total)]
            public sealed class TotalJobHandler : IJobHandler<TotalJob>
            {
                public Task HandleAsync(TotalJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP002"]);
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
    public void PolicyOnHandlerServingBothAJobAndAStream_IsAccepted()
    {
        // The attribute IS honoured for the job half, so the stream half must not fail the build.
        var diagnostics = Run("""
            public sealed class MixedJob : IJob;

            public sealed class MixedStream : IStreamRequest<string>;

            [Mutex("k")]
            public sealed class MixedHandler : IJobHandler<MixedJob>, IStreamRequestHandler<MixedStream, string>
            {
                public Task HandleAsync(MixedJob message, CancellationToken cancellationToken) => Task.CompletedTask;

                public IAsyncEnumerable<string> HandleAsync(MixedStream request, CancellationToken cancellationToken) => null!;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void PolicyOnHandlerServingTwoUnsupportedShapes_ReportsOnce()
    {
        // The same attribute syntax must not produce one diagnostic per unsupported pair.
        var diagnostics = Run("""
            public sealed class FirstPlain : IRequest<string>;

            public sealed class SecondPlain : IRequest<string>;

            [Mutex("k")]
            public sealed class TwoPlainHandler : IRequestHandler<FirstPlain, string>, IRequestHandler<SecondPlain, string>
            {
                public Task<string> HandleAsync(FirstPlain request, CancellationToken cancellationToken) => Task.FromResult("x");

                public Task<string> HandleAsync(SecondPlain request, CancellationToken cancellationToken) => Task.FromResult("x");
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP001"]);
    }

    [TimedFact]
    public void TotalScopedTimeoutOnHandlerServingTwoJobs_ReportsOnce()
    {
        var diagnostics = Run("""
            public sealed class FirstTotalJob : IJob;

            public sealed class SecondTotalJob : IJob;

            [Timeout(30, Scope = TimeoutScope.Total)]
            public sealed class TwoJobHandler : IJobHandler<FirstTotalJob>, IJobHandler<SecondTotalJob>
            {
                public Task HandleAsync(FirstTotalJob message, CancellationToken cancellationToken) => Task.CompletedTask;

                public Task HandleAsync(SecondTotalJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP002"]);
    }

    [TimedFact]
    public void SelfHandlingJobWithPolicy_IsExempt()
    {
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
