using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Notifications;

/// <summary>
/// End-to-end integration tests for UseDatabasePush on SQL Server (Service Broker).
/// Mirrors PostgresDatabasePushIntegrationTests — each test starts a fresh WarpTestServer
/// with UseDispatcher=true, long PollingInterval, and DB push enabled, then asserts that
/// jobs are picked up quickly via push (not via the long poll).
/// </summary>
[Trait("Category", "SqlServer")]
public class SqlServerDatabasePushIntegrationTests : IAsyncLifetime, IClassFixture<SqlServerPushClassFixture>
{
    private readonly SqlServerPushClassFixture _fixture;

    public SqlServerDatabasePushIntegrationTests(SqlServerPushClassFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact(20_000)]
    public async Task JobEnqueued_WithDispatcherPlusPush_DispatcherPicksUpWithoutPolling()
    {
        await using var server = await WarpTestServer.StartAsync(
            fixture: _fixture,
            configure: cfg =>
            {
                cfg.UseDispatcher = true;
                cfg.PollingInterval = TimeSpan.FromSeconds(10);
                cfg.MaxPollingInterval = TimeSpan.FromSeconds(10);
                cfg.PollingIntervalFactor = 1.0;
                cfg.UseDatabasePush(o => o.ChannelName = "warp_push_it_job_mssql");
            });

        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new CounterRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var sw = Stopwatch.StartNew();
        await server.WaitForJobState(jobId, State.Completed, TimeSpan.FromSeconds(5));
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3), "Push should wake the dispatcher in <3s even though PollingInterval=10s");
    }

    [TimedFact(20_000)]
    public async Task MessageEnqueued_WithPush_MessageRoutingWakesImmediately()
    {
        await using var server = await WarpTestServer.StartAsync(
            fixture: _fixture,
            configure: cfg =>
            {
                cfg.UseDispatcher = true;
                cfg.PollingInterval = TimeSpan.FromSeconds(10);
                cfg.MaxPollingInterval = TimeSpan.FromSeconds(10);
                cfg.MessageRoutingInterval = TimeSpan.FromSeconds(10);
                cfg.PollingIntervalFactor = 1.0;
                cfg.UseDatabasePush(o => o.ChannelName = "warp_push_it_msg_mssql");
            });

        var publisher = server.CreatePublisher();
        var messageId = await publisher.Publish(new SingleHandlerMessage());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var sw = Stopwatch.StartNew();
        await server.WaitForCompletion(TimeSpan.FromSeconds(8));
        sw.Stop();

        var ctx = server.CreateContext();
        var message = await ctx.Set<Job>().FirstAsync(j => j.Id == messageId, Xunit.TestContext.Current.CancellationToken);
        message.CurrentState.ShouldBe(State.Completed);

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "Push should wake MessageRoutingTask AND the dispatcher within 5s even though both intervals are 10s");
    }

    [TimedFact(20_000)]
    public async Task PushEnabled_WithoutDispatcher_ProcessesWork()
    {
        // UseDispatcher=false + push: there is no dispatcher to wake, but the listener still signals
        // ServerTaskSignals.JobEnqueued, which every bare worker subscribes to (§2.9), and MessageRoutingTask
        // and OrchestrationTask wake as usual. The system processes work in single-worker mode.
        await using var server = await WarpTestServer.StartAsync(
            fixture: _fixture,
            configure: cfg =>
            {
                cfg.UseDispatcher = false;
                cfg.PollingInterval = TimeSpan.FromMilliseconds(200);
                cfg.UseDatabasePush(o => o.ChannelName = "warp_push_it_noDispatch_mssql");
            });

        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new CounterRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Completed, TimeSpan.FromSeconds(10));
    }
}
