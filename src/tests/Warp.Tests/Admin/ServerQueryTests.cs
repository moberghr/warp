using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Admin;

[GenerateDatabaseTests]
public abstract class ServerQueryTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ServerQueryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetServerLogs_ReturnsPaginatedLogs()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var serverId = Guid.NewGuid();

        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
            ServiceCount = 1,
        });

        var task = new ServerTask
        {
            ServerId = serverId,
            TaskName = "StaleJobRecovery",
            IntervalSeconds = 60,
        };
        ctx.Set<ServerTask>().Add(task);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Insert 5 log entries
        for (var i = 0; i < 5; i++)
        {
            ctx.Set<ServerLog>().Add(new ServerLog
            {
                ServerId = serverId,
                ServerTaskId = task.Id,
                Status = "Success",
                Message = $"Log entry {i}",
                Timestamp = DateTime.UtcNow.AddMinutes(-i),
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var request = new BaseListRequest { Page = 0, PageSize = 3 };
        var logs = await svc.GetServerLogs(serverId, request);

        // Assert
        logs.TotalCount.ShouldBe(5);
        logs.Items.Count.ShouldBe(3);
        logs.PageCount.ShouldBe(2);
    }

    [TimedFact]
    public async Task GetServerLogs_FilteredByTaskName()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var serverId = Guid.NewGuid();

        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
            ServiceCount = 1,
        });

        var task1 = new ServerTask
        {
            ServerId = serverId,
            TaskName = "StaleJobRecovery",
            IntervalSeconds = 60,
        };
        var task2 = new ServerTask
        {
            ServerId = serverId,
            TaskName = "ExpirationCleanup",
            IntervalSeconds = 120,
        };
        ctx.Set<ServerTask>().Add(task1);
        ctx.Set<ServerTask>().Add(task2);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Insert logs for task1
        for (var i = 0; i < 3; i++)
        {
            ctx.Set<ServerLog>().Add(new ServerLog
            {
                ServerId = serverId,
                ServerTaskId = task1.Id,
                Status = "Success",
                Message = $"Recovery log {i}",
                Timestamp = DateTime.UtcNow.AddMinutes(-i),
            });
        }

        // Insert logs for task2
        for (var i = 0; i < 2; i++)
        {
            ctx.Set<ServerLog>().Add(new ServerLog
            {
                ServerId = serverId,
                ServerTaskId = task2.Id,
                Status = "Success",
                Message = $"Cleanup log {i}",
                Timestamp = DateTime.UtcNow.AddMinutes(-i),
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var request = new BaseListRequest { Page = 0, PageSize = 20 };
        var logs = await svc.GetServerLogs(serverId, request, taskName: "StaleJobRecovery");

        // Assert
        logs.TotalCount.ShouldBe(3);
        logs.Items.ShouldAllBe(l => string.Equals(l.TaskName, "StaleJobRecovery", StringComparison.Ordinal));
    }

    [TimedFact]
    public async Task GetServerTaskSummaries_ReturnsRegisteredTasks()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var serverId = Guid.NewGuid();

        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
            ServiceCount = 1,
        });

        ctx.Set<ServerTask>().Add(new ServerTask
        {
            ServerId = serverId,
            TaskName = "StaleJobRecovery",
            IntervalSeconds = 60,
            LastStatus = "Success",
            LastMessage = "Requeued 2 stale jobs",
            LastRun = DateTime.UtcNow.AddMinutes(-1),
            LastDurationMs = 42.5,
        });
        ctx.Set<ServerTask>().Add(new ServerTask
        {
            ServerId = serverId,
            TaskName = "ExpirationCleanup",
            IntervalSeconds = 120,
            LastStatus = "Success",
            LastMessage = "Cleaned 0 expired jobs",
            LastRun = DateTime.UtcNow.AddMinutes(-2),
            LastDurationMs = 15.3,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var summaries = await svc.GetServerTaskSummaries(serverId);

        // Assert
        summaries.Count.ShouldBe(2);
        summaries.ShouldContain(s => string.Equals(s.TaskName, "StaleJobRecovery", StringComparison.Ordinal));
        summaries.ShouldContain(s => string.Equals(s.TaskName, "ExpirationCleanup", StringComparison.Ordinal));

        var recovery = summaries.First(s => string.Equals(s.TaskName, "StaleJobRecovery", StringComparison.Ordinal));
        recovery.IntervalSeconds.ShouldBe(60);
        recovery.LastStatus.ShouldBe("Success");
        recovery.LastMessage.ShouldBe("Requeued 2 stale jobs");
        recovery.LastDurationMs.ShouldBe(42.5);
    }

    [TimedFact]
    public async Task GetServerTaskSummaries_OrdersByTaskName()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var serverId = Guid.NewGuid();

        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
            ServiceCount = 1,
        });

        // Insert in deliberately non-alphabetical order
        foreach (var name in new[] { "StaleJobRecovery", "Heartbeat", "MessageRouter", "ExpirationCleanup" })
        {
            ctx.Set<ServerTask>().Add(new ServerTask
            {
                ServerId = serverId,
                TaskName = name,
                IntervalSeconds = 60,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var summaries = await svc.GetServerTaskSummaries(serverId);

        // Assert
        summaries.Select(s => s.TaskName).ShouldBe(["ExpirationCleanup", "Heartbeat", "MessageRouter", "StaleJobRecovery"]);
    }

    [TimedFact]
    public async Task GetServers_OrdersByStartedTime()
    {
        // Arrange — insert in non-chronological order; the query must return them by StartedTime
        var ctx = _fixture.CreateContext();
        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        var oldest = new Server { Id = Guid.NewGuid(), StartedTime = baseTime, LastHeartbeatTime = baseTime, ServiceCount = 1 };
        var middle = new Server { Id = Guid.NewGuid(), StartedTime = baseTime.AddMinutes(3), LastHeartbeatTime = baseTime, ServiceCount = 1 };
        var newest = new Server { Id = Guid.NewGuid(), StartedTime = baseTime.AddMinutes(6), LastHeartbeatTime = baseTime, ServiceCount = 1 };

        ctx.Set<Server>().Add(middle);
        ctx.Set<Server>().Add(newest);
        ctx.Set<Server>().Add(oldest);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var servers = await svc.GetServers();

        // Assert
        servers.Select(s => s.Id).ShouldBe([oldest.Id, middle.Id, newest.Id]);
    }
}
