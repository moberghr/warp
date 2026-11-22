using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

public class IgnoresCancellationRequest : IJob;

/// <summary>
/// Parks on the barrier WITHOUT observing its token, then returns normally: the handler that is asked
/// to cancel and finishes its work anyway.
/// </summary>
public class IgnoresCancellationCommand : IJobHandler<IgnoresCancellationRequest>
{
    private readonly BarrierSignal _signal;

    public IgnoresCancellationCommand(BarrierSignal signal) => _signal = signal;

    public async Task HandleAsync(IgnoresCancellationRequest message, CancellationToken cancellationToken)
    {
        _signal.Running.Release();
        await _signal.CanFinish.WaitAsync(CancellationToken.None);
    }
}

public class ThrowsOwnCancellationRequest : IJob;

/// <summary>
/// Throws an <see cref="OperationCanceledException"/> nobody asked for (an inner timeout, a cancelled
/// HTTP call). Nothing cancelled the job, so this is a failure, not a cancellation.
/// </summary>
public class ThrowsOwnCancellationCommand : IJobHandler<ThrowsOwnCancellationRequest>
{
    public Task HandleAsync(ThrowsOwnCancellationRequest message, CancellationToken cancellationToken) =>
        throw new OperationCanceledException("The handler gave up on its own");
}
