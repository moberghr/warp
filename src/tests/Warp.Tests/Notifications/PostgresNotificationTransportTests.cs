using Npgsql;
using Shouldly;
using Warp.Core.Notifications;
using Warp.Provider.PostgreSql;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Notifications;

[Trait("Category", "PostgreSql")]
public class PostgresNotificationTransportTests : IAsyncLifetime, IClassFixture<PostgreSqlClassFixture>
{
    private readonly PostgreSqlClassFixture _fixture;

    public PostgresNotificationTransportTests(PostgreSqlClassFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task PublishListen_RoundTrip_DeliversNotification()
    {
        var transport = new PostgresNotificationTransport(
            _fixture.ConnectionString,
            new WarpDatabasePushConfiguration { ChannelName = "warp_notify_test" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var enumerator = transport.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            // Kick off MoveNextAsync first so the iterator runs OpenAsync+LISTEN before we publish.
            // Then await the public ListenerReady signal — deterministic, no Task.Delay race.
            var moveTask = enumerator.MoveNextAsync();
            await transport.ListenerReady.WaitAsync(cts.Token);

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
        var transport = new PostgresNotificationTransport(
            _fixture.ConnectionString,
            new WarpDatabasePushConfiguration { ChannelName = "warp_notify_test2" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var enumerator = transport.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            var firstMoveTask = enumerator.MoveNextAsync();
            await transport.ListenerReady.WaitAsync(cts.Token);

            await transport.PublishAsync(NotificationKind.MessageEnqueued, null, cts.Token);
            await transport.PublishAsync(NotificationKind.JobFinalized, null, cts.Token);
            await transport.PublishAsync(NotificationKind.JobEnqueued, "critical", cts.Token);

            var received = new List<Notification>();
            var hasNext = await firstMoveTask;
            hasNext.ShouldBeTrue();
            received.Add(enumerator.Current);

            for (var i = 0; i < 2; i++)
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

    [TimedFact]
    public async Task PublishListen_RoundTrip_WithDataSource_DeliversNotification()
    {
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var transport = new PostgresNotificationTransport(
            dataSource,
            new WarpDatabasePushConfiguration { ChannelName = "warp_notify_ds_test" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var enumerator = transport.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            var moveTask = enumerator.MoveNextAsync();
            await transport.ListenerReady.WaitAsync(cts.Token);

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
}
