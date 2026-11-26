using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;

namespace Warp.Core.Notifications;

/// <summary>
/// Helpers for emitting push notifications from the publish-side hooks
/// (Publisher / BatchPublisher / JobCommandService / worker handler-commit path).
/// </summary>
internal static class NotificationDispatch
{
    /// <summary>
    /// Snapshots newly-added <see cref="Job"/> rows about to be committed and returns
    /// the coarse-grained notifications that should fire post-commit: one
    /// <see cref="NotificationKind.JobEnqueued"/> per distinct queue for <see cref="JobKind.Job"/>
    /// rows in <see cref="State.Enqueued"/>, and one <see cref="NotificationKind.MessageEnqueued"/>
    /// if any <see cref="JobKind.Message"/> row in <see cref="State.Enqueued"/> is added.
    /// <para>
    /// <see cref="State.Scheduled"/> rows are skipped — they fire when the activation task flips them.
    /// </para>
    /// </summary>
    public static List<Notification> CapturePending(DbContext context)
    {
        var entries = context.ChangeTracker.Entries<Job>();

        var result = new List<Notification>();
        HashSet<string>? seenQueues = null;
        HashSet<Guid>? awaitedParents = null;
        var sawMessage = false;

        foreach (var entry in entries)
        {
            // A continuation published after its parent finished would otherwise wait for the
            // Orchestrator's next sweep: nothing else is going to finish that parent again. Naming the
            // parent has the Orchestrator look at its awaiting rows now (rule 6.11).
            if (entry.State == EntityState.Added && entry.Entity.CurrentState == State.Awaiting && entry.Entity.ParentJobId is { } awaited)
            {
                awaitedParents ??= [];
                awaitedParents.Add(awaited);

                continue;
            }

            if (!ShouldEmit(entry))
            {
                continue;
            }

            var job = entry.Entity;
            if (job.Kind == JobKind.Job)
            {
                seenQueues ??= new HashSet<string>(StringComparer.Ordinal);
                var queue = string.IsNullOrEmpty(job.Queue) ? "default" : job.Queue;
                if (seenQueues.Add(queue))
                {
                    result.Add(new Notification(NotificationKind.JobEnqueued, queue));
                }
            }
            else if (job.Kind == JobKind.Message && !sawMessage)
            {
                result.Add(new Notification(NotificationKind.MessageEnqueued, null));
                sawMessage = true;
            }
        }

        if (awaitedParents != null)
        {
            result.Add(new Notification(NotificationKind.JobFinalized, FinalizedParentsPayload.Encode([], awaitedParents)));
        }

        return result;
    }

    /// <summary>
    /// Fires all captured notifications. Swallows transport failures — the originating
    /// transaction is already durable and a missed notification only delays pickup until
    /// the next listener reconnect-drain or subsequent notification.
    /// </summary>
    public static async Task FireAsync(
        IWarpNotificationTransport transport,
        IReadOnlyList<Notification> notifications,
        CancellationToken ct = default)
    {
        if (notifications.Count == 0 || transport is NullNotificationTransport)
        {
            return;
        }

        for (var i = 0; i < notifications.Count; i++)
        {
            var n = notifications[i];
            try
            {
                await transport.PublishAsync(n.Kind, n.Queue, ct);
            }
            catch
            {
                // Transport-level failure is the transport's responsibility to log.
                // Publishing must not throw upward — the commit already happened.
            }
        }
    }

    /// <summary>
    /// Wakes in-process subscribers via <paramref name="signals"/> AND publishes
    /// <paramref name="notifications"/> through <paramref name="transport"/> for cross-process
    /// wake-up. Local signals always fire — independent of transport — so bare workers and
    /// server-task loops in the same process do not depend on <c>UseDatabasePush()</c> for
    /// their wake-up. With push enabled, the listener's incoming signal arrives as a duplicate
    /// that the semaphore-based subscribers harmlessly absorb.
    /// </summary>
    public static Task DispatchAsync<TContext>(
        IReadOnlyList<Notification> notifications,
        ServerTaskSignals<TContext> signals,
        IWarpNotificationTransport transport,
        CancellationToken ct = default)
        where TContext : DbContext
    {
        SignalLocally(notifications, signals);

        return FireAsync(transport, notifications, ct);
    }

    /// <summary>The in-process half of <see cref="DispatchAsync{TContext}"/>: wakes this process's
    /// subscribers only, publishes nothing cross-process.</summary>
    public static void SignalLocally<TContext>(IReadOnlyList<Notification> notifications, ServerTaskSignals<TContext> signals)
        where TContext : DbContext
    {
        for (var i = 0; i < notifications.Count; i++)
        {
            switch (notifications[i].Kind)
            {
                case NotificationKind.JobEnqueued:
                    signals.SignalJobEnqueued(notifications[i].Queue);
                    break;
                case NotificationKind.MessageEnqueued:
                    signals.SignalMessageEnqueued();
                    break;
                case NotificationKind.JobFinalized:
                    FinalizedParentsPayload.Signal(signals, notifications[i].Queue);
                    break;
                default:
                    break;
            }
        }
    }

    private static bool ShouldEmit(EntityEntry<Job> entry)
    {
        return entry.State == EntityState.Added
            && entry.Entity.CurrentState == State.Enqueued;
    }
}
