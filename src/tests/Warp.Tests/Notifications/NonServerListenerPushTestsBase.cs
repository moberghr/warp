using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Warp.Core;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Tests.Fixtures;
using Warp.Worker;

namespace Warp.Tests.Notifications;

// Capability regression: the DB-push notification LISTENER now lives in Warp.Core and no longer
// depends on the server-only WarpServerConfiguration / DispatcherRegistry. That lets an
// AddWarp-ONLY process (a non-server publisher / dashboard host — NO AddWarpServer) both CONSTRUCT
// and RUN the listener, so it receives cross-process DB NOTIFYs and republishes them onto the local
// ServerTaskSignals pipe — the channel a non-server dashboard host's DashboardBroadcaster subscribes
// to for realtime push (§2.9/§2.10). Before the move, the listener's ctor required deps only
// AddWarpServer registered, so this shape could not construct it at all.
[GenerateDatabaseTests(WithPush = true)]
public abstract class NonServerListenerPushTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected NonServerListenerPushTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task AddWarpOnlyProcess_ReceivesCrossProcessNotifications_RepublishesOnLocalSignals()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        string connectionString;
        bool isPostgres;
        await using (var probe = _fixture.CreateContext())
        {
            connectionString = probe.Database.GetConnectionString()!;
            isPostgres = probe.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
        }

        // Unique channel per run so parallel provider variants don't cross-deliver.
        var channel = "warp_nonserver_push_" + Guid.NewGuid().ToString("N");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(options =>
        {
            if (isPostgres)
            {
                options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
            }
            else
            {
                options.UseSqlServer(connectionString);
            }
        });

        // The whole point: AddWarp only — NO AddWarpServer. This process runs no server, no worker,
        // and no server tasks, yet opts into a provider + DB push.
        services.AddWarp<TestContext>(opt =>
        {
            if (isPostgres)
            {
                opt.UsePostgreSql();
            }
            else
            {
                opt.UseSqlServer();
            }

            opt.UseDatabasePush(o => o.ChannelName = channel);
        });

        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Capability #1 (impossible before the Core move): the listener CONSTRUCTS in an AddWarp-only
        // graph. Its ctor previously took IOptions<WarpServerConfiguration> + DispatcherRegistry —
        // neither registered without AddWarpServer — so resolving the hosted service would have thrown.
        var listener = sp.GetServices<IHostedService>()
            .OfType<NotificationListenerTask<TestContext>>()
            .Single();

        // A non-server dashboard host wakes its DashboardBroadcaster off these exact signals.
        var signals = sp.GetRequiredService<ServerTaskSignals<TestContext>>();

        // Capability #2: the running listener republishes a received cross-process notification onto
        // the local signals.
        await listener.StartAsync(ct);
        try
        {
            // Gate on the listener's transport actually being on the wire before we publish, so the
            // NOTIFY can't be dropped by a startup race. This also guarantees the listener's
            // drain-on-connect (which fires every local signal once, unconditionally) has already run
            // BEFORE we subscribe below — so a completed TCS can only come from the published
            // notification, never from the connect-time drain.
            var transport = sp.GetRequiredService<IWarpNotificationTransport>();
            await transport.ListenerReady.WaitAsync(ct);

            var finalizedFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var messageFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var finalizedSub = signals.Subscribe(ServerTaskSignal.JobFinalized, () => finalizedFired.TrySetResult());
            using var messageSub = signals.Subscribe(ServerTaskSignal.MessageEnqueued, () => messageFired.TrySetResult());

            // Publish from a SEPARATE transport instance built off the same provider factory — a
            // genuine second "process" writing to the same channel, exactly the cross-process path
            // (IWarpNotificationTransport.PublishAsync) production callers use post-commit.
            var factory = sp.GetRequiredService<IWarpNotificationTransportFactory>();
            var pushConfig = sp.GetRequiredService<WarpDatabasePushConfiguration>();
            var publisher = factory.Create(connectionString, pushConfig, sp.GetRequiredService<ILoggerFactory>());

            await publisher.PublishAsync(NotificationKind.JobFinalized, null, ct);
            await publisher.PublishAsync(NotificationKind.MessageEnqueued, null, ct);

            await finalizedFired.Task.WaitAsync(ct);
            await messageFired.Task.WaitAsync(ct);

            finalizedFired.Task.IsCompletedSuccessfully.ShouldBeTrue(
                "the AddWarp-only listener must republish a cross-process JobFinalized NOTIFY onto local signals");
            messageFired.Task.IsCompletedSuccessfully.ShouldBeTrue(
                "the AddWarp-only listener must republish a cross-process MessageEnqueued NOTIFY onto local signals");
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await listener.StopAsync(stopCts.Token);
        }
    }
}
