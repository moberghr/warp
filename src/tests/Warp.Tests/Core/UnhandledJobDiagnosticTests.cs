using Microsoft.CodeAnalysis;
using Shouldly;

namespace Warp.Tests.Core;

/// <summary>
/// WARP003 — a job/message contract nothing handles. Publishing one succeeds and the failure only
/// surfaces on a worker ("No handler registered for X"), so the generator, which already knows every
/// contract and every handler, reports it at build time instead.
/// </summary>
/// <remarks>
/// The exemptions carry the weight here: the compilation cannot see an assembly that references it,
/// so a contracts assembly (one declaring no job-family handler of its own) is skipped wholesale,
/// saga messages are handled through <c>ISagaHandler</c> rather than <c>IMessageHandler</c>, and the
/// severity stays Warning so what slips through can be suppressed rather than blocking a build.
/// </remarks>
[Trait("Category", "NoDb")]
public sealed class UnhandledJobDiagnosticTests
{
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Warp.Core.Handlers;
        using Warp.Core.Sagas;

        namespace Worker;

        """;

    [TimedFact]
    public void JobWithoutHandler_IsReported()
    {
        var diagnostics = Run("""
            public sealed class OrphanJob : IJob;

            public sealed class OtherJob : IJob;

            public sealed class OtherJobHandler : IJobHandler<OtherJob>
            {
                public Task HandleAsync(OtherJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP003"]);
        diagnostics[0].GetMessage().ShouldContain("OrphanJob");
        diagnostics[0].GetMessage().ShouldContain("IJobHandler");
    }

