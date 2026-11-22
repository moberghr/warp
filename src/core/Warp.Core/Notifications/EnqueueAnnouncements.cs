using System.Threading.Channels;
using Warp.Core.Buffering;
using Warp.Core.Logging;

namespace Warp.Core.Notifications;

/// <summary>
/// In-process hand-off between <see cref="EnqueueNotificationInterceptor{TContext}"/> (which knows
/// jobs were committed) and <see cref="EnqueueNotificationRelay"/> (which tells other processes).
/// It has no dependencies, so the interceptor can be built at any point of startup — it never needs
/// the transport (#322).
/// <para>
/// Only active when <c>UseDatabasePush()</c> registered the relay; otherwise there is no one to tell
/// and <see cref="Announce"/> does nothing. Bounded and lossy like the other <see cref="DrainQueue{TItem}"/>
/// pipelines: a full queue drops the notification and counts it on <c>warp.notifications.dropped</c>,
/// which costs at most one poll interval — the listener also drains on every reconnect.
/// </para>
/// </summary>
internal sealed class EnqueueAnnouncements
{
    internal const int Capacity = 10_000;

    private readonly DrainQueue<Notification> _queue = new(Capacity);

    public EnqueueAnnouncements(IEnumerable<EnqueueNotificationRelayMarker> relays)
    {
        IsActive = relays.Any();
    }

    public bool IsActive { get; }

    public ChannelReader<Notification> Reader => _queue.Reader;

    public void Announce(IReadOnlyList<Notification> committed)
    {
        if (!IsActive)
        {
            return;
        }

        for (var i = 0; i < committed.Count; i++)
        {
            if (!_queue.TryAdd(committed[i]))
            {
                WarpTelemetry.NotificationsDropped.Add(1);
            }
        }
    }

    public void Complete() => _queue.Complete();
}
