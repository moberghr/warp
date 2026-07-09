using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Handlers;
using Warp.Core.RateLimit;
using Warp.Core.Retry;
using Warp.Core.Timeout;

namespace Warp.Tests.TestData.Handlers;

// Parents that each carry a per-handler addon constraint and spawn a plain UnitRequest child
// during execution. The child declares no such constraint of its own, so these exercise
// whether addon (operational-policy) metadata leaks from parent to causally-spawned child.
[RateLimit("mi-ratelimit", count: 1, perSeconds: 60)]
public class RateLimitedParentRequest : IJob;

public class RateLimitedParentHandler(IPublisher publisher, TestContext context)
    : IJobHandler<RateLimitedParentRequest>
{
    public async Task HandleAsync(RateLimitedParentRequest message, CancellationToken cancellationToken)
    {
        await publisher.Enqueue(new UnitRequest());
        await context.SaveChangesAsync(cancellationToken);
    }
}

[Mutex("mi-mutex")]
public class MutexParentRequest : IJob;

public class MutexParentHandler(IPublisher publisher, TestContext context)
    : IJobHandler<MutexParentRequest>
{
    public async Task HandleAsync(MutexParentRequest message, CancellationToken cancellationToken)
    {
        await publisher.Enqueue(new UnitRequest());
        await context.SaveChangesAsync(cancellationToken);
    }
}

[Semaphore("mi-semaphore", 3)]
public class SemaphoreParentRequest : IJob;

public class SemaphoreParentHandler(IPublisher publisher, TestContext context)
    : IJobHandler<SemaphoreParentRequest>
{
    public async Task HandleAsync(SemaphoreParentRequest message, CancellationToken cancellationToken)
    {
        await publisher.Enqueue(new UnitRequest());
        await context.SaveChangesAsync(cancellationToken);
    }
}

[Timeout(30, Scope = TimeoutScope.Total)]
public class TimeoutParentRequest : IJob;

public class TimeoutParentHandler(IPublisher publisher, TestContext context)
    : IJobHandler<TimeoutParentRequest>
{
    public async Task HandleAsync(TimeoutParentRequest message, CancellationToken cancellationToken)
    {
        await publisher.Enqueue(new UnitRequest());
        await context.SaveChangesAsync(cancellationToken);
    }
}

// Fails on its first attempt (RetriedTimes == 0) to force exactly one retry, then on the
// retry (RetriedTimes == 1) spawns a plain child and succeeds. The parent's live RetriedTimes
// counter is therefore populated at the moment the child inherits its metadata.
public class RetriedParentRequest : IJob;

public class RetriedParentHandler(IPublisher publisher, TestContext context, IJobContext jobContext)
    : IJobHandler<RetriedParentRequest>
{
    public async Task HandleAsync(RetriedParentRequest message, CancellationToken cancellationToken)
    {
        var retriedTimes = jobContext.GetMetadata<IRetryMetadata>().RetriedTimes;
        if (retriedTimes == 0)
        {
            throw new InvalidOperationException("Forcing one retry so RetriedTimes is populated.");
        }

        await publisher.Enqueue(new UnitRequest());
        await context.SaveChangesAsync(cancellationToken);
    }
}
