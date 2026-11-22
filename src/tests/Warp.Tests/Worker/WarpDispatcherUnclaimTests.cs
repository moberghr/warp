using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;

namespace Warp.Tests.Worker;

// A claim commits its rows as Processing before any of them reaches a worker. When shutdown interrupts
// the hand-over, the dispatcher must put the rows it could not deliver back to Enqueued, or they sit as
// Processing rows nobody is running until StaleJobRecovery finds them.
[GenerateDatabaseTests]
public abstract class WarpDispatcherUnclaimTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected WarpDispatcherUnclaimTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task ExecuteAsync_StoppedWhileHandingOver_ReturnsUndeliveredJobsToEnqueued()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        // Three rows as a claim leaves them. The group has room for one (one worker, no prefetch) and
        // no worker is reading, so the dispatcher hands over the first and blocks on the second.
        var claimed = new List<Job>();
        await using (var arrange = _fixture.CreateContext())
        {
            for (var i = 0; i < 3; i++)
            {
                claimed.Add(new Job
                {
                    Id = Guid.NewGuid(),
                    Kind = JobKind.Job,
                    CurrentState = State.Processing,
                    Type = "test",
                    Message = "{}",
                    CreateTime = DateTime.UtcNow,
                    ScheduleTime = DateTime.UtcNow,
                    Queue = "default",
                    CurrentWorkerId = Guid.NewGuid(),
                    LastKeepAlive = DateTime.UtcNow,
                });
            }

            arrange.Set<Job>().AddRange(claimed);
            await arrange.SaveChangesAsync(ct);
        }

        var claims = 0;
        var queries = new Mock<IWarpSqlQueries<TestContext>>();
        queries
            .Setup(x => x.ClaimEnqueuedJobsAsync(
                It.IsAny<DbContext>(),
                It.IsAny<string[]>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref claims) == 1 ? claimed : []);

        var services = new ServiceCollection();
        services.AddSingleton(queries.Object);
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        await using var provider = services.BuildServiceProvider();

        using var dispatcher = new WarpDispatcher<TestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WarpDispatcher<TestContext>>.Instance,
            Options.Create(new WarpServerConfiguration()),
            new WorkerGroupConfiguration
            {
                WorkerCount = 1,
                PrefetchCount = 0,
                Queues = ["default"],
                PollingInterval = TimeSpan.FromMilliseconds(10),
                MaxPollingInterval = TimeSpan.FromSeconds(30),
                PollingIntervalFactor = 2.0,
            },
            TimeProvider.System,
            new PauseStateHolder(),
            Guid.NewGuid(),
            new DispatcherRegistry(),
            new ServerTaskSignals<TestContext>());

        await dispatcher.StartAsync(CancellationToken.None);

        // All three are booked against capacity as soon as the claim returns, and the first is in the channel.
        await WarpTestServer.WaitUntil(
            () => Task.FromResult(dispatcher.Availability.Outstanding == 3 && dispatcher.JobReader.Count == 1),
            TimeSpan.FromSeconds(5),
            ct);

        // Act — shutdown cancels the blocked hand-over.
        await dispatcher.StopAsync(CancellationToken.None);

        // Assert — the delivered job is still Processing (a worker owns it), the other two are claimable again.
        await using var assert = _fixture.CreateContext();
        var rows = await assert.Set<Job>()
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Id, ct);

        rows[claimed[0].Id].CurrentState.ShouldBe(State.Processing);
        foreach (var undelivered in claimed.Skip(1))
        {
            var row = rows[undelivered.Id];
            row.CurrentState.ShouldBe(State.Enqueued);
            row.CurrentWorkerId.ShouldBeNull();
            row.LastKeepAlive.ShouldBeNull();
        }

        // Their capacity is handed back too; only the delivered job still holds a slot.
        dispatcher.Availability.Outstanding.ShouldBe(1);
    }
}
