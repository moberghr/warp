using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Notifications;

namespace Warp.Tests.Notifications;

// The relay publishes what the enqueue interceptor announced (#322). It folds whatever queued up
// into one notification per (kind, queue), and publishes what is still queued when the host stops.
[Trait("Category", "NoDb")]
public class EnqueueNotificationRelayTests
{
    [TimedFact]
    public async Task Relay_QueuedAnnouncements_PublishesOnePerKindAndQueue()
    {
        var announcements = new EnqueueAnnouncements([new EnqueueNotificationRelayMarker()]);
        using var transport = new RecordingTransport();
        using var relay = new EnqueueNotificationRelay(announcements, transport);

        // Queued before the relay starts, so its first read finds all of them at once.
        announcements.Announce(
        [
            new Notification(NotificationKind.JobEnqueued, "a"),
            new Notification(NotificationKind.JobEnqueued, "a"),
            new Notification(NotificationKind.JobEnqueued, "b"),
            new Notification(NotificationKind.MessageEnqueued, null),
            new Notification(NotificationKind.MessageEnqueued, null),
        ]);

        await relay.StartAsync(Xunit.TestContext.Current.CancellationToken);
        await transport.WaitForCount(3);
        await relay.StopAsync(Xunit.TestContext.Current.CancellationToken);

        transport.Published.Count.ShouldBe(3);
        transport.Published.ShouldContain((NotificationKind.JobEnqueued, (string?)"a"));
        transport.Published.ShouldContain((NotificationKind.JobEnqueued, (string?)"b"));
        transport.Published.ShouldContain((NotificationKind.MessageEnqueued, (string?)null));
    }

    [TimedFact]
    public async Task Relay_StoppedWhilePublishing_PublishesWhatIsStillQueued()
    {
        var announcements = new EnqueueAnnouncements([new EnqueueNotificationRelayMarker()]);
        using var transport = new RecordingTransport();
        using var relay = new EnqueueNotificationRelay(announcements, transport);
        var ct = Xunit.TestContext.Current.CancellationToken;

        // Hold the relay inside its first publish, queue a second announcement behind it, and stop
        // the host while the first is still in flight: the second must still go out.
        transport.HoldNextPublish();
        await relay.StartAsync(ct);
        announcements.Announce([new Notification(NotificationKind.JobEnqueued, "first")]);
        await transport.WaitForPublishStarted();
        announcements.Announce([new Notification(NotificationKind.JobEnqueued, "queued")]);

        var stopping = relay.StopAsync(ct);
        transport.ReleaseHeldPublish();
        await stopping;

        transport.Published.ShouldBe([(NotificationKind.JobEnqueued, "first"), (NotificationKind.JobEnqueued, "queued")]);
    }

    [TimedFact]
    public void Announce_QueueFull_CountsTheDropsAndKeepsTheRest()
    {
        var announcements = new EnqueueAnnouncements([new EnqueueNotificationRelayMarker()]);
        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, x) =>
        {
            if (string.Equals(instrument.Name, "warp.notifications.dropped", StringComparison.Ordinal))
            {
                x.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref dropped, value));
        listener.Start();

        // Nothing drains the queue here, so everything past its capacity is refused.
        var batch = Enumerable.Range(0, EnqueueAnnouncements.Capacity + 3)
            .Select(x => new Notification(NotificationKind.JobEnqueued, "q"))
            .ToList();
        announcements.Announce(batch);

        Interlocked.Read(ref dropped).ShouldBe(3);
        var kept = 0;
        while (announcements.Reader.TryRead(out _))
        {
            kept++;
        }

        kept.ShouldBe(EnqueueAnnouncements.Capacity);
    }

    [TimedFact]
    public void Announce_WithoutRelay_KeepsNothing()
    {
        var announcements = new EnqueueAnnouncements([]);

        announcements.Announce([new Notification(NotificationKind.JobEnqueued, "a")]);

        announcements.IsActive.ShouldBeFalse();
        announcements.Reader.TryRead(out _).ShouldBeFalse();
    }

    private sealed class RecordingTransport : IWarpNotificationTransport, IDisposable
    {
        private readonly ConcurrentQueue<(NotificationKind Kind, string? Queue)> _published = new();
        private readonly SemaphoreSlim _publishedSignal = new(0);
        private readonly SemaphoreSlim _publishStarted = new(0);
        private readonly SemaphoreSlim _release = new(0);
        private int _holdNext;

        public IReadOnlyList<(NotificationKind Kind, string? Queue)> Published => [.. _published];

        public Task ListenerReady { get; } = Task.CompletedTask;

        public async Task WaitForCount(int expected)
        {
            while (_published.Count < expected)
            {
                (await _publishedSignal.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                    .ShouldBeTrue($"expected {expected} publishes, saw {_published.Count}");
            }
        }

        public void HoldNextPublish() => Interlocked.Exchange(ref _holdNext, 1);

        public void ReleaseHeldPublish() => _release.Release();

        public async Task WaitForPublishStarted() =>
            (await _publishStarted.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the relay never started publishing");

        public async Task PublishAsync(NotificationKind kind, string? queue, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _holdNext, 0) == 1)
            {
                _publishStarted.Release();
                (await _release.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).ShouldBeTrue("the held publish was never released");
            }

            _published.Enqueue((kind, queue));
            _publishedSignal.Release();
        }

        public async IAsyncEnumerable<Notification> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield break;
        }

        public void Dispose()
        {
            _publishedSignal.Dispose();
            _publishStarted.Dispose();
            _release.Dispose();
        }
    }
}
