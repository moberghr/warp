using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.ErrorGrouping;
using Warp.Core.Handlers;
using Warp.Tests.Core;
using Warp.Worker.Logging;

namespace Warp.Tests.ErrorGrouping;

[Trait("Category", "NoDb")]
public class HandlerStackTraceTests
{
    // What 8.0 stored for one failed attempt of the retry benchmark before the trim (paths shortened).
    private const string Stored =
        "System.InvalidOperationException: This is from ThrowExceptionCommand\n"
        + "   at Warp.Core.Handlers.ThrowExceptionCommand.HandleAsync(ThrowExceptionRequest message, CancellationToken cancellationToken) in /src/ThrowExceptionCommand.cs:line 14\n"
        + "   at Warp.Core.Handlers.Generated.GeneratedJobDispatcher.<>c__DisplayClass31_1.<Execute_Warp_Core_Handlers_ThrowExceptionRequest>b__1(ThrowExceptionRequest req, CancellationToken ct)\n"
        + "--- End of stack trace from previous location ---\n"
        + "   at Warp.Core.Handlers.TimingPipelineBehavior`2.HandleAsync(T message, RequestHandlerDelegate`2 next, CancellationToken cancellationToken) in /src/LoggingPipelineBehavior.cs:line 20\n"
        + "   at Warp.Core.Retry.RetryPipelineBehavior`2.HandleAsync(TRequest request, RequestHandlerDelegate`2 next, CancellationToken cancellationToken) in /src/RetryPipelineBehavior.cs:line 36\n"
        + "   at Warp.Worker.WarpWorkerService`1.ExecuteJob(Job job, IServiceProvider provider, CancellationToken cancellationToken) in /src/WarpWorkerService.cs:line 576";

    [Fact]
    public void Trim_TraceThroughTheGeneratedDispatch_EndsAtTheHandler()
    {
        // Act
        var trimmed = HandlerStackTrace.Trim(Stored);

        // Assert
        trimmed.ShouldBe(
            "System.InvalidOperationException: This is from ThrowExceptionCommand\n"
            + "   at Warp.Core.Handlers.ThrowExceptionCommand.HandleAsync(ThrowExceptionRequest message, CancellationToken cancellationToken) in /src/ThrowExceptionCommand.cs:line 14");
    }

    [Fact]
    public void Trim_InnerExceptionThroughTheDispatch_KeepsTheOuterExceptionsOwnFrames()
    {
        // Arrange: a behaviour wrapped the handler's exception; each exception's frames end at its own dispatcher frame.
        var trace =
            "System.ApplicationException: wrapped\n"
            + " ---> System.InvalidOperationException: inner\n"
            + "   at App.Handler.HandleAsync(Request message, CancellationToken cancellationToken)\n"
            + "   at Warp.Core.Handlers.Generated.GeneratedJobDispatcher.AsUnit(Task handling)\n"
            + "   at App.WrappingBehaviour.HandleAsync(Request request, RequestHandlerDelegate`2 next, CancellationToken cancellationToken)\n"
            + "   --- End of inner exception stack trace ---\n"
            + "   at App.WrappingBehaviour.HandleAsync(Request request, RequestHandlerDelegate`2 next, CancellationToken cancellationToken)\n"
            + "   at Warp.Worker.WarpWorkerService`1.ExecuteJob(Job job, IServiceProvider provider, CancellationToken cancellationToken)";

        // Act
        var trimmed = HandlerStackTrace.Trim(trace);

        // Assert
        trimmed.ShouldBe(
            "System.ApplicationException: wrapped\n"
            + " ---> System.InvalidOperationException: inner\n"
            + "   at App.Handler.HandleAsync(Request message, CancellationToken cancellationToken)\n"
            + "   --- End of inner exception stack trace ---\n"
            + "   at App.WrappingBehaviour.HandleAsync(Request request, RequestHandlerDelegate`2 next, CancellationToken cancellationToken)\n"
            + "   at Warp.Worker.WarpWorkerService`1.ExecuteJob(Job job, IServiceProvider provider, CancellationToken cancellationToken)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("System.InvalidOperationException: no frames")]
    [InlineData("System.InvalidOperationException: reflection path\n   at App.Handler.HandleAsync(Request message)\n   at InvokeStub_Handler.HandleAsync(Object, Span`1)")]
    public void Trim_TraceWithoutAGeneratedFrame_IsReturnedAsItIs(string? trace)
    {
        HandlerStackTrace.Trim(trace).ShouldBe(trace);
    }

    [TimedFact]
    public async Task Build_FailureThroughAPipelineBehaviour_StoresNoFrameBelowTheHandler()
    {
        // Arrange: a real failure through the generated dispatch with a behaviour in the pipeline and a handler
        // that throws synchronously, the shape the retry benchmark stored 1,302 characters for (529 on 7.7).
        var services = new ServiceCollection();
        services.AddTransient<IJobHandler<SyncThrowingRequest>, SyncThrowingHandler>();
        services.AddTransient<IPipelineBehavior<SyncThrowingRequest, Unit>, AwaitingBehaviour>();
        await using var provider = services.BuildServiceProvider();
        var error = await Should.ThrowAsync<InvalidOperationException>(JobDispatcher.ExecuteJobHandler(
            new SyncThrowingRequest(),
            typeof(SyncThrowingRequest),
            typeof(SyncThrowingHandler),
            provider,
            Xunit.TestContext.Current.CancellationToken));
        error.ToString().ShouldContain(nameof(AwaitingBehaviour));
        var job = new Job { Id = Guid.NewGuid(), CurrentState = State.Failed };

        // Act
        var log = FinalizationLogs.Build(job, error, 1, Guid.NewGuid(), DateTime.UtcNow, outcome: null).Single();
        var occurrence = ErrorOccurrenceFactory.FromException(ErrorSource.Job, error, "culprit", traceId: null, application: null, DateTime.UtcNow);

        // Assert
        foreach (var stored in new[] { log.Exception.ShouldNotBeNull(), occurrence.Stack.ShouldNotBeNull() })
        {
            stored.ShouldContain(nameof(SyncThrowingHandler));
            stored.ShouldNotContain("GeneratedJobDispatcher");
            stored.ShouldNotContain(nameof(AwaitingBehaviour));
        }
    }
}

public sealed class AwaitingBehaviour : IPipelineBehavior<SyncThrowingRequest, Unit>
{
    public async Task<Unit> HandleAsync(SyncThrowingRequest request, RequestHandlerDelegate<SyncThrowingRequest, Unit> next, CancellationToken cancellationToken)
    {
        return await next(request, cancellationToken);
    }
}
