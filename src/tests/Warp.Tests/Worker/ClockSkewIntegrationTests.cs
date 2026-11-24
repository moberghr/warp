using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker.Services;

namespace Warp.Tests.Worker;

// #350: a server whose own clock is hours ahead of the database's. It compares times other servers wrote
// (keep-alives, heartbeats, schedule times) on the database's clock, measured by its heartbeat, so it must not
// requeue another server's running job, remove a live server, or fire a schedule early.
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class ClockSkewIntegrationTestsBase : IntegrationTestBase
{
    private static readonly TimeSpan Skew = TimeSpan.FromHours(3);

    protected ClockSkewIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task StaleJobRecovery_ServerClockHoursAhead_LeavesAnotherServersRunningJob()
    {
        // Arrange — renewed a moment ago by another server; one minute of InvisibilityTimeout away from stale.
        await using var server = await StartSkewedAsync();
        var jobId = await InsertAsync(new Job
        {
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            Type = "test",
            Message = "{}",
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            CurrentWorkerId = Guid.NewGuid(),
            LastKeepAlive = DateTime.UtcNow,
            RunId = Guid.NewGuid(),
        });

        // Act
        await server.RunServerTaskThroughLoopAsync<StaleJobRecovery<TestContext>>(Xunit.TestContext.Current.CancellationToken);

        // Assert
        (await server.GetJob(jobId)).CurrentState.ShouldBe(State.Processing);
    }

    [TimedFact]
    public async Task ServerCleanup_ServerClockHoursAhead_KeepsALiveServer()
    {
        // Arrange — another server that heartbeat a moment ago.
        await using var server = await StartSkewedAsync();
        var otherId = Guid.NewGuid();
        await using (var context = Fixture.CreateContext())
        {
            context.Set<Server>().Add(new Server
            {
                Id = otherId,
                StartedTime = DateTime.UtcNow,
                LastHeartbeatTime = DateTime.UtcNow,
            });
            await context.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        }

        // Act
        await server.RunServerTaskThroughLoopAsync<ServerCleanup<TestContext>>(Xunit.TestContext.Current.CancellationToken);

        // Assert
        await using var read = Fixture.CreateContext();
        (await read.Set<Server>().AnyAsync(x => x.Id == otherId, Xunit.TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [TimedFact]
    public async Task ScheduledJobActivation_ServerClockHoursAhead_DoesNotFireAJobDueInAnHour()
    {
        // Arrange
        await using var server = await StartSkewedAsync();
        var jobId = await InsertAsync(new Job
        {
            Kind = JobKind.Job,
            CurrentState = State.Scheduled,
            Type = "test",
            Message = "{}",
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddHours(1),
            Queue = "default",
        });

        // Act
        await server.RunServerTaskOnceAsync<ScheduledJobActivation<TestContext>>(Xunit.TestContext.Current.CancellationToken);

        // Assert
        (await server.GetJob(jobId)).CurrentState.ShouldBe(State.Scheduled);
    }

    [TimedFact]
    public async Task Heartbeat_ServerClockHoursAhead_WritesTheDatabaseTimeAndRecordsTheOffset()
    {
        // Arrange
        await using var server = await StartSkewedAsync();

        // Act
        await server.RunHeartbeatOnceAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert — the row carries the database's time, and how far this server's clock is off.
        await using var read = Fixture.CreateContext();
        var row = await read.Set<Server>()
            .AsNoTracking()
            .Where(x => x.Id == server.ServerId)
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);
        row.LastHeartbeatTime.ShouldBe(DateTime.UtcNow, TimeSpan.FromSeconds(30));
        row.ClockOffsetMs.ShouldNotBeNull();
        row.ClockOffsetMs.Value.ShouldBe(-Skew.TotalMilliseconds, TimeSpan.FromSeconds(30).TotalMilliseconds);
    }

    private Task<WarpTestServer> StartSkewedAsync() =>
        WarpTestServer.StartAsync(
            Fixture,
            cfg =>
            {
                // Driven by hand below: an hour keeps the host's own loop from running them again meanwhile.
                cfg.StaleJobRecoveryInterval = TimeSpan.FromHours(1);
                cfg.ServerCleanupInterval = TimeSpan.FromHours(1);
            },
            services => services.AddSingleton<TimeProvider>(new SkewedTimeProvider(Skew)));

    private async Task<Guid> InsertAsync(Job job)
    {
        await using var context = Fixture.CreateContext();
        context.Set<Job>().Add(job);
        await context.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return job.Id;
    }

    private sealed class SkewedTimeProvider(TimeSpan skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + skew;
    }
}
