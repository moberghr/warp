using Microsoft.Data.SqlClient;
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
            // Each listener has its own queue, created when it starts listening: like PG LISTEN/NOTIFY, a
            // publish before that reaches no one, so wait for the listener first.
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
        var transport = CreateTransport("warp_notify_multi");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var enumerator = transport.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            // Wait for the listener's queue: see PublishListen_RoundTrip_DeliversNotification.
            var firstMoveTask = enumerator.MoveNextAsync();
            await transport.ListenerReady.WaitAsync(cts.Token);

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

    [TimedFact]
    public async Task Publish_TwoListeningProcesses_BothReceiveIt()
    {
        // A Service Broker queue hands each message to ONE receiver. With one shared queue a notification
        // woke one random process, and was lost when that process had no workers. Every listener must get it.
        var first = CreateTransport("warp_notify_fanout");
        var second = CreateTransport("warp_notify_fanout");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstEnumerator = first.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var secondEnumerator = second.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            var firstMove = firstEnumerator.MoveNextAsync();
            var secondMove = secondEnumerator.MoveNextAsync();
            await first.ListenerReady.WaitAsync(cts.Token);
            await second.ListenerReady.WaitAsync(cts.Token);

            // Act
            await CreateTransport("warp_notify_fanout").PublishAsync(NotificationKind.JobEnqueued, "default", cts.Token);

            // Assert
            (await firstMove).ShouldBeTrue("the first listener did not receive the notification");
            (await secondMove).ShouldBeTrue("the second listener did not receive the notification");
            firstEnumerator.Current.Kind.ShouldBe(NotificationKind.JobEnqueued);
            secondEnumerator.Current.Kind.ShouldBe(NotificationKind.JobEnqueued);
        }
        finally
        {
            await cts.CancelAsync();
            await DisposeQuietlyAsync(firstEnumerator);
            await DisposeQuietlyAsync(secondEnumerator);
        }
    }

    [TimedFact]
    public async Task Listen_QueueLeftByADeadProcess_IsSweptAndItsOwnQueueIsDroppedWhenItStops()
    {
        // A process that dies keeps its listener queue, and every publish adds to it. The next listener
        // removes a queue whose oldest message is older than StaleQueueAge (zero here), and a listener
        // removes its own queue when it stops.
        const string channel = "warp_notify_sweep";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var publisher = CreateTransport(channel);
        await publisher.PublishAsync(NotificationKind.JobEnqueued, "default", cts.Token); // creates the contract
        await ExecuteSqlAsync($@"
            CREATE QUEUE [{channel}/queue/dead];
            CREATE SERVICE [{channel}/service/dead] ON QUEUE [{channel}/queue/dead] ([{channel}/contract]);");
        await publisher.PublishAsync(NotificationKind.JobEnqueued, "default", cts.Token); // lands in the dead queue
        (await ServiceExistsAsync($"{channel}/service/dead")).ShouldBeTrue();

        var listener = new SqlServerNotificationTransport(_fixture.ConnectionString, new WarpDatabasePushConfiguration { ChannelName = channel })
        {
            StaleQueueAge = TimeSpan.Zero,
        };
        var enumerator = listener.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var listening = Task.CompletedTask;
        try
        {
            // Act
            listening = enumerator.MoveNextAsync().AsTask();
            await listener.ListenerReady.WaitAsync(cts.Token);

            // Assert
            (await ServiceExistsAsync($"{channel}/service/dead")).ShouldBeFalse("the dead process's queue was not swept");
            (await ServiceExistsAsync(listener.ListenerServiceName)).ShouldBeTrue();
        }
        finally
        {
            // The pending move must finish before the iterator can be disposed.
            await cts.CancelAsync();
            await listening.ContinueWith(_ => { }, TaskScheduler.Default);
            await DisposeQuietlyAsync(enumerator);
        }

        (await ServiceExistsAsync(listener.ListenerServiceName)).ShouldBeFalse("the listener did not drop its own queue when it stopped");
    }

    [TimedFact]
    public async Task Listen_ManyNotificationsArrive_DoesNotSweepBeforeItsInterval()
    {
        // The sweep counted loop iterations, and an iteration ends whenever a notification arrives, so a busy
        // listener swept every ten notifications instead of every few minutes. A queue left after this
        // listener started (so its first sweep did not see it) must still be there after a burst.
        const string channel = "warp_notify_sweep_interval";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listener = new SqlServerNotificationTransport(_fixture.ConnectionString, new WarpDatabasePushConfiguration { ChannelName = channel })
        {
            StaleQueueAge = TimeSpan.Zero,
        };
        var enumerator = listener.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Task<bool> Next() => enumerator.MoveNextAsync().AsTask();
        var pending = Next();
        try
        {
            await listener.ListenerReady.WaitAsync(cts.Token);
            await ExecuteSqlAsync($@"
                CREATE QUEUE [{channel}/queue/dead];
                CREATE SERVICE [{channel}/service/dead] ON QUEUE [{channel}/queue/dead] ([{channel}/contract]);");

            // Act: each notification is received on its own, so each ends one iteration of the loop.
            for (var i = 0; i < 25; i++)
            {
                await listener.PublishAsync(NotificationKind.JobEnqueued, "default", cts.Token);
                (await pending).ShouldBeTrue();
                pending = Next();
            }

            // Assert
            (await ServiceExistsAsync($"{channel}/service/dead")).ShouldBeTrue("the listener swept on a notification count, not on its interval");
        }
        finally
        {
            await cts.CancelAsync();
            await pending.ContinueWith(_ => { }, TaskScheduler.Default);
            await DisposeQuietlyAsync(enumerator);
            await ExecuteSqlAsync($@"
                IF EXISTS (SELECT 1 FROM sys.services WHERE name = N'{channel}/service/dead') DROP SERVICE [{channel}/service/dead];
                IF EXISTS (SELECT 1 FROM sys.service_queues WHERE name = N'{channel}/queue/dead') DROP QUEUE [{channel}/queue/dead];");
        }
    }

    [TimedFact]
    public async Task Listen_PeerQueueWithAnOldMessageWhoseOwnerIsStillConnected_IsNotSwept()
    {
        // A listener that is paused or behind still owns its queue: it holds a session lock named after the
        // queue on its listening connection. Only the age of the oldest message was checked, so a peer
        // dropped a live listener's queue and the wakes waiting in it.
        const string channel = "warp_notify_sweep_live";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var publisher = CreateTransport(channel);
        await publisher.PublishAsync(NotificationKind.JobEnqueued, "default", cts.Token); // creates the contract
        await ExecuteSqlAsync($@"
            CREATE QUEUE [{channel}/queue/stalled];
            CREATE SERVICE [{channel}/service/stalled] ON QUEUE [{channel}/queue/stalled] ([{channel}/contract]);");
        await using var owner = new SqlConnection(_fixture.ConnectionString);
        await owner.OpenAsync(cts.Token);
        await using (var hold = new SqlCommand("EXEC sp_getapplock @Resource = @queue, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;", owner))
        {
            hold.Parameters.AddWithValue("@queue", $"{channel}/queue/stalled");
            await hold.ExecuteNonQueryAsync(cts.Token);
        }

        await publisher.PublishAsync(NotificationKind.JobEnqueued, "default", cts.Token); // waits in the stalled queue

        var listener = new SqlServerNotificationTransport(_fixture.ConnectionString, new WarpDatabasePushConfiguration { ChannelName = channel })
        {
            StaleQueueAge = TimeSpan.Zero,
        };
        var enumerator = listener.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var listening = Task.CompletedTask;
        try
        {
            // Act: the new listener sweeps as it starts.
            listening = enumerator.MoveNextAsync().AsTask();
            await listener.ListenerReady.WaitAsync(cts.Token);

            // Assert
            (await ServiceExistsAsync($"{channel}/service/stalled")).ShouldBeTrue("a live listener's queue was swept");
        }
        finally
        {
            await cts.CancelAsync();
            await listening.ContinueWith(_ => { }, TaskScheduler.Default);
            await DisposeQuietlyAsync(enumerator);
            await ExecuteSqlAsync($@"
                IF EXISTS (SELECT 1 FROM sys.services WHERE name = N'{channel}/service/stalled') DROP SERVICE [{channel}/service/stalled];
                IF EXISTS (SELECT 1 FROM sys.service_queues WHERE name = N'{channel}/queue/stalled') DROP QUEUE [{channel}/queue/stalled];");
        }
    }

    [TimedFact]
    public async Task Queue_MessageEnqueueTime_IsUtc()
    {
        // The sweep compares message_enqueue_time with GETUTCDATE().
        const string channel = "warp_notify_enqueue_time";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var publisher = CreateTransport(channel);
        await publisher.PublishAsync(NotificationKind.JobEnqueued, "default", cts.Token); // creates the contract
        await ExecuteSqlAsync($@"
            CREATE QUEUE [{channel}/queue/probe];
            CREATE SERVICE [{channel}/service/probe] ON QUEUE [{channel}/queue/probe] ([{channel}/contract]);");
        try
        {
            await publisher.PublishAsync(NotificationKind.JobEnqueued, "default", cts.Token);

            await using var conn = new SqlConnection(_fixture.ConnectionString);
            await conn.OpenAsync(cts.Token);
            await using var cmd = new SqlCommand($"SELECT DATEDIFF(SECOND, MIN(message_enqueue_time), GETUTCDATE()) FROM [{channel}/queue/probe];", conn);
            var ageSeconds = (int)(await cmd.ExecuteScalarAsync(cts.Token))!;

            ageSeconds.ShouldBeInRange(-2, 30);
        }
        finally
        {
            await ExecuteSqlAsync($@"
                DROP SERVICE [{channel}/service/probe];
                DROP QUEUE [{channel}/queue/probe];");
        }
    }

    // Rule 2.9 bridge (REMOVE in the release after 8.x): during a rolling deploy from 7.7, which sends to and listens
    // on ONE shared service and queue that a 7.7 process creates when it starts, the two releases still wake each other.
    [TimedFact]
    public async Task Publish_SharedQueueCreatedBy77_AlsoSendsThereSo77ListenersWake()
    {
        // Arrange
        const string channel = "warp_notify_bridge_publish";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var publisher = CreateTransport(channel);
        await CreateSharedQueueAs77DidAsync(publisher, channel, cts.Token);
        try
        {
            // Act
            await publisher.PublishAsync(NotificationKind.JobEnqueued, "default", cts.Token);

            // Assert
            (await CountMessagesAsync($"{channel}/queue", $"{channel}/msg")).ShouldBe(1);
        }
        finally
        {
            await DropSharedQueueAsync(channel);
        }
    }

    [TimedFact]
    public async Task Listen_77PublisherSendsToTheSharedQueue_TheListenerReceivesIt()
    {
        // Arrange
        const string channel = "warp_notify_bridge_receive";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = CreateTransport(channel);
        await CreateSharedQueueAs77DidAsync(listener, channel, cts.Token);
        var enumerator = listener.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var next = enumerator.MoveNextAsync().AsTask();
        try
        {
            await listener.ListenerReady.WaitAsync(cts.Token);

            // Act
            await PublishAs77DidAsync(channel, "J:legacy");

            // Assert
            (await next).ShouldBeTrue();
            enumerator.Current.Kind.ShouldBe(NotificationKind.JobEnqueued);
            enumerator.Current.Queue.ShouldBe("legacy");
        }
        finally
        {
            await cts.CancelAsync();
            await next.ContinueWith(_ => { }, TaskScheduler.Default);
            await DisposeQuietlyAsync(enumerator);
            await DropSharedQueueAsync(channel);
        }
    }

    [TimedFact]
    public async Task Listen_TwoListenersAndOne77Message_BothReceiveIt()
    {
        // Arrange — the shared queue hands the message to ONE of the two relays; it re-sends it to both listeners.
        const string channel = "warp_notify_bridge_fanout";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var first = CreateTransport(channel);
        var second = CreateTransport(channel);
        await CreateSharedQueueAs77DidAsync(first, channel, cts.Token);
        var firstEnumerator = first.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var secondEnumerator = second.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var firstMove = firstEnumerator.MoveNextAsync().AsTask();
        var secondMove = secondEnumerator.MoveNextAsync().AsTask();
        try
        {
            await first.ListenerReady.WaitAsync(cts.Token);
            await second.ListenerReady.WaitAsync(cts.Token);

            // Act
            await PublishAs77DidAsync(channel, "J:legacy");

            // Assert
            (await firstMove).ShouldBeTrue("the first listener did not receive the 7.7 notification");
            (await secondMove).ShouldBeTrue("the second listener did not receive the 7.7 notification");
            firstEnumerator.Current.Queue.ShouldBe("legacy");
            secondEnumerator.Current.Queue.ShouldBe("legacy");
        }
        finally
        {
            await cts.CancelAsync();
            await firstMove.ContinueWith(_ => { }, TaskScheduler.Default);
            await secondMove.ContinueWith(_ => { }, TaskScheduler.Default);
            await DisposeQuietlyAsync(firstEnumerator);
            await DisposeQuietlyAsync(secondEnumerator);
            await DropSharedQueueAsync(channel);
        }
    }

    [TimedFact]
    public async Task Listen_80PublishWhileTheSharedQueueExists_IsDeliveredOnce()
    {
        // Arrange — the publish reaches the listener directly AND lands in the shared queue for 7.7. The relay must
        // drop that copy: a 7.7 message sent after it is then the very next notification, not a duplicate.
        const string channel = "warp_notify_bridge_once";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = CreateTransport(channel);
        await CreateSharedQueueAs77DidAsync(listener, channel, cts.Token);
        var enumerator = listener.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Task<bool> Next() => enumerator.MoveNextAsync().AsTask();
        var next = Next();
        try
        {
            await listener.ListenerReady.WaitAsync(cts.Token);

            // Act
            await listener.PublishAsync(NotificationKind.JobEnqueued, "first", cts.Token);
            await PublishAs77DidAsync(channel, "J:second");

            // Assert
            (await next).ShouldBeTrue();
            enumerator.Current.Queue.ShouldBe("first");
            next = Next();
            (await next).ShouldBeTrue();
            enumerator.Current.Queue.ShouldBe("second");
        }
        finally
        {
            await cts.CancelAsync();
            await next.ContinueWith(_ => { }, TaskScheduler.Default);
            await DisposeQuietlyAsync(enumerator);
            await DropSharedQueueAsync(channel);
        }
    }

    [TimedFact]
    public async Task PublishListen_No77SharedQueue_CreatesNothingAndStillDelivers()
    {
        // Arrange — a deployment that never ran 7.7 has no shared queue, and this release never creates it.
        const string channel = "warp_notify_bridge_none";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transport = CreateTransport(channel);
        var enumerator = transport.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var next = enumerator.MoveNextAsync().AsTask();
        try
        {
            await transport.ListenerReady.WaitAsync(cts.Token);

            // Act
            await transport.PublishAsync(NotificationKind.JobEnqueued, "default", cts.Token);

            // Assert
            (await next).ShouldBeTrue();
            enumerator.Current.Queue.ShouldBe("default");
            (await ServiceExistsAsync($"{channel}/service")).ShouldBeFalse("the 7.7 shared service was created");
            (await QueueExistsAsync($"{channel}/queue")).ShouldBeFalse("the 7.7 shared queue was created");
        }
        finally
        {
            await cts.CancelAsync();
            await next.ContinueWith(_ => { }, TaskScheduler.Default);
            await DisposeQuietlyAsync(enumerator);
        }
    }

    [TimedFact]
    public async Task Stop_AsTheNextWaitStarts_EndsAtOnce()
    {
        // SQL Server 2025 ignores a cancel that reaches a WAITFOR (RECEIVE) as it starts until the WAITFOR times out
        // (30 s); it happened in about 40% of stops. The stop wakes the listener's queue instead. Each round stops a
        // listener right after asking for its next notification, which is when its next WAITFOR is being sent.
        const string channel = "warp_notify_stop";
        for (var round = 0; round < 10; round++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var listener = CreateTransport(channel);
            var enumerator = listener.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            Task<bool> Next() => enumerator.MoveNextAsync().AsTask();
            var pending = Next();
            try
            {
                await listener.ListenerReady.WaitAsync(cts.Token);
                await listener.PublishAsync(NotificationKind.JobEnqueued, "default", cts.Token);
                (await pending).ShouldBeTrue();

                // Act
                pending = Next();
                await cts.CancelAsync();

                // Assert
                (await EndsWithinAsync(pending, TimeSpan.FromSeconds(2))).ShouldBeTrue($"round {round}: the stopped listener's WAITFOR ran on");
            }
            finally
            {
                // Bounded, so a WAITFOR that runs on fails the assertion above rather than the test budget. The
                // iterator then stops and drops its queue by itself once that WAITFOR returns.
                await cts.CancelAsync();
                if (await EndsWithinAsync(pending, TimeSpan.FromSeconds(2)))
                {
                    await DisposeQuietlyAsync(enumerator);
                }
            }
        }
    }

    [TimedFact]
    public async Task Wake_SentToTheListenerAndToThe77SharedQueue_IsNeitherDeliveredNorRelayed()
    {
        // Arrange — a stop wakes the listener's own queue and, while its relay waits there, 7.7's shared queue,
        // where another process's relay or a 7.7 listener may receive it.
        const string channel = "warp_notify_wake";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = CreateTransport(channel);
        await CreateSharedQueueAs77DidAsync(listener, channel, cts.Token);
        var enumerator = listener.ListenAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Task<bool> Next() => enumerator.MoveNextAsync().AsTask();
        var next = Next();
        try
        {
            await listener.ListenerReady.WaitAsync(cts.Token);

            // Act
            await listener.SendWakeAsync(listener.ListenerServiceName);
            await listener.SendWakeAsync($"{channel}/service");
            await PublishAs77DidAsync(channel, "J:relayed");
            await listener.PublishAsync(NotificationKind.JobEnqueued, "direct", cts.Token);

            // Assert — exactly the two real notifications, in the order the relay and the direct send deliver them.
            var received = new List<string?>();
            for (var i = 0; i < 2; i++)
            {
                (await next).ShouldBeTrue();
                received.Add(enumerator.Current.Queue);
                next = Next();
            }

            received.ShouldBe(["relayed", "direct"], ignoreOrder: true);

            // Nothing else was delivered in between: the next notification is the next one published.
            await listener.PublishAsync(NotificationKind.JobEnqueued, "last", cts.Token);
            (await next).ShouldBeTrue();
            enumerator.Current.Queue.ShouldBe("last");
        }
        finally
        {
            await cts.CancelAsync();
            await next.ContinueWith(_ => { }, TaskScheduler.Default);
            await DisposeQuietlyAsync(enumerator);
            await DropSharedQueueAsync(channel);
        }
    }

    private static async Task<bool> EndsWithinAsync(Task pending, TimeSpan limit)
    {
        try
        {
            await pending.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(limit);

            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // 7.7's setup of the shared objects, after this release's setup created the message type and contract (7.7
    // creates the same ones).
    private async Task CreateSharedQueueAs77DidAsync(SqlServerNotificationTransport transport, string channel, CancellationToken ct)
    {
        await transport.PublishAsync(NotificationKind.MessageEnqueued, null, ct); // runs the setup
        await ExecuteSqlAsync($@"
            CREATE QUEUE [{channel}/queue];
            CREATE SERVICE [{channel}/service] ON QUEUE [{channel}/queue] ([{channel}/contract]);");
    }

    // 7.7's PublishAsync: a dialog from the shared service to itself.
    private async Task PublishAs77DidAsync(string channel, string payload)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync(Xunit.TestContext.Current.CancellationToken);
        await using var cmd = new SqlCommand(
            $@"
            DECLARE @conv UNIQUEIDENTIFIER;
            BEGIN DIALOG @conv
                FROM SERVICE [{channel}/service]
                TO SERVICE '{channel}/service'
                ON CONTRACT [{channel}/contract]
                WITH ENCRYPTION = OFF;
            SEND ON CONVERSATION @conv
                MESSAGE TYPE [{channel}/msg] (@payload);
            END CONVERSATION @conv;",
            conn);
        cmd.Parameters.Add("@payload", System.Data.SqlDbType.VarBinary, -1).Value = System.Text.Encoding.UTF8.GetBytes(payload);
        await cmd.ExecuteNonQueryAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private Task DropSharedQueueAsync(string channel) => ExecuteSqlAsync($@"
        IF EXISTS (SELECT 1 FROM sys.services WHERE name = N'{channel}/service') DROP SERVICE [{channel}/service];
        IF EXISTS (SELECT 1 FROM sys.service_queues WHERE name = N'{channel}/queue') DROP QUEUE [{channel}/queue];");

    private async Task<int> CountMessagesAsync(string queue, string messageType)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync(Xunit.TestContext.Current.CancellationToken);
        await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM [{queue}] WHERE message_type_name = @type", conn);
        cmd.Parameters.AddWithValue("@type", messageType);

        return (int)(await cmd.ExecuteScalarAsync(Xunit.TestContext.Current.CancellationToken))!;
    }

    private async Task<bool> QueueExistsAsync(string name)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync(Xunit.TestContext.Current.CancellationToken);
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.service_queues WHERE name = @name", conn);
        cmd.Parameters.AddWithValue("@name", name);

        return (int)(await cmd.ExecuteScalarAsync(Xunit.TestContext.Current.CancellationToken))! > 0;
    }

    private static async Task DisposeQuietlyAsync(IAsyncEnumerator<Notification> enumerator)
    {
        try
        {
            await enumerator.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync(Xunit.TestContext.Current.CancellationToken);
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<bool> ServiceExistsAsync(string name)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync(Xunit.TestContext.Current.CancellationToken);
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.services WHERE name = @name", conn);
        cmd.Parameters.AddWithValue("@name", name);

        return (int)(await cmd.ExecuteScalarAsync(Xunit.TestContext.Current.CancellationToken))! > 0;
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
