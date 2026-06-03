using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Notifications;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Notifications;

// Durability guarantee under broken push: if the notification transport is completely broken,
// polling must still pick up jobs. This is why polling runs alongside push — a silent push
// failure (transport crash, network blip, missed LISTEN/NOTIFY) cannot leave jobs stranded.
// If this property regresses, any deploy that ships with a broken transport becomes a silent
// job-loss incident.
[GenerateDatabaseTests(WithPush = true)]
public abstract class PushFailurePollingBackstopTestsBase : IntegrationTestBase
{
    protected PushFailurePollingBackstopTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenPushEnabledButTransportBroken_WhenJobEnqueued_ThenPollingStillPicksItUp()
    {
        // Server configured with push ON and a deliberately-broken transport. PublishAsync
        // throws on every call; ListenAsync also throws. NotificationDispatch.FireAsync swallows
        // the publish exception (outbox durability), and NotificationListenerTask's reconnect
        // loop logs + backs off when ListenAsync throws. Neither error is allowed to lose the
        // job — polling at PollingInterval is the safety net.
        const string queue = "push-broken-polling-backstop";
        var pollingInterval = TimeSpan.FromSeconds(1);
        var failingTransport = new FailingTransport();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.UseDispatcher = true;
                cfg.WorkerCount = 1;
                cfg.Queues = [queue];

                // Push is ON, so the fast path would normally pick up sub-50ms. We force it
                // off by replacing the transport below; the test must therefore complete via
                // polling.
                cfg.PollingInterval = pollingInterval;
                cfg.MaxPollingInterval = pollingInterval;
                cfg.PollingIntervalFactor = 1.0;

                cfg.UseDatabasePush(o => o.ChannelName = "push_broken_test");
            },
            configureServices: services =>
            {
                // Swap the provider-built transport for our failing one. Still a singleton,
                // still consumed by NotificationListenerTask and by the publish-side FireAsync.
                services.RemoveAll<IWarpNotificationTransport>();
                services.AddSingleton<IWarpNotificationTransport>(failingTransport);
            });

        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new CounterRequest(), queue: queue);
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // The job must complete within a small multiple of PollingInterval — proving polling,
        // not push, delivered it. Upper bound: one interval to claim + one more for completion
        // write; 3× gives headroom for CI jitter without hiding a real regression.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await server.WaitForJobState(
            jobId,
            State.Completed,
            TimeSpan.FromSeconds(pollingInterval.TotalSeconds * 3));
        sw.Stop();

        // And confirm the transport was actually broken the whole time — the test would be
        // meaningless if somehow push delivered it.
        failingTransport.PublishAttempts.ShouldBeGreaterThan(
            0,
            "publish-side should have attempted at least once (job enqueue triggers FireAsync)");
    }

    // Transport that throws on every method. Exercises NotificationDispatch.FireAsync's
    // swallow-on-failure contract and NotificationListenerTask's reconnect-on-exception loop.
    private sealed class FailingTransport : IWarpNotificationTransport
    {
        private int _publishAttempts;

        public int PublishAttempts => _publishAttempts;

        public Task PublishAsync(NotificationKind kind, string? queue, CancellationToken ct)
        {
            Interlocked.Increment(ref _publishAttempts);
            throw new InvalidOperationException("FailingTransport: publish deliberately broken");
        }

        public async IAsyncEnumerable<Notification> ListenAsync([EnumeratorCancellation] CancellationToken ct)
        {
            // Throw before yielding anything — the listener task's reconnect loop catches,
            // logs at Warning, and waits ReconnectInitialDelay before retrying. The yield
            // break below is unreachable but required for the async-iterator signature.
            await Task.Yield();
            throw new InvalidOperationException("FailingTransport: listen deliberately broken");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public Task ListenerReady { get; } = Task.CompletedTask;
    }
}
