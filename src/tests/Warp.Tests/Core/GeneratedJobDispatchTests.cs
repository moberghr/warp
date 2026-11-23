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
