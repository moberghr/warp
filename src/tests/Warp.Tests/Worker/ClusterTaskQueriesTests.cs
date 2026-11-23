using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Worker;

/// <summary>
/// The cluster-wide task statements (rule 6.20) against a real database: one claim for every due task,
/// the lease that keeps a second server out, the idle backoff, run requests, renewal and completion.
/// </summary>
[GenerateDatabaseTests]
public abstract class ClusterTaskQueriesTestsBase : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private readonly IDatabaseFixture _fixture;

    protected ClusterTaskQueriesTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task ClaimClusterTasks_DueTask_ClaimsItAndMovesItsNextRunOneInterval()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = Now.AddSeconds(-1) });
        var server = Guid.NewGuid();

        // Act
        var claims = await ClaimAsync(["a"], server, Now);

        // Assert
        claims.Tasks.ShouldHaveSingleItem().ShouldBe(new ClusterTaskState("a", true, Now.AddSeconds(10)));
        var row = await ReadAsync("a");
        row.ClaimedBy.ShouldBe(server);
        row.LeaseUntil.ShouldBe(Now.AddSeconds(10));
        row.IdleStreak.ShouldBe(1);
    }

    [TimedFact]
    public async Task ClaimClusterTasks_LeaseHeldByAnotherServer_DoesNotClaimAndReportsWhenItIsFree()
    {
        // The lease is what keeps the task on one server: a second server asking while the first holds
        // it gets nothing, and learns when to look again.
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = Now.AddSeconds(-1) });
        await ClaimAsync(["a"], Guid.NewGuid(), Now);

        // Act
        var second = await ClaimAsync(["a"], Guid.NewGuid(), Now.AddSeconds(1));

        // Assert
        second.Tasks.ShouldHaveSingleItem().ShouldBe(new ClusterTaskState("a", false, Now.AddSeconds(10)));
    }

    [TimedFact]
    public async Task ClaimClusterTasks_IdleStreak_BacksOffUpToTheLimit()
    {
        // Three idle runs in a row: 10 s doubled three times is 80 s, capped at the 60 s limit.
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, IdleBackoffLimitSeconds = 60, IdleStreak = 3, NextRunAt = Now });

        // Act
        var claims = await ClaimAsync(["a"], Guid.NewGuid(), Now);

        // Assert
        claims.Tasks.ShouldHaveSingleItem().DueAt.ShouldBe(Now.AddSeconds(60));
        (await ReadAsync("a")).IdleStreak.ShouldBe(4);
    }

    [TimedFact]
    public async Task RequestClusterTaskRun_LeaseHeldElsewhere_MarksTheRequestAndTheNextClaimRunsIt()
    {
        // A signal on a server that does not hold the lease asks the holder's next turn to run it; the
        // next claim after the lease takes it even though its schedule is not due.
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = Now });
        await ClaimAsync(["a"], Guid.NewGuid(), Now);

        // Act
        var claimedNow = await Queries().RequestClusterTaskRunAsync(_fixture.CreateContext(), "a", Guid.NewGuid(), Now.AddSeconds(1), CancellationToken.None);

        // Assert
        claimedNow.ShouldBeFalse();
        (await ReadAsync("a")).RunRequestedAt.ShouldBe(Now.AddSeconds(1));
        var afterLease = await ClaimAsync(["a"], Guid.NewGuid(), Now.AddSeconds(10));
        afterLease.Tasks.ShouldHaveSingleItem().Claimed.ShouldBeTrue();
    }

    [TimedFact]
    public async Task RequestClusterTaskRun_LeaseFree_ClaimsItAtOnceAndResetsTheBackoff()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, IdleStreak = 5, NextRunAt = Now.AddMinutes(5) });
        var server = Guid.NewGuid();

        // Act
        var claimedNow = await Queries().RequestClusterTaskRunAsync(_fixture.CreateContext(), "a", server, Now, CancellationToken.None);

        // Assert
        claimedNow.ShouldBeTrue();
        var row = await ReadAsync("a");
        row.ClaimedBy.ShouldBe(server);
        row.IdleStreak.ShouldBe(0);
    }

    [TimedFact]
    public async Task RenewClusterTaskLease_AnotherServerHoldsIt_ReturnsFalse()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = Now });
        await ClaimAsync(["a"], Guid.NewGuid(), Now);

        // Act
        var renewed = await Queries().RenewClusterTaskLeaseAsync(_fixture.CreateContext(), "a", Guid.NewGuid(), Now.AddSeconds(3), CancellationToken.None);

        // Assert
        renewed.ShouldBeFalse();
    }

    [TimedFact]
    public async Task CompleteClusterTask_RunThatDidWork_FreesTheLeaseResetsTheBackoffAndRecordsIt()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, IdleStreak = 2, NextRunAt = Now });
        var server = Guid.NewGuid();
        await ClaimAsync(["a"], server, Now);

        // Act
        await Queries().CompleteClusterTaskAsync(
            _fixture.CreateContext(),
            new ClusterTaskCompletion("a", server, Now.AddSeconds(2), DidWork: true, Rerun: false, Record: true, "Completed", "did 3", 12.5),
            CancellationToken.None);

        // Assert
        var row = await ReadAsync("a");
        row.LeaseUntil.ShouldBeNull();
        row.ClaimedBy.ShouldBeNull();
        row.IdleStreak.ShouldBe(0);
        row.NextRunAt.ShouldBe(Now.AddSeconds(12));
        row.LastServerId.ShouldBe(server);
        row.LastMessage.ShouldBe("did 3");
        row.LastDurationMs.ShouldBe(12.5);
    }

    [TimedFact]
    public async Task CompleteClusterTask_RunRequestedMeanwhile_IsDueAtOnce()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = Now });
        var server = Guid.NewGuid();
        await ClaimAsync(["a"], server, Now);
        await Queries().RequestClusterTaskRunAsync(_fixture.CreateContext(), "a", Guid.NewGuid(), Now.AddSeconds(1), CancellationToken.None);

        // Act
        await Queries().CompleteClusterTaskAsync(
            _fixture.CreateContext(),
            new ClusterTaskCompletion("a", server, Now.AddSeconds(2), DidWork: false, Rerun: false, Record: false, null, null, 1),
            CancellationToken.None);

        // Assert
        var row = await ReadAsync("a");
        row.NextRunAt.ShouldBe(Now.AddSeconds(2));
        row.RunRequestedAt.ShouldBeNull();
    }

    [TimedFact]
    public async Task ClaimClusterTasks_ServerFromBeforeTheLeaseAlive_ReportsIt()
    {
        // A live server without a coordination version still takes each task's old lock; newer servers
        // must take it too until it is gone (rule 6.20).
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = Now });
        await using (var ctx = _fixture.CreateContext())
        {
            ctx.Set<Server>().Add(new Server { Id = Guid.NewGuid(), StartedTime = Now, LastHeartbeatTime = Now, CoordinationVersion = null });
            ctx.Set<Server>().Add(new Server { Id = Guid.NewGuid(), StartedTime = Now, LastHeartbeatTime = Now, CoordinationVersion = ServerCoordination.LeaseVersion });
            await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        }

        // Act
        var live = await ClaimAsync(["a"], Guid.NewGuid(), Now, liveSince: Now.AddSeconds(-30));
        var gone = await ClaimAsync(["a"], Guid.NewGuid(), Now, liveSince: Now.AddSeconds(30));

        // Assert
        live.LegacyServersAlive.ShouldBeTrue();
        gone.LegacyServersAlive.ShouldBeFalse();
    }

    private IWarpSqlQueries<TestContext> Queries()
    {
        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        if (!queries.SupportsClusterTaskLeases)
        {
            Assert.Skip("Cluster task leases are not implemented for this provider yet (rule 6.20).");
        }

        return queries;
    }

    private Task<ClusterTaskClaims> ClaimAsync(string[] names, Guid server, DateTime now, DateTime? liveSince = null) =>
        Queries().ClaimClusterTasksAsync(_fixture.CreateContext(), names, server, now, liveSince ?? now.AddMinutes(-1), CancellationToken.None);

    private async Task SeedAsync(ClusterTask task)
    {
        await using var ctx = _fixture.CreateContext();
        ctx.Set<ClusterTask>().Add(task);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<ClusterTask> ReadAsync(string name)
    {
        await using var ctx = _fixture.CreateContext();

        return await ctx.Set<ClusterTask>().AsNoTracking().SingleAsync(x => x.Name == name);
    }
}
