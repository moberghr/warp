using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Observability;

[GenerateDatabaseTests]
public abstract class StatsTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected StatsTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetStatsHistory_ReturnsHourlyData()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var hourKey = $"stats:succeeded:{DateTime.UtcNow:yyyy-MM-dd-HH}";
        ctx.Set<Statistic>().Add(new Statistic { Key = hourKey, Value = 5 });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var history = await svc.GetStatsHistory(24);

        // Assert
        history.Count.ShouldBeGreaterThanOrEqualTo(1);
        history.ShouldContain(p => p.Succeeded >= 5);
    }

    [TimedFact]
    public async Task GetWarpStatus_ReturnsCorrectCounts()
    {
        // Arrange
        var ctx = _fixture.CreateContext();

        for (var i = 0; i < 3; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Enqueued,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
            });
        }

        for (var i = 0; i < 2; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
            });
        }

        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var status = await svc.GetWarpStatus();

        // Assert
        status.Created.ShouldBe(3);
        status.Failed.ShouldBe(2);
        status.Completed.ShouldBe(1);
        status.Total.ShouldBe(6);
    }

    [TimedFact]
    public async Task GetServers_ReturnsRegisteredServers()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var serverId = Guid.NewGuid();
        var worker1Id = Guid.NewGuid();
        var worker2Id = Guid.NewGuid();

        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
            ServiceCount = 2,
        });
        ctx.Set<Warp.Core.Data.Entities.Worker>().Add(new Warp.Core.Data.Entities.Worker
        {
            Id = worker1Id,
            ServerId = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
        });
        ctx.Set<Warp.Core.Data.Entities.Worker>().Add(new Warp.Core.Data.Entities.Worker
        {
            Id = worker2Id,
            ServerId = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var servers = await svc.GetServers();

        // Assert
        servers.Count.ShouldBe(1);
        servers[0].Id.ShouldBe(serverId);
        servers[0].Workers.Count.ShouldBe(2);
    }
}
