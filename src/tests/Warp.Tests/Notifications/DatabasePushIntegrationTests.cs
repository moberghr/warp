using Shouldly;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Notifications;

// UseDatabasePush end to end, on PostgreSQL (LISTEN/NOTIFY) and SQL Server (Service Broker). Each test
// starts a server whose poll interval is 10s, so work that completes within the waits below (all well
// under 10s) was woken by push, not found by a poll. The wait IS the assertion: it throws on timeout.
[GenerateDatabaseTests(WithPush = true, SerializeInCollection = "HeavyIntegration")]
public abstract class DatabasePushIntegrationTestsBase : IntegrationTestBase
{
    // Service Broker delivers more slowly than NOTIFY; one limit that both meet, half the poll interval.
    private static readonly TimeSpan PushDeadline = TimeSpan.FromSeconds(5);

    protected DatabasePushIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task JobEnqueued_WithDispatcherPlusPush_DispatcherPicksUpWithoutPolling()
    {
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            cfg =>
            {
                cfg.UseDispatcher = true;
                cfg.PollingInterval = TimeSpan.FromSeconds(10);
                cfg.MaxPollingInterval = TimeSpan.FromSeconds(10);
                cfg.PollingIntervalFactor = 1.0;
                cfg.UseDatabasePush(o => o.ChannelName = "warp_push_it_job");
            });
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new CounterRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Completed, PushDeadline);
    }

    [TimedFact]
    public async Task JobEnqueuedByAnotherHost_WithDispatcherPlusPush_DispatcherPicksUpWithoutPolling()
    {
        // The job is published from a different host (no worker), so the server's in-process signal never
        // fires: only the database notification the publisher's host sends can wake the dispatcher before
        // its 10 s poll. The publisher's host must be running (it sends the notification from a relay).
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            cfg =>
            {
                cfg.UseDispatcher = true;
                cfg.PollingInterval = TimeSpan.FromSeconds(10);
                cfg.MaxPollingInterval = TimeSpan.FromSeconds(10);
                cfg.PollingIntervalFactor = 1.0;
                cfg.UseDatabasePush(o => o.ChannelName = "warp_push_it_cross");
            });
        await using var publisherHost = await WarpTestServer.StartAsync(
            Fixture,
            cfg =>
            {
                cfg.DisableWorker();
                cfg.UseDatabasePush(o => o.ChannelName = "warp_push_it_cross");
            });

        var publisher = publisherHost.CreatePublisher();
        var jobId = await publisher.Enqueue(new CounterRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Completed, PushDeadline);
    }

    [TimedFact]
    public async Task MessageEnqueued_WithPush_MessageRoutingWakesImmediately()
    {
        // Both the router and the dispatcher have to be woken: the message is routed into a child job,
        // which then has to be picked up, and each of the two would otherwise wait 10s.
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            cfg =>
            {
                cfg.UseDispatcher = true;
                cfg.PollingInterval = TimeSpan.FromSeconds(10);
                cfg.MaxPollingInterval = TimeSpan.FromSeconds(10);
                cfg.MessageRoutingInterval = TimeSpan.FromSeconds(10);
                cfg.PollingIntervalFactor = 1.0;
                cfg.UseDatabasePush(o => o.ChannelName = "warp_push_it_msg");
            });
        var publisher = server.CreatePublisher();
        var messageId = await publisher.Publish(new SingleHandlerMessage());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion(PushDeadline);

        (await server.GetJob(messageId)).CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task PushEnabled_WithoutDispatcher_ProcessesWork()
    {
        // UseDispatcher=false + push: there is no dispatcher to wake, but the listener still signals
        // ServerTaskSignals.JobEnqueued, which every bare worker subscribes to (rule 2.9).
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            cfg =>
            {
                cfg.UseDispatcher = false;
                cfg.PollingInterval = TimeSpan.FromMilliseconds(200);
                cfg.UseDatabasePush(o => o.ChannelName = "warp_push_it_noDispatch");
            });
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new CounterRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Completed, PushDeadline);
    }
}
