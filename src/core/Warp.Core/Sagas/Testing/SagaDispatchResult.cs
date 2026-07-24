using Warp.Core.Handlers;

namespace Warp.Core.Sagas.Testing;

/// <summary>
/// The classified effect of dispatching one message through <see cref="SagaTestHarness{TContext}"/>.
/// Derived from the saga row's before/after existence plus the <see cref="IJobContext.Outcome"/> the
/// <see cref="SagaHandlerProxy{TSaga, TMessage}"/> set. Values start at 1 (§8.11).
/// </summary>
public enum SagaDispatchOutcome
{
    /// <summary>A <c>[StartsSaga]</c> message created a new live saga row.</summary>
    Created = 1,

    /// <summary>A message was applied to an existing saga which remains live afterwards.</summary>
    Updated = 2,

    /// <summary>
    /// The handler called <c>MarkCompleted()</c> — the saga row was removed (or, for a start
    /// message that completes in the same call, never persisted). The correlation key is free again.
    /// </summary>
    Completed = 3,

    /// <summary>
    /// A non-<c>[StartsSaga]</c> message arrived for an unknown correlation key — the dead-letter
    /// path (<c>ISagaHandler.NotFoundAsync</c> ran; the proxy set the not-found outcome).
    /// </summary>
    NotFound = 4,

    /// <summary>
    /// The per-correlation-key lock was already held, so the handler was skipped and the message
    /// was rescheduled. In the harness this happens only when a test pre-holds the lock.
    /// </summary>
    Busy = 5,

    /// <summary>
    /// An <see cref="ITimeoutMessage"/> fired for a saga that no longer exists (already completed) —
    /// silently dropped as moot rather than failing.
    /// </summary>
    TimeoutDropped = 6,
}

/// <summary>
/// Result of <see cref="SagaTestHarness{TContext}.DispatchAsync{TMessage}"/>: the classified
/// <see cref="Outcome"/> plus the raw <see cref="JobOutcome"/> the proxy set (for the busy,
/// not-found, and timeout-dropped paths; <c>null</c> on the create/update/complete success paths,
/// exactly as a real worker would see it).
/// </summary>
public sealed class SagaDispatchResult
{
    /// <summary>The classified dispatch outcome.</summary>
    public required SagaDispatchOutcome Outcome { get; init; }

    /// <summary>
    /// The <see cref="JobOutcome"/> the proxy assigned to <see cref="IJobContext.Outcome"/>, or
    /// <c>null</c> when the dispatch succeeded (create / update / complete). Inspect
    /// <see cref="JobOutcome.State"/> / <see cref="JobOutcome.LogMessage"/> to distinguish, for
    /// example, a busy reschedule from a save-conflict reschedule.
    /// </summary>
    public JobOutcome? JobOutcome { get; init; }
}
