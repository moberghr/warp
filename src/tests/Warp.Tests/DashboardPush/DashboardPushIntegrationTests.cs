using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Models;
using Warp.Dashboard.Push;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;
using XunitTestContext = Xunit.TestContext;

namespace Warp.Tests.DashboardPush;

/// <summary>
/// End-to-end integration tests for the dashboard push addon — boots a full WarpTestServer
/// with <c>AddDashboardPush()</c>, replaces the SignalR hub context with a <see cref="FakeHubContext"/>,
/// and asserts that finalizing a real job results in a hub broadcast.
/// </summary>
[Trait("Category", "PostgreSql")]
public class DashboardPushIntegrationTests_PostgreSql : DashboardPushIntegrationTestsBase, IClassFixture<PostgreSqlClassFixture>
{
    public DashboardPushIntegrationTests_PostgreSql(PostgreSqlClassFixture fixture)
        : base(fixture)
    {
    }
}

[Trait("Category", "SqlServer")]
public class DashboardPushIntegrationTests_SqlServer : DashboardPushIntegrationTestsBase, IClassFixture<SqlServerClassFixture>
{
    public DashboardPushIntegrationTests_SqlServer(SqlServerClassFixture fixture)
        : base(fixture)
    {
    }
}

public abstract class DashboardPushIntegrationTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected DashboardPushIntegrationTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task JobFinalized_FiresHubBroadcast()
    {
        var fakeHub = new FakeHubContext();

        await using var server = await WarpTestServer.StartAsync(
            fixture: _fixture,
            configure: cfg => cfg.AddDashboardPush(o => o.CoalesceWindow = TimeSpan.FromMilliseconds(50)),
            configureServices: services =>
            {
                services.RemoveAll<IHubContext<WarpDashboardHub>>();
                services.AddSingleton<IHubContext<WarpDashboardHub>>(fakeHub);
            });

        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new CounterRequest());
        await publisher.SaveChangesAsync(XunitTestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Completed, TimeSpan.FromSeconds(5));

        await fakeHub.WaitForMethodAsync("JobFinalized");
        fakeHub.CountOf("JobFinalized").ShouldBeGreaterThanOrEqualTo(1);

        // Wire-shape contract: every JobFinalized broadcast carries the current
        // DashboardStatistics DTO as its single argument. The SPA reads it directly
        // into the dashboard store. Breaking this contract silently regresses the
        // push-data optimization — assert here so the test fails fast on regression.
        var firstJobFinalized = fakeHub.Broadcasts.First(x => string.Equals(x.Method, "JobFinalized", StringComparison.Ordinal));
        firstJobFinalized.Args.Length.ShouldBe(1);
        firstJobFinalized.Args[0].ShouldBeOfType<DashboardStatistics>();
    }

    [TimedFact]
    public async Task MessagePublished_WithDatabasePush_FiresMessageEnqueuedBroadcast()
    {
        // The MessageEnqueued signal is only fired by NotificationListenerTask (the
        // DB-push consumer). Without UseDatabasePush, the publisher writes the row but no
        // signal reaches the broadcaster — MessageRouter discovers the row via polling
        // instead. This test verifies the integrated path: publish → DB push notification
        // → listener fires SignalMessageEnqueued → broadcaster broadcasts.
        var fakeHub = new FakeHubContext();

        await using var server = await WarpTestServer.StartAsync(
            fixture: _fixture,
            configure: cfg =>
            {
                cfg.UseDatabasePush(o => o.ChannelName = $"warp_push_dash_{Guid.NewGuid():N}");
                cfg.AddDashboardPush(o => o.CoalesceWindow = TimeSpan.FromMilliseconds(50));
            },
            configureServices: services =>
            {
                services.RemoveAll<IHubContext<WarpDashboardHub>>();
                services.AddSingleton<IHubContext<WarpDashboardHub>>(fakeHub);
            });

        var publisher = server.CreatePublisher();
        await publisher.Publish(new SingleHandlerMessage());
        await publisher.SaveChangesAsync(XunitTestContext.Current.CancellationToken);

        await server.WaitForCompletion(TimeSpan.FromSeconds(5));

        await fakeHub.WaitForMethodAsync("MessageEnqueued");
        fakeHub.CountOf("MessageEnqueued").ShouldBeGreaterThanOrEqualTo(1);

        // Wire-shape contract: MessageEnqueued broadcasts also carry the stats DTO.
        var firstMessageEnqueued = fakeHub.Broadcasts.First(x => string.Equals(x.Method, "MessageEnqueued", StringComparison.Ordinal));
        firstMessageEnqueued.Args.Length.ShouldBe(1);
        firstMessageEnqueued.Args[0].ShouldBeOfType<DashboardStatistics>();
    }
}
