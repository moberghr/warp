using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Handlers;

namespace Warp.Tests.Core;

// The worker runs a job through the generator's typed dispatch, registered by the assembly's module
// initializer, rather than through reflection.
[Trait("Category", "NoDb")]
public sealed class GeneratedJobDispatchTests
{
    [TimedFact]
    public async Task ExecuteJobHandler_HandlerThatThrowsSynchronously_SurfacesItsOwnException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTransient<IJobHandler<SyncThrowingRequest>, SyncThrowingHandler>();
        await using var provider = services.BuildServiceProvider();

        // Act
        var execute = JobDispatcher.ExecuteJobHandler(
            new SyncThrowingRequest(),
            typeof(SyncThrowingRequest),
            typeof(SyncThrowingHandler),
            provider,
            Xunit.TestContext.Current.CancellationToken);

        // Assert: through MethodInfo.Invoke it arrives wrapped in a TargetInvocationException.
        var exception = await Should.ThrowAsync<InvalidOperationException>(execute);
        exception.Message.ShouldBe(SyncThrowingHandler.Message);
    }

    [TimedFact]
    public async Task ExecuteJobHandler_HandlerFails_StackTraceCarriesNoGeneratedPlumbing()
    {
        // Arrange — the trace is stored for every failure (the failure log row and the error-grouping
        // entry). The generated frames carried the job type's full name and the generated file's path:
        // on the failure-path benchmark, ~15% more WAL per failed job than the reflection path.
        var services = new ServiceCollection();
        services.AddTransient<IJobHandler<AsyncThrowingRequest>, AsyncThrowingHandler>();
        await using var provider = services.BuildServiceProvider();

        // Act
        var exception = await Should.ThrowAsync<InvalidOperationException>(JobDispatcher.ExecuteJobHandler(
            new AsyncThrowingRequest(),
            typeof(AsyncThrowingRequest),
            typeof(AsyncThrowingHandler),
            provider,
            Xunit.TestContext.Current.CancellationToken));

        // Assert
        var trace = exception.StackTrace.ShouldNotBeNull();
        trace.ShouldContain(nameof(AsyncThrowingHandler));
        trace.ShouldNotContain(".g.cs", Case.Sensitive);
        trace.ShouldNotContain(nameof(AsyncThrowingRequest) + "(", Case.Sensitive);
    }

    [TimedFact]
    public async Task ExecuteJobHandler_HandlerThrowsSynchronously_TheBehaviourGetsAFaultedTaskFromNext()
    {
        // Arrange: a behaviour that does not await next() (it attaches a continuation, or returns the task).
        // On the reflection path the innermost delegate is async, so a handler that throws before returning
        // its task gives the behaviour a faulted task; the generated path must give it the same.
        var behaviour = new NonAwaitingBehaviour();
        var services = new ServiceCollection();
        services.AddTransient<IJobHandler<SyncThrowingRequest>, SyncThrowingHandler>();
        services.AddSingleton<IPipelineBehavior<SyncThrowingRequest, Unit>>(behaviour);
        await using var provider = services.BuildServiceProvider();

        // Act
        await Should.ThrowAsync<InvalidOperationException>(JobDispatcher.ExecuteJobHandler(
            new SyncThrowingRequest(),
            typeof(SyncThrowingRequest),
            typeof(SyncThrowingHandler),
            provider,
            Xunit.TestContext.Current.CancellationToken));

        // Assert
        behaviour.NextReturnedAFaultedTask.ShouldBeTrue("next() threw into the behaviour instead of returning a faulted task");
    }

    [TimedFact]
    public async Task ExecuteHandler_RowNamesAHandlerOtherThanTheLastRegistered_RunsTheNamedOne()
    {
        // Arrange
        var ran = new HandlerRuns();
        var services = new ServiceCollection();
        services.AddSingleton(ran);
        services.AddTransient<IJobHandler<TwoHandlerRequest>, FirstTwoHandler>();
        services.AddTransient<IJobHandler<TwoHandlerRequest>, SecondTwoHandler>();
        await using var provider = services.BuildServiceProvider();

        // Act
        await JobDispatcher.ExecuteHandler(
            new TwoHandlerRequest(),
            typeof(TwoHandlerRequest),
            typeof(FirstTwoHandler),
            provider,
            Xunit.TestContext.Current.CancellationToken);

        // Assert
        ran.Names.ShouldBe([nameof(FirstTwoHandler)]);
    }
}

public sealed class SyncThrowingRequest : IJob;

public sealed class SyncThrowingHandler : IJobHandler<SyncThrowingRequest>
{
    public const string Message = "thrown before the handler returned a task";

    public Task HandleAsync(SyncThrowingRequest message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Message);
}

public sealed class TwoHandlerRequest : IJob;

public sealed class HandlerRuns
{
    public List<string> Names { get; } = [];
}

public sealed class FirstTwoHandler(HandlerRuns runs) : IJobHandler<TwoHandlerRequest>
{
    public Task HandleAsync(TwoHandlerRequest message, CancellationToken cancellationToken)
    {
        runs.Names.Add(nameof(FirstTwoHandler));

        return Task.CompletedTask;
    }
}

public sealed class SecondTwoHandler(HandlerRuns runs) : IJobHandler<TwoHandlerRequest>
{
    public Task HandleAsync(TwoHandlerRequest message, CancellationToken cancellationToken)
    {
        runs.Names.Add(nameof(SecondTwoHandler));

        return Task.CompletedTask;
    }
}

public sealed class AsyncThrowingRequest : IJob;

public sealed class AsyncThrowingHandler : IJobHandler<AsyncThrowingRequest>
{
    public async Task HandleAsync(AsyncThrowingRequest message, CancellationToken cancellationToken)
    {
        await Task.Yield();

        throw new InvalidOperationException("failed after its first await");
    }
}

public sealed class NonAwaitingBehaviour : IPipelineBehavior<SyncThrowingRequest, Unit>
{
    public bool NextReturnedAFaultedTask { get; private set; }

    public Task<Unit> HandleAsync(SyncThrowingRequest request, RequestHandlerDelegate<SyncThrowingRequest, Unit> next, CancellationToken cancellationToken)
    {
        var handling = next(request, cancellationToken);
        NextReturnedAFaultedTask = handling.IsFaulted;

        return handling;
    }
}
