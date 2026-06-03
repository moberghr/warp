using System.Runtime.CompilerServices;

namespace Warp.Core.Notifications;

/// <summary>
/// Default transport when <c>opt.UseDatabasePush() (inside the AddWarp/AddWarpWorker lambda)</c> is not called. Publish is a no-op;
/// listen blocks until cancelled and yields nothing. Keeps the publish-side hot path free
/// of branching — callers always just <c>await transport.PublishAsync(...)</c>.
/// </summary>
public sealed class NullNotificationTransport : IWarpNotificationTransport
{
    public Task PublishAsync(NotificationKind kind, string? queue, CancellationToken ct) => Task.CompletedTask;

    public async IAsyncEnumerable<Notification> ListenAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // No-op listener: block forever (until cancelled). The notification listener hosted
        // service is only registered by opt.UseDatabasePush(), so this path normally isn't hit.
        await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    public Task ListenerReady { get; } = Task.CompletedTask;
}
