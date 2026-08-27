using Warp.Core.Handlers;
using Warp.Core.Timeout;

namespace Warp.Tests.TestData.Handlers;

[Timeout(seconds: 30)]
public class TimeoutAttributeRequest : IJob;

[Timeout(seconds: 60, Mode = TimeoutMode.Fail)]
public class TimeoutFailModeRequest : IJob;

[Timeout(seconds: 60, Mode = TimeoutMode.Fail, Scope = TimeoutScope.Total)]
public class TimeoutTotalScopeRequest : IJob;

[Timeout(seconds: 99)]
public abstract class TimeoutBaseRequest : IJob;

public class TimeoutDerivedWithoutAttributeRequest : TimeoutBaseRequest;

public class TimeoutDerivedWithoutAttributeCommand : IJobHandler<TimeoutDerivedWithoutAttributeRequest>
{
    public Task HandleAsync(TimeoutDerivedWithoutAttributeRequest message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public class TimeoutAttributeCommand : IJobHandler<TimeoutAttributeRequest>
{
    public Task HandleAsync(TimeoutAttributeRequest message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public class TimeoutFailModeCommand : IJobHandler<TimeoutFailModeRequest>
{
    public Task HandleAsync(TimeoutFailModeRequest message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public class TimeoutTotalScopeCommand : IJobHandler<TimeoutTotalScopeRequest>
{
    public Task HandleAsync(TimeoutTotalScopeRequest message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

// Handler-axis timeout: the REQUEST carries no attribute; the HANDLER declares [Timeout(60)].
// Resolved at first execution via AddonAttributeResolver and stamped into metadata (addon policy axis).
public class HandlerTimeoutRequest : IJob;

[Timeout(seconds: 60)]
public class HandlerTimeoutCommand : IJobHandler<HandlerTimeoutRequest>
{
    public async Task HandleAsync(HandlerTimeoutRequest message, CancellationToken cancellationToken)
    {
        // Simulate long-running work that respects cancellation (the timeout token cancels it)
        await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken);
    }
}

// Handler-axis timeout on a handler that THROWS: the pipeline sets no outcome, so the job fails with
// the stamp still expected on the row (§8.8 failure path).
public class HandlerTimeoutThrowingRequest : IJob;

[Timeout(seconds: 60)]
public class HandlerTimeoutThrowingCommand : IJobHandler<HandlerTimeoutThrowingRequest>
{
    public Task HandleAsync(HandlerTimeoutThrowingRequest message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Always fails");
}

// Recurring firings with a contract Total-scoped timeout: the scheduler stages the row directly
// (Metadata = null), so no publish-time deadline exists and the execution-side resolver must refuse
// the attribute rather than invent a differently-anchored deadline.
[Timeout(seconds: 30, Scope = TimeoutScope.Total)]
public class RecurringTotalTimeoutRequest : IJob;

public class RecurringTotalTimeoutCommand : IJobHandler<RecurringTotalTimeoutRequest>
{
    public Task HandleAsync(RecurringTotalTimeoutRequest message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
