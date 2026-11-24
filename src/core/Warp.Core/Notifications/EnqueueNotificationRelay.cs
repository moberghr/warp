using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warp.Core.Buffering;

namespace Warp.Core.Notifications;

/// <summary>
/// Publishes the cross-process notifications for jobs committed through TContext's saves, off the
/// saving thread. The in-process wake-up already fired inline in the interceptor; this sends the
/// <c>JobEnqueued</c>/<c>MessageEnqueued</c> NOTIFY (or Service Broker message) for other processes.
/// <para>
/// Each batch is folded to one notification per (kind, queue), so a burst of saves on one queue costs
/// one round trip instead of one per save. Adds no delay of its own: an idle relay publishes as soon as
/// the first announcement arrives. The loop and the budgeted shutdown drain are
/// <see cref="DrainService{TItem}"/>'s; as a hosted service it needs a running host, like the listener
/// on the receiving side.
/// </para>
/// </summary>
internal sealed class EnqueueNotificationRelay : DrainService<Notification>
{
    // A batch folds to at most one notification per queue, so a large batch only means fewer round trips.
    private const int RelayBatchSize = 1_000;

    private readonly EnqueueAnnouncements _announcements;
    private readonly IWarpNotificationTransport _transport;

    public EnqueueNotificationRelay(EnqueueAnnouncements announcements, IWarpNotificationTransport transport)
    {
        _announcements = announcements;
        _transport = transport;
    }

    protected override ChannelReader<Notification> Reader => _announcements.Reader;

    protected override int BatchSize => RelayBatchSize;

    protected override TimeSpan ShutdownDrainBudget => TimeSpan.FromSeconds(5);

    /// <summary>Registers the relay and activates the announcements. Called by <c>UseDatabasePush()</c>.</summary>
    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton<EnqueueNotificationRelayMarker>();
        services.AddHostedService<EnqueueNotificationRelay>();
    }

    protected override void CompleteWriting() => _announcements.Complete();

    // FireAsync never throws (transports log and count failures), which DrainService requires of a flush.
    // A job-finalized notification names its own jobs, so those are merged rather than folded: one per
    // batch (more only past the payload's id limit) instead of one per finished job.
    protected override Task FlushAsync(List<Notification> batch, CancellationToken cancellationToken)
    {
        var finalized = batch
            .Where(x => x.Kind == NotificationKind.JobFinalized)
            .Select(x => x.Queue)
            .ToList();
        var notifications = batch
            .Where(x => x.Kind != NotificationKind.JobFinalized)
            .Distinct()
            .ToList();
        if (finalized.Count > 0)
        {
            notifications.AddRange(FinalizedParentsPayload.Merge(finalized).Select(x => new Notification(NotificationKind.JobFinalized, x)));
        }

        return NotificationDispatch.FireAsync(_transport, notifications, cancellationToken);
    }
}
