namespace Warp.Core.Notifications;

/// <summary>
/// Event kinds emitted by Warp to notification transports. Consumers re-query the DB on wake,
/// so payloads are coarse-grained — the kind (+ queue for job enqueues) is all that's needed.
/// </summary>
public enum NotificationKind : byte
{
    // Kind=Job, CurrentState=Enqueued, ScheduleTime<=now → wake dispatcher on that queue.
    JobEnqueued = 1,

    // Kind=Message, CurrentState=Enqueued → wake MessageRouter.
    MessageEnqueued = 2,

    // Child reached terminal state → wake Orchestrator (cross-server).
    JobFinalized = 3,
}

/// <summary>
/// Event payload carried over the notification transport. <paramref name="Queue"/> is the queue for
/// <see cref="NotificationKind.JobEnqueued"/>. For <see cref="NotificationKind.JobFinalized"/> it carries
/// the parents of the finished jobs (written and read by <c>FinalizedParentsPayload</c>), or null when
/// the sender cannot name them. Null for <see cref="NotificationKind.MessageEnqueued"/>.
/// </summary>
public readonly record struct Notification(NotificationKind Kind, string? Queue);
