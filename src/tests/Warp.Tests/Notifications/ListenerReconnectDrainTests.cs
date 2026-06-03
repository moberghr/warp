using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warp.Core.Enums;
using Warp.Core.Notifications;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Notifications;

// Durability guarantee under a dropped listener connection: when the transport's ListenAsync
// throws (network blip, SQL failover, transport restart), the reconnect loop in
// NotificationListenerTask.ExecuteAsync fires DrainSignals on every retry iteration. That fan-out
// — dispatcher + MessageRouter + Orchestrator — lets each consumer do a fresh DB poll and pick
// up whatever was enqueued during the listener's offline window. Without this, a single
// transport hiccup would strand every job that arrived during the gap until the next slow poll.
[GenerateDatabaseTests(WithPush = true)]
public abstract class ListenerReconnectDrainTestsBase : IntegrationTestBase
{
    protected ListenerReconnectDrainTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenListenerAlwaysFails_WhenJobEnqueued_ThenReconnectDrainStillDelivers()
    {
        // Transport that always throws on ListenAsync but accepts PublishAsync silently. The
        // listener task will catch the throw, log, wait ReconnectInitialDelay, and loop back —
        // on every loop iteration DrainSignals fans a wake-up signal out to the dispatcher.
        // If drain-on-reconnect works, that's how the job gets picked up. If it doesn't, the
        // job sits until the 30s PollingInterval expires, and the test fails at its 10s cap.
        const string queue = "listener-reconnect-drain";

        var transport = new ListenFailingTransport();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.UseDispatcher = true;
                cfg.WorkerCount = 1;
                cfg.Queues = [queue];

                // Polling set well beyond the test budget. If polling ever delivers, the test
                // would pass at ~30s, not within the 10s TimedFact — so a pass here is proof
                // that push's drain-on-reconnect path is what delivered the job.
                cfg.PollingInterval = TimeSpan.FromSeconds(30);
                cfg.MaxPollingInterval = TimeSpan.FromSeconds(30);
                cfg.PollingIntervalFactor = 1.0;

                cfg.UseDatabasePush(o =>
                {
                    o.ChannelName = "listener_reconnect_drain_test";
                    o.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
                    o.ReconnectMaxDelay = TimeSpan.FromSeconds(1);
                });
            },
            configureServices: services =>
            {
                services.RemoveAll<IWarpNotificationTransport>();
                services.AddSingleton<IWarpNotificationTransport>(transport);
            });

        // Wait until the listener has STARTED its second attempt — that proves the first
        // attempt fully unwound (throw → log → backoff sleep → loop back) and the listener
        // is actively reconnecting on failure. Subsequent drain iterations are what we rely
        // on to deliver the job below.
        await transport.SecondAttemptStarted.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new CounterRequest(), queue: queue);
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // The job must complete via the reconnect-driven DrainSignals path, not polling.
        // PollingInterval / MaxPollingInterval are set to 30s above, so if polling ever
        // delivers, the WaitForJobState below would only succeed at ~30s — well past this
        // 4s budget. A pass within 4s is therefore proof that one of the listener's reconnect
        // iterations fired a DrainSignals wake-up whose dispatcher poll observed our job.
        //
        // We deliberately do NOT assert `ListenAttempts > N` afterwards. DrainSignals only
        // signals consumers — the dispatcher's actual DB poll runs on its own task and can
        // land after the signal that triggered it. So a drain whose signal fires before our
        // enqueue can still observe the enqueue when its poll task is scheduled. That makes
        // "more attempts after enqueue" decoupled from "drain delivered" and produced a flaky
        // assertion. Completion within the 4s budget is the actual invariant under test.
        await server.WaitForJobState(jobId, State.Completed, TimeSpan.FromSeconds(4));
    }

    // Listener always throws, publisher is a no-op. Keeps the push side plausibly healthy
    // from the publisher's POV while guaranteeing the listener never actually observes a
    // notification — so any delivery is through the reconnect-driven DrainSignals path.
    private sealed class ListenFailingTransport : IWarpNotificationTransport
    {
        private readonly TaskCompletionSource _secondAttemptStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _listenAttempts;

        // Fires when ListenAsync is entered for the SECOND time, i.e. after the first attempt
        // fully unwound through the reconnect-backoff sleep and the listener task looped back.
        public Task SecondAttemptStarted => _secondAttemptStarted.Task;

        public Task PublishAsync(NotificationKind kind, string? queue, CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<Notification> ListenAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _listenAttempts);
            if (attempt >= 2)
            {
                _secondAttemptStarted.TrySetResult();
            }

            await Task.Yield();
            throw new InvalidOperationException("ListenFailingTransport: listen deliberately throws to exercise reconnect-drain");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public Task ListenerReady { get; } = Task.CompletedTask;
    }
}
