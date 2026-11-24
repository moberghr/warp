using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
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

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task<Guid> SeedServerAsync()
    {
        var serverId = Guid.NewGuid();
        await using var ctx = _fixture.CreateContext();
        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
            ServiceCount = 1,
        });
        await ctx.SaveChangesAsync(Ct);

        return serverId;
    }

    [TimedFact]
    public async Task GetServerLogs_ReturnsPaginatedLogs()
    {
        // Arrange
        var serverId = await SeedServerAsync();
        await using (var ctx = _fixture.CreateContext())
        {
            for (var i = 0; i < 5; i++)
            {
                ctx.Set<ServerLog>().Add(new ServerLog
                {
                    ServerId = serverId,
                    TaskName = "StaleJobRecovery",
                    Status = "Success",
                    Message = $"Log entry {i}",
                    Timestamp = DateTime.UtcNow.AddMinutes(-i),
                });
            }

            await ctx.SaveChangesAsync(Ct);
        }

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var logs = await svc.GetServerLogs(serverId, new BaseListRequest { Page = 0, PageSize = 3 });

        // Assert
        logs.TotalCount.ShouldBe(5);
        logs.Items.Count.ShouldBe(3);
        logs.PageCount.ShouldBe(2);
    }

    [TimedFact]
    public async Task GetServerLogs_FilteredByTaskName()
    {
        // Arrange
        var serverId = await SeedServerAsync();
        await using (var ctx = _fixture.CreateContext())
        {
            for (var i = 0; i < 3; i++)
            {
                ctx.Set<ServerLog>().Add(new ServerLog
                {
                    ServerId = serverId,
                    TaskName = "StaleJobRecovery",
                    Status = "Success",
                    Message = $"Recovery log {i}",
                    Timestamp = DateTime.UtcNow.AddMinutes(-i),
                });
            }

            for (var i = 0; i < 2; i++)
            {
                ctx.Set<ServerLog>().Add(new ServerLog
                {
                    ServerId = serverId,
                    TaskName = "ExpirationCleanup",
                    Status = "Success",
                    Message = $"Cleanup log {i}",
                    Timestamp = DateTime.UtcNow.AddMinutes(-i),
                });
            }

            await ctx.SaveChangesAsync(Ct);
        }

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var logs = await svc.GetServerLogs(serverId, new BaseListRequest { Page = 0, PageSize = 20 }, taskName: "StaleJobRecovery");

        // Assert
        logs.TotalCount.ShouldBe(3);
        logs.Items.ShouldAllBe(l => string.Equals(l.TaskName, "StaleJobRecovery", StringComparison.Ordinal));
    }

    [TimedFact]
    public async Task GetServerTaskSummaries_ReturnsTheClusterTasks()
    {
        // Arrange
        var serverId = await SeedServerAsync();
        await using (var ctx = _fixture.CreateContext())
        {
            ctx.Set<ClusterTask>().Add(new ClusterTask
            {
                Name = "StaleJobRecovery",
                IntervalSeconds = 60,
                NextRunAt = DateTime.UtcNow.AddSeconds(30),
                LastStatus = "Success",
                LastMessage = "Requeued 2 stale jobs",
                LastRunAt = DateTime.UtcNow.AddMinutes(-1),
                LastServerId = serverId,
                LastDurationMs = 42.5,
            });
            ctx.Set<ClusterTask>().Add(new ClusterTask
            {
                Name = "ExpirationCleanup",
                IntervalSeconds = 120,
                NextRunAt = DateTime.UtcNow.AddSeconds(60),
                LastStatus = "Success",
                LastMessage = "Cleaned 0 expired jobs",
                LastRunAt = DateTime.UtcNow.AddMinutes(-2),
                LastDurationMs = 15.3,
            });
            await ctx.SaveChangesAsync(Ct);
        }

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var summaries = await svc.GetServerTaskSummaries(serverId);

        // Assert
        summaries.Count.ShouldBe(2);
        var recovery = summaries.First(s => string.Equals(s.TaskName, "StaleJobRecovery", StringComparison.Ordinal));
        recovery.IntervalSeconds.ShouldBe(60);
        recovery.LastStatus.ShouldBe("Success");
        recovery.LastMessage.ShouldBe("Requeued 2 stale jobs");
        recovery.LastDurationMs.ShouldBe(42.5);
        recovery.LastServerId.ShouldBe(serverId);
    }

    [TimedFact]
    public async Task GetServerTaskSummaries_OrdersByTaskName()
    {
        // Arrange
        var serverId = await SeedServerAsync();
        await using (var ctx = _fixture.CreateContext())
        {
            // Inserted in deliberately non-alphabetical order.
            foreach (var name in new[] { "StaleJobRecovery", "Orchestrator", "MessageRouter", "ExpirationCleanup" })
            {
                ctx.Set<ClusterTask>().Add(new ClusterTask { Name = name, IntervalSeconds = 60, NextRunAt = DateTime.UtcNow });
            }

            await ctx.SaveChangesAsync(Ct);
        }

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var summaries = await svc.GetServerTaskSummaries(serverId);

        // Assert
        summaries.Select(s => s.TaskName).ShouldBe(["ExpirationCleanup", "MessageRouter", "Orchestrator", "StaleJobRecovery"]);
    }

    // In dispatcher mode a running job carries its worker GROUP's id from the claim until its first
    // keep-alive, so several running jobs can share one id. The servers page must still load.
    [TimedFact]
    public async Task GetServers_SeveralRunningJobsUnderOneId_StillLoads()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        var groupId = Guid.NewGuid();
        var server = new Server { Id = Guid.NewGuid(), StartedTime = now, LastHeartbeatTime = now, ServiceCount = 1 };
        await using (var seed = _fixture.CreateContext())
        {
            seed.Set<Server>().Add(server);
            seed.Set<Job>().AddRange(Enumerable.Range(0, 2).Select(_ =>
                new Job
                {
                    Id = Guid.NewGuid(),
                    Kind = JobKind.Job,
                    CurrentState = State.Processing,
                    CurrentWorkerId = groupId,
                    CreateTime = now,
                    ScheduleTime = now,
                    Queue = "default",
                }));
            await seed.SaveChangesAsync(ct);
        }

        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System);

        (await svc.GetServers()).Select(x => x.Id).ShouldBe([server.Id]);
        (await svc.GetServerById(server.Id)).ShouldNotBeNull();
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
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var servers = await svc.GetServers();

        // Assert
        servers.Select(s => s.Id).ShouldBe([oldest.Id, middle.Id, newest.Id]);
    }
}
