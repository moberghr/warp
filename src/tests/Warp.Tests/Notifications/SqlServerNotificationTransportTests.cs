using Shouldly;
using Warp.Core.Notifications;
using Warp.Provider.SqlServer;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Notifications;

[Trait("Category", "SqlServer")]
public class SqlServerNotificationTransportTests : IAsyncLifetime, IClassFixture<SqlServerPushClassFixture>
{
    private readonly SqlServerPushClassFixture _fixture;

    public SqlServerNotificationTransportTests(SqlServerPushClassFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private SqlServerNotificationTransport CreateTransport(string channelName) =>
        new(_fixture.ConnectionString, new WarpDatabasePushConfiguration { ChannelName = channelName });

    [TimedFact]
    public async Task PublishListen_RoundTrip_DeliversNotification()
    {
        var transport = CreateTransport("warp_notify_rt");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var enumerator = transport.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            // Service Broker queues are persistent: WAITFOR (RECEIVE) returns any messages
            // already enqueued at the moment it runs. Unlike PG LISTEN/NOTIFY (where a NOTIFY
            // fired before LISTEN is registered is lost), publish-before-listen is safe here.
            // No timing guard needed.
            var moveTask = enumerator.MoveNextAsync();

            await transport.PublishAsync(NotificationKind.JobEnqueued, "default", cts.Token);

            var hasNext = await moveTask;
            hasNext.ShouldBeTrue("Expected to receive a notification within the timeout");
            enumerator.Current.Kind.ShouldBe(NotificationKind.JobEnqueued);
            enumerator.Current.Queue.ShouldBe("default");
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await enumerator.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [TimedFact]
    public async Task PublishListen_MultipleKinds_DeliversAll()
    {
        var transport = CreateTransport("warp_notify_multi");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var enumerator = transport.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            // Persistent queue — see PublishListen_RoundTrip_DeliversNotification for rationale.
            var firstMoveTask = enumerator.MoveNextAsync();

            await transport.PublishAsync(NotificationKind.MessageEnqueued, null, cts.Token);
            await transport.PublishAsync(NotificationKind.JobFinalized, null, cts.Token);
            await transport.PublishAsync(NotificationKind.JobEnqueued, "critical", cts.Token);

            var received = new List<Notification>();
            var hasNext = await firstMoveTask;
            hasNext.ShouldBeTrue();
            received.Add(enumerator.Current);

            while (received.Count < 3)
            {
                hasNext = await enumerator.MoveNextAsync();
                hasNext.ShouldBeTrue();
                received.Add(enumerator.Current);
            }

            received.ShouldContain(n => n.Kind == NotificationKind.MessageEnqueued);
            received.ShouldContain(n => n.Kind == NotificationKind.JobFinalized);
            received.ShouldContain(n => n.Kind == NotificationKind.JobEnqueued && n.Queue == "critical");
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await enumerator.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public void Constructor_UnsafeChannelName_Throws()
    {
        var unsafe1 = () => new SqlServerNotificationTransport(
            _fixture.ConnectionString,
            new WarpDatabasePushConfiguration { ChannelName = "bad; drop table foo;" });
        unsafe1.ShouldThrow<ArgumentException>();

        var unsafe2 = () => new SqlServerNotificationTransport(
            _fixture.ConnectionString,
            new WarpDatabasePushConfiguration { ChannelName = "1starts_with_digit" });
        unsafe2.ShouldThrow<ArgumentException>();
    }
}
