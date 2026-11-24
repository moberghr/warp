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
/// the lease that keeps a second server out, run requests, renewal and completion. Every time in them
/// is the database's own now(), so the tests compare times the statements wrote with each other, and
/// seed "due" and "free" with times far enough away that no clock difference can matter.
/// </summary>
[GenerateDatabaseTests]
public abstract class ClusterTaskQueriesTestsBase : IAsyncLifetime
{
    private static readonly DateTime LongAgo = DateTime.UnixEpoch;

    private static readonly DateTime FarAhead = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan LiveWindow = TimeSpan.FromSeconds(30);

    private readonly IDatabaseFixture _fixture;

    protected ClusterTaskQueriesTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task ClaimClusterTasks_DueTask_ClaimsItForOneIntervalAndMovesItsNextRun()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        var server = Guid.NewGuid();

        // Act
        var claims = await ClaimAsync(["a"], server);

        // Assert
        var state = claims.Tasks.ShouldHaveSingleItem();
        state.Claimed.ShouldBeTrue();
        state.DueIn.ShouldBe(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        var row = await ReadAsync("a");
        row.ClaimedBy.ShouldBe(server);
        (row.LeaseUntil - row.ClaimedAt).ShouldBe(TimeSpan.FromSeconds(10));
        (row.NextRunAt - row.ClaimedAt!.Value).ShouldBe(TimeSpan.FromSeconds(10));
    }

