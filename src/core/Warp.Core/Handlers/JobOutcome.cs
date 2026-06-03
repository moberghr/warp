using Warp.Core.Enums;

namespace Warp.Core.Handlers;

/// <summary>
/// Outcome of a job execution as observed (or overridden) by the pipeline. Set by
/// <c>IJobContext.Outcome</c> from inside an <see cref="IPipelineBehavior{TRequest, TResponse}"/>
/// when the behavior wants to short-circuit normal completion — e.g. reschedule for retry,
/// fail without retry, mark as <see cref="State.Deleted"/>, etc.
/// </summary>
/// <remarks>
/// <para>
/// All properties are <c>init</c>-only by design: a <see cref="JobOutcome"/> represents a
/// final decision, not a mutable buffer. To short-circuit from a pipeline behavior,
/// construct a NEW instance with the desired state and assign it to
/// <c>IJobContext.Outcome</c>:
/// </para>
/// <code>
/// public async Task&lt;Unit&gt; HandleAsync(MyRequest request, RequestHandlerDelegate&lt;Unit&gt; next, CancellationToken ct)
/// {
///     try
///     {
///         return await next();
///     }
///     catch (DontRetryException)
///     {
///         _jobContext.Outcome = new JobOutcome
///         {
///             State = State.Failed,
///             LogMessage = "Short-circuited: domain refused retry",
///         };
///         throw;
///     }
/// }
/// </code>
/// <para>
/// You cannot write <c>outcome.State = X</c> on an existing instance — that's CS8852.
/// See <see cref="RescheduledState"/> for choosing between <see cref="State.Enqueued"/>
/// and <see cref="State.Scheduled"/> based on the target time.
/// </para>
/// </remarks>
public class JobOutcome
{
    /// <summary>
    /// Terminal state for the job. Common values: <see cref="State.Completed"/>,
    /// <see cref="State.Failed"/>, <see cref="State.Deleted"/>, or the result of
    /// <see cref="RescheduledState"/> when rescheduling for retry.
    /// </summary>
    public State State { get; init; }

    /// <summary>
    /// When set, the job is rescheduled to this time instead of finalising. Combine with
    /// <see cref="RescheduledState"/> to pick the correct <see cref="State"/>.
    /// </summary>
    public DateTime? ScheduleTime { get; init; }

    /// <summary>
    /// Set to <c>true</c> to clear the resolved <c>HandlerType</c> on requeue — used by Retry
    /// so the next attempt re-resolves the handler (important for routed IMessage jobs).
    /// </summary>
    public bool ClearHandlerType { get; init; }

    /// <summary>
    /// Optional explanatory message appended to the job's log when this outcome is applied.
    /// </summary>
    public string? LogMessage { get; init; }

    /// <summary>
    /// Picks the correct queued state for a reschedule: <see cref="State.Scheduled"/> when the
    /// target time is in the future (so <c>ScheduledJobActivation</c> owns the flip back),
    /// otherwise <see cref="State.Enqueued"/>. Every pipeline behaviour that reschedules a job
    /// (retry, circuit breaker, future mutex/rate-limit) must route through this so no new
    /// behaviour silently writes <c>Enqueued</c> + future <c>ScheduleTime</c> — which the worker
    /// fetch would pick up prematurely.
    /// </summary>
    public static State RescheduledState(DateTime scheduleTime, DateTime now) =>
        scheduleTime > now ? State.Scheduled : State.Enqueued;
}
