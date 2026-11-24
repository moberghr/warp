using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Admin;

/// <summary>
/// The dashboard's view of cluster-wide tasks (rule 6.23): each server's task list shows them, and their
/// runs, which carry a task name instead of a per-server row, appear in its logs and are kept as long
/// as a per-server task's would be.
/// </summary>
[GenerateDatabaseTests]
public abstract class ClusterTaskDashboardTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ClusterTaskDashboardTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetServerTaskSummaries_ClusterTaskRunningElsewhere_ListedWithItsScheduleAndRunnerAndNoHeartbeat()
    {
        var now = DateTime.UtcNow;
        var server = await SeedServerAsync();
        var other = Guid.NewGuid();
        await using (var ctx = _fixture.CreateContext())
        {
            ctx.Set<ServerTask>().Add(new ServerTask { ServerId = server, TaskName = "Heartbeat", IntervalSeconds = 5 });
            ctx.Set<ClusterTask>().Add(new ClusterTask
            {
                Name = "Orchestration",
                IntervalSeconds = 10,
                NextRunAt = now.AddSeconds(10),
                LeaseUntil = now.AddSeconds(10),
                ClaimedBy = other,
                LastServerId = server,
                LastStatus = "Completed",
                LastRunAt = now.AddSeconds(-5),
            });
            await ctx.SaveChangesAsync(Ct);
        }

        // Act
        var tasks = await new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System).GetServerTaskSummaries(server);

        // Assert: the heartbeat's per-server row from before 8.0 is not listed; the server's own
        // last-heartbeat time is its record.
        var cluster = tasks.ShouldHaveSingleItem();
        cluster.TaskName.ShouldBe("Orchestration");
        cluster.ClusterWide.ShouldBeTrue();
        cluster.RunningOn.ShouldBe(other);
        cluster.LastServerId.ShouldBe(server);
    }

    [TimedFact]
    public async Task GetServerLogs_ClusterTaskName_ReturnsThatServersRunsOfIt()
    {
        var server = await SeedServerAsync();
        await using (var ctx = _fixture.CreateContext())
        {
            ctx.Set<ServerLog>().Add(new ServerLog { ServerId = server, TaskName = "Orchestration", Status = "Completed", Message = "finalized 2", Timestamp = DateTime.UtcNow });
            ctx.Set<ServerLog>().Add(new ServerLog { ServerId = server, TaskName = "ExpirationCleanup", Status = "Completed", Message = "deleted 1", Timestamp = DateTime.UtcNow });
            await ctx.SaveChangesAsync(Ct);
        }

        // Act
        var logs = await new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System)
            .GetServerLogs(server, new BaseListRequest(), "Orchestration");

        // Assert
        logs.Items.ShouldHaveSingleItem().TaskName.ShouldBe("Orchestration");
    }

    [TimedFact]
    public async Task RunCleanup_ClusterTaskLogs_KeptIntervalTimes300LikePerServerTaskLogs()
    {
        // 10 s interval: kept 50 minutes. A run of a task that no longer exists: kept a day. The log sweep
        // runs only on a pass that also deletes an expired job (pre-existing), so one is seeded.
        var now = DateTime.UtcNow;
        var server = await SeedServerAsync();
        await using (var ctx = _fixture.CreateContext())
        {
            ctx.Set<ClusterTask>().Add(new ClusterTask { Name = "Orchestration", IntervalSeconds = 10, NextRunAt = now });
            ctx.Set<Warp.Core.Entities.Job>().Add(new Warp.Core.Entities.Job
            {
                Id = Guid.NewGuid(),
                Kind = Warp.Core.Enums.JobKind.Job,
                CurrentState = Warp.Core.Enums.State.Completed,
                Type = "expired",
                Message = "{}",
                CreateTime = now.AddDays(-2),
                ScheduleTime = now.AddDays(-2),
                ExpireAt = now.AddDays(-1),
                Queue = "default",
            });
            ctx.Set<ServerLog>().Add(new ServerLog { ServerId = server, TaskName = "Orchestration", Status = "Completed", Message = "old", Timestamp = now.AddMinutes(-60) });
            ctx.Set<ServerLog>().Add(new ServerLog { ServerId = server, TaskName = "Orchestration", Status = "Completed", Message = "recent", Timestamp = now.AddMinutes(-40) });
            ctx.Set<ServerLog>().Add(new ServerLog { ServerId = server, TaskName = "Removed", Status = "Completed", Message = "gone task, recent", Timestamp = now.AddHours(-2) });
            ctx.Set<ServerLog>().Add(new ServerLog { ServerId = server, TaskName = "Removed", Status = "Completed", Message = "gone task, old", Timestamp = now.AddDays(-2) });
            await ctx.SaveChangesAsync(Ct);
        }

        // Act
        await TestTasks.CreateExpirationCleanup(_fixture.CreateContext(), TimeProvider.System).RunCleanupAsync(Ct);

        // Assert
        var left = await _fixture.CreateContext().Set<ServerLog>().AsNoTracking().Select(x => x.Message).OrderBy(x => x).ToListAsync(Ct);
        left.ShouldBe(["gone task, recent", "recent"]);
    }

    private async Task<Guid> SeedServerAsync()
    {
        var id = Guid.NewGuid();
        await using var ctx = _fixture.CreateContext();
        ctx.Set<Server>().Add(new Server { Id = id, StartedTime = DateTime.UtcNow, LastHeartbeatTime = DateTime.UtcNow });
        await ctx.SaveChangesAsync(Ct);

        return id;
    }
}
