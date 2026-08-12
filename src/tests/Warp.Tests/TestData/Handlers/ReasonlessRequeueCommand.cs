using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

/// <summary>
/// A job whose handler reschedules it WITHOUT stamping an <c>OutcomeReason</c> — the shape any user-written
/// pipeline behaviour or handler can produce, since <c>JobOutcome.Reason</c> is nullable and
/// <c>JobOutcome</c> is public API. Used to pin that requeue accounting does not silently ignore outcomes
/// Warp's own addons did not create.
/// <para>
/// <b>Deliberately a handler, not an <c>IPipelineBehavior</c>.</b> The Warp source generator registers every
/// behaviour it can see in referenced assemblies, open generics included, so a behaviour declared here runs
/// for EVERY request in EVERY test — and its <c>IJobContext</c> dependency cannot even be constructed
/// outside a worker's handler scope, which fails every <c>Warp.Http</c> request in the suite. A handler is
/// invoked only for its own request type, so the blast radius is exactly this job.
/// </para>
/// </summary>
public class ReasonlessRequeueRequest : IJob;

public class ReasonlessRequeueCommand : IJobHandler<ReasonlessRequeueRequest>
{
    private readonly IJobContext _jobContext;

    public ReasonlessRequeueCommand(IJobContext jobContext)
    {
        _jobContext = jobContext;
    }

    public Task HandleAsync(ReasonlessRequeueRequest message, CancellationToken cancellationToken)
    {
        _jobContext.Outcome = new JobOutcome
        {
            State = State.Enqueued,
            LogMessage = "Requeued by a handler that set no reason",
        };

        return Task.CompletedTask;
    }
}
