using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.RateLimit;
using Warp.Core.Timeout;

namespace Warp.Core.Handlers;

/// <summary>
/// Two request types that exist to drive the last outcome reasons that no other demo seed reaches.
/// </summary>
/// <remarks>
/// Concurrency reasons can be driven from an existing request via <c>JobParameters.WithMutex</c> /
/// <c>WithSemaphore</c>, but timeout and rate limit are read off the request TYPE (attribute placement,
/// §8.8) — there is no per-publish extension for them. So exercising <c>stats:deleted-timeout</c> and
/// <c>stats:requeued-ratelimit</c> end to end requires dedicated attributed types; these are them.
/// </remarks>

/// <summary>
/// Sleeps well past its own timeout. <c>[Timeout]</c> defaults to <see cref="TimeoutMode.Delete"/>, so the
/// pipeline marks the job <see cref="State.Deleted"/> with reason <c>Timeout</c> — and deliberately does NOT
/// let AddRetry retry it (§8.7). Produces <c>stats:deleted-timeout</c>.
/// </summary>
[Timeout(seconds: 1)]
public class TimeoutDemoRequest : IJob;

public class TimeoutDemoCommand : IJobHandler<TimeoutDemoRequest>
{
    public async Task HandleAsync(TimeoutDemoRequest message, CancellationToken cancellationToken)
    {
        // Comfortably past the 1s budget. Honours the token so cancellation is prompt once it fires.
        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
    }
}

/// <summary>
/// One start allowed per minute, in <see cref="RateLimitMode.Wait"/> mode, so the second and later jobs are
/// rescheduled rather than dropped — producing <c>stats:requeued-ratelimit</c>. Wait-mode reschedules land in
/// <see cref="State.Scheduled"/> and depend on <c>ScheduledJobActivation</c>, so the requeue is counted
/// immediately even though the job itself will not run again for a while (§8.8).
/// </summary>
[RateLimit("demo-ratelimit", count: 1, perSeconds: 60, Mode = RateLimitMode.Wait)]
public class RateLimitDemoRequest : IJob;

public class RateLimitDemoCommand : IJobHandler<RateLimitDemoRequest>
{
    public Task HandleAsync(RateLimitDemoRequest message, CancellationToken cancellationToken)
    {
        // The handler is irrelevant — the interesting behaviour is the surplus never reaching it.
        return Task.CompletedTask;
    }
}
