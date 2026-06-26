using Shouldly;

namespace Warp.Tests.Core;

/// <summary>
/// Generator-level coverage for <c>WarpMediatorGenerator</c>'s handler-driven discovery. Asserts on
/// the emitted C# directly (deterministic, no DB), complementing the runtime end-to-end checks in
/// <see cref="CrossAssemblyHandlerTests"/>.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class MediatorGeneratorCrossAssemblyTests
{
    private const string ContractsSource = """
        using Warp.Core.Handlers;

        namespace Contracts;

        public sealed class FooMessage : IMessage { }
        """;

    [TimedFact]
    public void HandlerForReferencedAssemblyContract_EmitsRegistrationAndDispatch()
    {
        const string workerSource = """
            using System.Threading;
            using System.Threading.Tasks;
            using Contracts;
            using Warp.Core.Handlers;

            namespace Worker;

            public sealed class FooHandler : IMessageHandler<FooMessage>
            {
                public Task HandleAsync(FooMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var generated = MediatorGeneratorTestHarness.RunAndConcatGeneratedSources(workerSource, ContractsSource);

        // DI registration keyed by the referenced-assembly message type (the fix).
        generated.ShouldContain(
            "services.AddTransient<global::Warp.Core.Handlers.IMessageHandler<global::Contracts.FooMessage>, global::Worker.FooHandler>();");

        // Dispatch entry — without it routing still reports "No handlers registered" (§detail 2).
        generated.ShouldContain("messageType == typeof(global::Contracts.FooMessage)");
    }

    [TimedFact]
    public void CoLocatedHandler_RegistersExactlyOnce()
    {
        const string workerSource = """
            using System.Threading;
            using System.Threading.Tasks;
            using Warp.Core.Handlers;

            namespace Worker;

            public sealed class LocalMessage : IMessage { }

            public sealed class LocalHandler : IMessageHandler<LocalMessage>
            {
                public Task HandleAsync(LocalMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var generated = MediatorGeneratorTestHarness.RunAndConcatGeneratedSources(workerSource);

        // The both-local case is found by the message-driven pass; the handler-driven pass must
        // dedup it rather than emit a second registration (which would fire pub/sub twice).
        var registration = "services.AddTransient<global::Warp.Core.Handlers.IMessageHandler<global::Worker.LocalMessage>, global::Worker.LocalHandler>();";
        var occurrences = generated.Split([registration], StringSplitOptions.None).Length - 1;

        occurrences.ShouldBe(1);
    }

    [TimedFact]
    public void HandlerDeclaredInReferencedAssembly_IsNotReRegisteredByConsumer()
    {
        const string contractsWithHandler = """
            using System.Threading;
            using System.Threading.Tasks;
            using Warp.Core.Handlers;

            namespace Contracts;

            public sealed class BarMessage : IMessage { }

            public sealed class BarHandler : IMessageHandler<BarMessage>
            {
                public Task HandleAsync(BarMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        // The consumer declares its own handler (so it emits a generated file) and references the
        // assembly that owns BarHandler. The consumer's generator must register only its own handler.
        const string workerSource = """
            using System.Threading;
            using System.Threading.Tasks;
            using Warp.Core.Handlers;

            namespace Worker;

            public sealed class WorkerMessage : IMessage { }

            public sealed class WorkerHandler : IMessageHandler<WorkerMessage>
            {
                public Task HandleAsync(WorkerMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var generated = MediatorGeneratorTestHarness.RunAndConcatGeneratedSources(workerSource, contractsWithHandler);

        generated.Contains("global::Worker.WorkerHandler", StringComparison.Ordinal)
            .ShouldBeTrue("consumer registers its own handler");
        generated.Contains("global::Contracts.BarHandler", StringComparison.Ordinal)
            .ShouldBeFalse("a handler owned by a referenced assembly must not be re-registered by the consumer");
    }

    // The fix is applied symmetrically across all four handler interfaces (§"Apply symmetrically").
    // The IMessage path is covered above and end-to-end in CrossAssemblyHandlerTests; the remaining
    // three kinds are pinned here so a future regression in any one branch is caught.
    [TimedFact]
    public void JobHandlerForReferencedContract_EmitsRegistrationAndDispatch()
    {
        const string contractsSource = """
            using Warp.Core.Handlers;

            namespace Contracts;

            public sealed class FooJob : IJob { }
            """;

        const string workerSource = """
            using System.Threading;
            using System.Threading.Tasks;
            using Contracts;
            using Warp.Core.Handlers;

            namespace Worker;

            public sealed class FooJobHandler : IJobHandler<FooJob>
            {
                public Task HandleAsync(FooJob message, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var generated = MediatorGeneratorTestHarness.RunAndConcatGeneratedSources(workerSource, contractsSource);

        generated.ShouldContain(
            "services.AddTransient<global::Warp.Core.Handlers.IJobHandler<global::Contracts.FooJob>, global::Worker.FooJobHandler>();");
        generated.ShouldContain("messageType == typeof(global::Contracts.FooJob)");
    }

    [TimedFact]
    public void RequestHandlerForReferencedContract_EmitsRegistrationAndMediator()
    {
        const string contractsSource = """
            using Warp.Core.Handlers;

            namespace Contracts;

            public sealed class FooQuery : IRequest<string> { }
            """;

        const string workerSource = """
            using System.Threading;
            using System.Threading.Tasks;
            using Contracts;
            using Warp.Core.Handlers;

            namespace Worker;

            public sealed class FooQueryHandler : IRequestHandler<FooQuery, string>
            {
                public Task<string> HandleAsync(FooQuery request, CancellationToken cancellationToken) => Task.FromResult("ok");
            }
            """;

        var generated = MediatorGeneratorTestHarness.RunAndConcatGeneratedSources(workerSource, contractsSource);

        generated.Contains("IRequestHandler<global::Contracts.FooQuery,", StringComparison.Ordinal)
            .ShouldBeTrue("request handler is registered for the referenced-assembly request");
        generated.Contains("global::Worker.FooQueryHandler", StringComparison.Ordinal)
            .ShouldBeTrue("the consumer's handler is the registered implementation");
        generated.ShouldContain("services.AddScoped<global::Warp.Core.Handlers.IMediator, GeneratedMediator>();");
    }

    [TimedFact]
    public void StreamRequestHandlerForReferencedContract_EmitsRegistration()
    {
        const string contractsSource = """
            using System.Collections.Generic;
            using Warp.Core.Handlers;

            namespace Contracts;

            public sealed class FooStream : IStreamRequest<int> { }
            """;

        const string workerSource = """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using Contracts;
            using Warp.Core.Handlers;

            namespace Worker;

            public sealed class FooStreamHandler : IStreamRequestHandler<FooStream, int>
            {
                public IAsyncEnumerable<int> HandleAsync(FooStream request, CancellationToken cancellationToken) => throw new NotImplementedException();
            }
            """;

        var generated = MediatorGeneratorTestHarness.RunAndConcatGeneratedSources(workerSource, contractsSource);

        generated.Contains("IStreamRequestHandler<global::Contracts.FooStream, int>", StringComparison.Ordinal)
            .ShouldBeTrue("stream request handler is registered for the referenced-assembly request");
        generated.Contains("global::Worker.FooStreamHandler", StringComparison.Ordinal)
            .ShouldBeTrue("the consumer's handler is the registered implementation");
    }

    [TimedFact]
    public void RequestTypesSharingASimpleNameAcrossAssemblies_DoNotCollideInGeneratedMembers()
    {
        // A local request and a referenced request both named "Ping" both land in the consumer's
        // GeneratedMediator. Generated member names (wrapper fields / dispatch methods) must be
        // derived from the full name, not the simple name — otherwise both emit `_wrapper_Ping`
        // and the generated mediator fails to compile with CS0102 / CS0111.
        const string contractsSource = """
            using Warp.Core.Handlers;

            namespace Contracts;

            public sealed class Ping : IRequest<string> { }
            """;

        const string workerSource = """
            using System.Threading;
            using System.Threading.Tasks;
            using Warp.Core.Handlers;

            namespace Worker;

            public sealed class Ping : IRequest<string> { }

            public sealed class LocalPingHandler : IRequestHandler<Ping, string>
            {
                public Task<string> HandleAsync(Ping request, CancellationToken cancellationToken) => Task.FromResult("local");
            }

            public sealed class ContractsPingHandler : IRequestHandler<global::Contracts.Ping, string>
            {
                public Task<string> HandleAsync(global::Contracts.Ping request, CancellationToken cancellationToken) => Task.FromResult("contracts");
            }
            """;

        var errors = MediatorGeneratorTestHarness.RunAndGetCompilationErrors(workerSource, contractsSource);

        var duplicateMemberErrors = errors
            .Where(x => string.Equals(x.Id, "CS0102", StringComparison.Ordinal)
                || string.Equals(x.Id, "CS0111", StringComparison.Ordinal))
            .ToList();

        duplicateMemberErrors.ShouldBeEmpty(
            $"generated mediator has duplicate members: {string.Join("; ", duplicateMemberErrors.Select(x => x.GetMessage()))}");
    }
}