    [TimedFact]
    public async Task ClaimClusterTasks_LeaseHeldByAnotherServer_DoesNotClaimAndReportsWhenItIsFree()
    {
        // The lease is what keeps the task on one server: a second server asking while the first holds
        // it gets nothing, and learns when to look again.
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        var holder = Guid.NewGuid();
        await ClaimAsync(["a"], holder);

        // Act
        var second = await ClaimAsync(["a"], Guid.NewGuid());

        // Assert
        var state = second.Tasks.ShouldHaveSingleItem();
        state.Claimed.ShouldBeFalse();
        state.DueIn.ShouldBe(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        (await ReadAsync("a")).ClaimedBy.ShouldBe(holder);
    }

    [TimedFact]
    public async Task ClaimClusterTasks_NotDue_DoesNotClaimAndReportsWhenItIsDue()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = FarAhead });

        // Act
        var claims = await ClaimAsync(["a"], Guid.NewGuid());

        // Assert
        var state = claims.Tasks.ShouldHaveSingleItem();
        state.Claimed.ShouldBeFalse();
        state.DueIn.ShouldBeGreaterThan(TimeSpan.FromDays(365));
    }

    [TimedFact]
    public async Task RequestClusterTaskRun_LeaseHeldElsewhere_MarksTheRequestAndTheNextClaimRunsIt()
    {
        // A signal on a server that does not hold the lease asks the holder's next turn to run it; the
        // next claim after the lease takes it even though its schedule is not due.
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        await ClaimAsync(["a"], Guid.NewGuid());

        // Act
        var claimedNow = await Queries().RequestClusterTaskRunAsync(_fixture.CreateContext(), "a", Guid.NewGuid(), Ct);

        // Assert
        claimedNow.ShouldBeFalse();
        (await ReadAsync("a")).RunRequestedAt.ShouldNotBeNull();
        await ExpireLeaseAsync("a");
        var afterLease = await ClaimAsync(["a"], Guid.NewGuid());
        afterLease.Tasks.ShouldHaveSingleItem().Claimed.ShouldBeTrue();
        (await ReadAsync("a")).RunRequestedAt.ShouldBeNull();
    }

    [TimedFact]
    public async Task RequestClusterTaskRun_LeaseFree_ClaimsItAtOnce()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = FarAhead });
        var server = Guid.NewGuid();

        // Act
        var claimedNow = await Queries().RequestClusterTaskRunAsync(_fixture.CreateContext(), "a", server, Ct);

        // Assert
        claimedNow.ShouldBeTrue();
        var row = await ReadAsync("a");
        row.ClaimedBy.ShouldBe(server);
        (row.NextRunAt - row.ClaimedAt!.Value).ShouldBe(TimeSpan.FromSeconds(10));
    }

    [TimedFact]
    public async Task RenewClusterTaskLease_Holder_ExtendsTheLease()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        var server = Guid.NewGuid();
        await ClaimAsync(["a"], server);
        var before = (await ReadAsync("a")).LeaseUntil!.Value;

        // Act
        var renewed = await Queries().RenewClusterTaskLeaseAsync(_fixture.CreateContext(), "a", server, Ct);

        // Assert
        renewed.ShouldBeTrue();
        (await ReadAsync("a")).LeaseUntil!.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    [TimedFact]
    public async Task RenewClusterTaskLease_AnotherServerHoldsIt_ReturnsFalse()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        await ClaimAsync(["a"], Guid.NewGuid());

        // Act
        var renewed = await Queries().RenewClusterTaskLeaseAsync(_fixture.CreateContext(), "a", Guid.NewGuid(), Ct);

        // Assert
        renewed.ShouldBeFalse();
    }

    [TimedFact]
    public async Task CompleteClusterTask_RunThatDidWork_FreesTheLeaseAndRecordsIt()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        var server = Guid.NewGuid();
        await ClaimAsync(["a"], server);

        // Act
        await Queries().CompleteClusterTaskAsync(
            _fixture.CreateContext(),
            new ClusterTaskCompletion("a", server, DidWork: true, Rerun: false, Record: true, Status: "Completed", Message: "did 3", DurationMs: 12.5),
            Ct);

        // Assert
        var row = await ReadAsync("a");
        row.LeaseUntil.ShouldBeNull();
        row.ClaimedBy.ShouldBeNull();
        (row.NextRunAt - row.LastRunAt!.Value).ShouldBe(TimeSpan.FromSeconds(10));
        row.LastServerId.ShouldBe(server);
        row.LastMessage.ShouldBe("did 3");
        row.LastDurationMs.ShouldBe(12.5);
    }

    [TimedFact]
    public async Task CompleteClusterTask_RunRequestedMeanwhile_IsDueAtOnce()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        var server = Guid.NewGuid();
        await ClaimAsync(["a"], server);
        await Queries().RequestClusterTaskRunAsync(_fixture.CreateContext(), "a", Guid.NewGuid(), Ct);

        // Act
        await Queries().CompleteClusterTaskAsync(
            _fixture.CreateContext(),
            new ClusterTaskCompletion("a", server, DidWork: false, Rerun: false, Record: false, Status: null, Message: null, DurationMs: 1),
            Ct);

        // Assert: due now, so the next claim takes it.
        (await ReadAsync("a")).RunRequestedAt.ShouldBeNull();
        var next = await ClaimAsync(["a"], Guid.NewGuid());
        next.Tasks.ShouldHaveSingleItem().Claimed.ShouldBeTrue();
    }

    [TimedFact]
    public async Task ClaimClusterTasks_ServerFromBeforeTheLeaseAlive_ReportsIt()
    {
        // A live server without a coordination version still takes each task's old lock; newer servers
        // must take it too until it is gone (rule 6.20). Liveness is judged on the database clock.
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        await using (var ctx = _fixture.CreateContext())
        {
            ctx.Set<Server>().Add(new Server { Id = Guid.NewGuid(), StartedTime = DateTime.UtcNow, LastHeartbeatTime = DateTime.UtcNow, CoordinationVersion = null });
            ctx.Set<Server>().Add(new Server { Id = Guid.NewGuid(), StartedTime = LongAgo, LastHeartbeatTime = FarAhead, CoordinationVersion = ServerCoordination.LeaseVersion });
            await ctx.SaveChangesAsync(Ct);
        }

        // Act
        var live = await ClaimAsync(["a"], Guid.NewGuid());
        await using (var ctx = _fixture.CreateContext())
        {
            await ctx.Set<Server>()
                .Where(x => x.CoordinationVersion == null)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.LastHeartbeatTime, LongAgo), Ct);
        }

        var gone = await ClaimAsync(["a"], Guid.NewGuid());

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

    private Task<ClusterTaskClaims> ClaimAsync(string[] names, Guid server) =>
        Queries().ClaimClusterTasksAsync(_fixture.CreateContext(), names, server, LiveWindow, Ct);

    private async Task ExpireLeaseAsync(string name)
    {
        await using var ctx = _fixture.CreateContext();
        await ctx.Set<ClusterTask>()
            .Where(x => x.Name == name)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.LeaseUntil, (DateTime?)LongAgo), Ct);
    }

    private async Task SeedAsync(ClusterTask task)
    {
        await using var ctx = _fixture.CreateContext();
        ctx.Set<ClusterTask>().Add(task);
        await ctx.SaveChangesAsync(Ct);
    }

    private async Task<ClusterTask> ReadAsync(string name)
    {
        await using var ctx = _fixture.CreateContext();

        return await ctx.Set<ClusterTask>().AsNoTracking().SingleAsync(x => x.Name == name);
    }
}
