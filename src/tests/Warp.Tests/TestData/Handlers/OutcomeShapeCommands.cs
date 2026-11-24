using System.Collections.Concurrent;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

// The outcomes Warp's own addons hand a worker, produced directly so a test can drive the worker's
// outcome handling without booting the addon and arranging contention for it.
public class RequeueOnceWithReasonRequest : IJob;

/// <summary>
/// First execution: requeues itself at once with a reason and the handler type cleared, the shape a
/// Wait-mode mutex or semaphore produces. Every later execution completes.
/// </summary>
/// <remarks>Attempts are tracked in a static set keyed by job id, as in <see cref="FailFirstAttemptCommand"/>.</remarks>
public class RequeueOnceWithReasonCommand : IJobHandler<RequeueOnceWithReasonRequest>
{
    private static readonly ConcurrentDictionary<Guid, byte> Attempted = new();

    private readonly IJobContext _jobContext;

    public RequeueOnceWithReasonCommand(IJobContext jobContext) => _jobContext = jobContext;

    public Task HandleAsync(RequeueOnceWithReasonRequest message, CancellationToken cancellationToken)
    {
        if (Attempted.TryAdd(_jobContext.JobId, 0))
        {
            _jobContext.Outcome = new JobOutcome
            {
                State = State.Enqueued,
                ClearHandlerType = true,
                Reason = OutcomeReason.Concurrency,
                LogMessage = "Requeued once, as a Wait-mode concurrency limit does",
            };
        }

        return Task.CompletedTask;
    }
}

public class DeleteByOutcomeRequest : IJob;

/// <summary>Ends its own job as Deleted with a reason, the shape a Skip-mode rate limit produces.</summary>
public class DeleteByOutcomeCommand : IJobHandler<DeleteByOutcomeRequest>
{
    private readonly IJobContext _jobContext;

    public DeleteByOutcomeCommand(IJobContext jobContext) => _jobContext = jobContext;

    public Task HandleAsync(DeleteByOutcomeRequest message, CancellationToken cancellationToken)
    {
        _jobContext.Outcome = new JobOutcome
        {
            State = State.Deleted,
            Reason = OutcomeReason.RateLimit,
            LogMessage = "Skipped, as a Skip-mode rate limit does",
        };

        return Task.CompletedTask;
    }
}
