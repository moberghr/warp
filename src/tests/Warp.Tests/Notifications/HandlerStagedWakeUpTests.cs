using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Enums;
using Warp.Core.Notifications;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Notifications;

// Issue #322, worker side. A job the handler stages is committed by the worker's handler-scope
// save, which the enqueue interceptor announces like any other TContext save (the worker sends
// nothing itself). Exactly one wake-up must go out for it: never zero (a missed wake-up) and never
// two (an extra NOTIFY on every job).
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class HandlerStagedWakeUpTestsBase : IntegrationTestBase
{
    private const string ChildQueue = "staged-child-q";

    protected HandlerStagedWakeUpTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task WorkerSave_HandlerStagedChild_SendsExactlyOneWakeUp()
    {
        var transport = new RecordingTransport();
        var server = await WarpTestServer.StartAsync(
            Fixture,
            opt => opt.Queues = ["default", ChildQueue],
            services =>
            {
                // What UseDatabasePush() wires for the send side, with a recording transport.
                services.AddSingleton<IWarpNotificationTransport>(transport);
                EnqueueNotificationRelay.Register(services);
            });
        try
        {
            var publisher = server.CreatePublisher();
            await publisher.Enqueue(new StageChildOnQueueRequest { Queue = ChildQueue });
            await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

            await server.WaitForCompletion();
        }
        finally
        {
            // Variant B publishes through the relay, off the saving thread. Stopping the host makes the
            // relay publish everything still queued, so a missing OR a duplicate wake-up shows below.
            await server.DisposeAsync();
        }

        transport.ForQueue(ChildQueue).ShouldBe(1);
    }

    private sealed class RecordingTransport : IWarpNotificationTransport
    {
        private readonly ConcurrentQueue<(NotificationKind Kind, string? Queue)> _published = new();

        public Task ListenerReady { get; } = Task.CompletedTask;

        public int ForQueue(string queue) => _published.Count(x => x.Kind == NotificationKind.JobEnqueued && string.Equals(x.Queue, queue, StringComparison.Ordinal));

        public Task PublishAsync(NotificationKind kind, string? queue, CancellationToken ct)
        {
            _published.Enqueue((kind, queue));

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<Notification> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield break;
        }
    }
}
