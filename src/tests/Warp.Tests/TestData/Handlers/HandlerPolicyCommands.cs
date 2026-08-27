using Warp.Core.Concurrency;
using Warp.Core.Handlers;
using Warp.Core.RateLimit;

namespace Warp.Tests.TestData.Handlers;

// Addon policy axis fixtures: the REQUEST types carry no policy attribute — the policy sits on the
// HANDLER and is resolved at first execution via PolicyResolver, then stamped into metadata.
public class HandlerMutexRequest : IJob;

[Mutex("handler-mutex")]
public class HandlerMutexCommand : IJobHandler<HandlerMutexRequest>
{
    private readonly BarrierSignal _signal;

    public HandlerMutexCommand(BarrierSignal signal)
    {
        _signal = signal;
    }

    public async Task HandleAsync(HandlerMutexRequest message, CancellationToken cancellationToken)
    {
        _signal.Running.Release();
        await _signal.CanFinish.WaitAsync(cancellationToken);
    }
}

public class HandlerRateLimitRequest : IJob;

[RateLimit("handler-rl", count: 1, perSeconds: 3600)]
public class HandlerRateLimitCommand : IJobHandler<HandlerRateLimitRequest>
{
    public Task HandleAsync(HandlerRateLimitRequest message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

// Contract-axis mutex on a type used for RECURRING firings: the scheduler/trigger stages the job row
// directly (Metadata = null, publish pipeline bypassed), so only the execution-side contract rung can
// apply the policy — the regression guard for the recurring-job gap.
[Mutex("recurring-mutex")]
public class RecurringMutexRequest : IJob;

public class RecurringMutexCommand : IJobHandler<RecurringMutexRequest>
{
    private readonly BarrierSignal _signal;

    public RecurringMutexCommand(BarrierSignal signal)
    {
        _signal = signal;
    }

    public async Task HandleAsync(RecurringMutexRequest message, CancellationToken cancellationToken)
    {
        _signal.Running.Release();
        await _signal.CanFinish.WaitAsync(cancellationToken);
    }
}

// Both axes on one pair, cross-family: a handler [Semaphore] must beat a contract [Mutex].
[Mutex("both-axes-contract")]
public class BothAxesMutexRequest : IJob;

[Semaphore("both-axes-handler", 1)]
public class BothAxesMutexCommand : IJobHandler<BothAxesMutexRequest>
{
    public Task HandleAsync(BothAxesMutexRequest message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