    [TimedFact]
    public void UnhandledContract_IsAWarningSoItCanBeSuppressed()
    {
        // A handler living in an assembly that references this one is invisible here, so the check
        // cannot be an error without breaking a legitimate layout it has no way to recognise.
        var diagnostics = Run("""
            public sealed class OrphanJob : IJob;

            public sealed class OtherJob : IJob;

            public sealed class OtherJobHandler : IJobHandler<OtherJob>
            {
                public Task HandleAsync(OtherJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Warning);
    }

    [TimedFact]
    public void MessageWithoutHandler_IsReported()
    {
        var diagnostics = Run("""
            public sealed class OrphanMessage : IMessage;

            public sealed class OtherMessage : IMessage;

            public sealed class OtherMessageHandler : IMessageHandler<OtherMessage>
            {
                public Task HandleAsync(OtherMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP003"]);
        diagnostics[0].GetMessage().ShouldContain("OrphanMessage");
        diagnostics[0].GetMessage().ShouldContain("IMessageHandler");
    }

    [TimedFact]
    public void HandledJobAndMessage_ReportNothing()
    {
        var diagnostics = Run("""
            public sealed class HandledJob : IJob;

            public sealed class HandledJobHandler : IJobHandler<HandledJob>
            {
                public Task HandleAsync(HandledJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public sealed class HandledMessage : IMessage;

            public sealed class HandledMessageHandler : IMessageHandler<HandledMessage>
            {
                public Task HandleAsync(HandledMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void SelfHandlingJob_ReportsNothing()
    {
        // Request and handler are the same type, so the contract is its own entry in the handler map.
        var diagnostics = Run("""
            public sealed class SelfHandlingJob : IJob, IJobHandler<SelfHandlingJob>
            {
                public Task HandleAsync(SelfHandlingJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void ContractsAssemblyDeclaringNoHandler_ReportsNothing()
    {
        // The whole point of a shared-contract layout: these are handled by an assembly that
        // references this one, which this compilation cannot see.
        var diagnostics = Run("""
            public sealed class SharedJob : IJob;

            public sealed class SharedMessage : IMessage;
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void InMemoryRequestHandlerAlone_DoesNotOpenTheGate()
    {
        // An API project declaring in-memory requests plus job contracts it publishes to a worker is
        // still a contracts assembly as far as jobs are concerned.
        var diagnostics = Run("""
            public sealed class PublishedJob : IJob;

            public sealed class Query : IRequest<string>;

            public sealed class QueryHandler : IRequestHandler<Query, string>
            {
                public Task<string> HandleAsync(Query request, CancellationToken cancellationToken) => Task.FromResult("x");
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void ContractInReferencedAssembly_IsNotThisCompilationsToReport()
    {
        var contracts = """
            using Warp.Core.Handlers;

            namespace Contracts;

            public sealed class ReferencedOrphanJob : IJob;
            """;

        var diagnostics = Run(
            """
            public sealed class LocalJob : IJob;

            public sealed class LocalJobHandler : IJobHandler<LocalJob>
            {
                public Task HandleAsync(LocalJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """,
            contracts);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void ReferencedContractHandledLocally_ReportsNothing()
    {
        // The shared-contract layout from the other side: Contracts.dll declares the job, this
        // assembly handles it.
        var contracts = """
            using Warp.Core.Handlers;

            namespace Contracts;

            public sealed class SharedJob : IJob;
            """;

        var diagnostics = Run(
            """
            public sealed class SharedJobHandler : IJobHandler<global::Contracts.SharedJob>
            {
                public Task HandleAsync(global::Contracts.SharedJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """,
            contracts);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void SagaHandledMessage_ReportsNothing()
    {
        // The user's handler implements ISagaHandler, never IMessageHandler — AddSagaHandler registers
        // a generated proxy for it, so the message is absent from the dispatch map but handled.
        var diagnostics = Run("""
            public sealed class OrderSaga : Saga
            {
                public string OrderId { get; set; } = "";
            }

            public sealed class StartOrder : IMessage
            {
                [Correlate]
                public string OrderId { get; set; } = "";
            }

            public sealed class OrderSagaHandler : ISagaHandler<OrderSaga, StartOrder>
            {
                public Task HandleAsync(OrderSaga saga, StartOrder message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void SagaTimeoutMessage_ReportsNothing()
    {
        var diagnostics = Run("""
            public sealed class OrderSaga : Saga
            {
                public string OrderId { get; set; } = "";
            }

            public sealed class OrderDeadline : ITimeoutMessage
            {
                [Correlate]
                public string OrderId { get; set; } = "";

                public TimeSpan Delay => TimeSpan.FromMinutes(10);
            }

            public sealed class OrderSagaHandler : ISagaHandler<OrderSaga, OrderDeadline>
            {
                public Task HandleAsync(OrderSaga saga, OrderDeadline message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void AbstractContract_ReportsNothing()
    {
        var diagnostics = Run("""
            public abstract class JobBase : IJob;

            public sealed class ConcreteJob : JobBase;

            public sealed class ConcreteJobHandler : IJobHandler<ConcreteJob>
            {
                public Task HandleAsync(ConcreteJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void OpenGenericContract_ReportsNothing()
    {
        // The handler map keys a closed type; a declaration keyed on a type parameter cannot be
        // matched against it with any confidence, so generics are left alone rather than guessed at.
        var diagnostics = Run("""
            public sealed class GenericJob<T> : IJob;

            public sealed class ConcreteJob : IJob;

            public sealed class ConcreteJobHandler : IJobHandler<ConcreteJob>
            {
                public Task HandleAsync(ConcreteJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void UnhandledInMemoryRequestAndStream_AreOutOfScope()
    {
        // Both fail on the calling thread at Send/CreateStream, in the caller's own test run. The
        // asymmetry WARP003 exists for is that a job fails later, somewhere else.
        var diagnostics = Run("""
            public sealed class OrphanRequest : IRequest<string>;

            public sealed class OrphanStream : IStreamRequest<string>;

            public sealed class HandledJob : IJob;

            public sealed class HandledJobHandler : IJobHandler<HandledJob>
            {
                public Task HandleAsync(HandledJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [TimedFact]
    public void SeveralUnhandledContracts_AreEachReported()
    {
        var diagnostics = Run("""
            public sealed class FirstOrphan : IJob;

            public sealed class SecondOrphan : IMessage;

            public sealed class HandledJob : IJob;

            public sealed class HandledJobHandler : IJobHandler<HandledJob>
            {
                public Task HandleAsync(HandledJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP003", "WARP003"]);
        diagnostics.Select(x => x.GetMessage()).ShouldContain(x => x.Contains("FirstOrphan", StringComparison.Ordinal));
        diagnostics.Select(x => x.GetMessage()).ShouldContain(x => x.Contains("SecondOrphan", StringComparison.Ordinal));
    }

    [TimedFact]
    public void PartialContract_IsReportedOnce()
    {
        // Each declaration reaches the syntax provider separately, and the two carry different
        // locations, so Roslyn's own dedup does not collapse them.
        var diagnostics = Run("""
            public sealed partial class SplitOrphanJob : IJob;

            public sealed partial class SplitOrphanJob
            {
                public int Value { get; set; }
            }

            public sealed class HandledJob : IJob;

            public sealed class HandledJobHandler : IJobHandler<HandledJob>
            {
                public Task HandleAsync(HandledJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        diagnostics.Select(x => x.Id).ShouldBe(["WARP003"]);
        diagnostics[0].GetMessage().ShouldContain("SplitOrphanJob");
    }

    private static IReadOnlyList<Diagnostic> Run(string source, string? referencedSource = null) =>
        MediatorGeneratorTestHarness.RunAndGetGeneratorDiagnostics(Preamble + source, referencedSource);
}
