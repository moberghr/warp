using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Worker;

/// <summary>
/// The cluster-wide task statements (rule 6.23) against a real database: one claim for every due task,
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
    public async Task ClaimClusterTasks_DueTask_ClaimsItUntilItsNextRunOnTheIntervalGrid()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        var server = Guid.NewGuid();

        // Act
        var claims = await ClaimAsync(["a"], server);

        // Assert
        var state = claims.Tasks.ShouldHaveSingleItem();
        state.Claimed.ShouldBeTrue();
        var row = await ReadAsync("a");
        row.ClaimedBy.ShouldBe(server);
        ShouldBeNextGridPoint(row, 10);
        row.LeaseUntil.ShouldBe(row.NextRunAt);
        state.DueIn.ShouldBe(row.NextRunAt - row.ClaimedAt!.Value, TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task ClaimClusterTasks_TasksClaimedAtDifferentTimes_FallDueAtTheSameInstant()
    {
        // Claimed by separate statements, so at different instants: with "now + interval" each would then
        // need its own claim forever; on the grid one claim covers both (and the 20 s one every other time).
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        await SeedAsync(new ClusterTask { Name = "b", IntervalSeconds = 10, NextRunAt = LongAgo });
        await SeedAsync(new ClusterTask { Name = "c", IntervalSeconds = 20, NextRunAt = LongAgo });

        // Act
        await ClaimAsync(["a"], Guid.NewGuid());
        await ClaimAsync(["c"], Guid.NewGuid());
        await ClaimAsync(["b"], Guid.NewGuid());

        // Assert: a and b share a due time unless a tick boundary fell between their claims, in which
        // case they are exactly one interval apart; c is due on a's grid.
        var a = await ReadAsync("a");
        var b = await ReadAsync("b");
        var c = await ReadAsync("c");
        ((b.NextRunAt - a.NextRunAt).TotalSeconds % 10).ShouldBe(0);
        ((c.NextRunAt - a.NextRunAt).TotalSeconds % 10).ShouldBe(0);
        (b.NextRunAt - a.NextRunAt).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
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
        var row = await ReadAsync("a");
        row.ClaimedBy.ShouldBe(holder);
        state.DueIn.ShouldBe(row.LeaseUntil!.Value - row.ClaimedAt!.Value, TimeSpan.FromSeconds(1));
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
    public async Task ClaimClusterTasks_DueBySchedule_ReportsAScheduledClaim()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });

        // Act
        var claims = await ClaimAsync(["a"], Guid.NewGuid());

        // Assert
        var state = claims.Tasks.ShouldHaveSingleItem();
        state.Claimed.ShouldBeTrue();
        state.Scheduled.ShouldBeTrue();
    }

    [TimedFact]
    public async Task ClaimClusterTasks_ClaimedOnlyForARunRequest_ReportsItIsNotScheduled()
    {
        // The Orchestrator sweeps every parent on a scheduled run only; a run a signal asked for (here on
        // another server, honoured by this claim before the schedule is due) checks the named parents.
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = FarAhead, RunRequestedAt = LongAgo });

        // Act
        var claims = await ClaimAsync(["a"], Guid.NewGuid());

        // Assert
        var state = claims.Tasks.ShouldHaveSingleItem();
        state.Claimed.ShouldBeTrue();
        state.Scheduled.ShouldBeFalse();
    }

    [TimedFact]
    public async Task RequestClusterTaskRun_LeaseHeldByTheAskingServer_ClaimsItAtOnce()
    {
        // The scheduler asks only for a task it is not running, so its own lease is one an idle run left
        // without a completion: the request must not wait for that lease to end (rule 6.23).
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 300, NextRunAt = LongAgo });
        var server = Guid.NewGuid();
        await ClaimAsync(["a"], server);

        // Act
        var claimedNow = await Queries().RequestClusterTaskRunAsync(_fixture.CreateContext(), "a", server, Ct);

        // Assert
        claimedNow.ShouldBeTrue();
        var row = await ReadAsync("a");
        row.ClaimedBy.ShouldBe(server);
        row.RunRequestedAt.ShouldBeNull();
    }

    [TimedFact]
    public async Task RequestClusterTaskRun_AfterAnotherServerReleasedItsIdleRun_ClaimsItAtOnce()
    {
        // An idle run of a task that signals run frees its lease when it ends, so a signal on another
        // server does not wait for the lease the claim took (rule 6.23).
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 300, NextRunAt = LongAgo });
        var holder = Guid.NewGuid();
        await ClaimAsync(["a"], holder);
        await Queries().CompleteClusterTaskAsync(
            _fixture.CreateContext(),
            new ClusterTaskCompletion("a", holder, DidWork: false, Rerun: false, Record: false, Status: null, Message: null, DurationMs: 1),
            Ct);
        var other = Guid.NewGuid();

        // Act
        var claimedNow = await Queries().RequestClusterTaskRunAsync(_fixture.CreateContext(), "a", other, Ct);

        // Assert
        claimedNow.ShouldBeTrue();
        (await ReadAsync("a")).ClaimedBy.ShouldBe(other);
    }

    [TimedFact]
    public async Task RequestClusterTaskRun_WhileAnotherServersRunIsInProgress_DoesNotClaim()
    {
        // No completion yet, renewed or not: the run may still be going, so the task stays with its holder.
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 300, NextRunAt = LongAgo });
        var holder = Guid.NewGuid();
        await ClaimAsync(["a"], holder);
        await Queries().RenewClusterTaskLeaseAsync(_fixture.CreateContext(), "a", holder, Ct);

        // Act
        var claimedNow = await Queries().RequestClusterTaskRunAsync(_fixture.CreateContext(), "a", Guid.NewGuid(), Ct);

        // Assert
        claimedNow.ShouldBeFalse();
        (await ReadAsync("a")).ClaimedBy.ShouldBe(holder);
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
        ShouldBeNextGridPoint(row, 10);
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
    public async Task RenewClusterTaskLease_IntervalClearedWhileTheTaskRuns_NeverShortensOrClearsTheLease()
    {
        // A task disabled from the dashboard mid-run has no interval. The renewal must still leave a lease:
        // on SQL Server the NULL interval made the new lease NULL, and a free lease lets a second server
        // claim a task that is still running.
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        var server = Guid.NewGuid();
        await ClaimAsync(["a"], server);
        var before = (await ReadAsync("a")).LeaseUntil!.Value;
        await using (var ctx = _fixture.CreateContext())
        {
            await ctx.Set<ClusterTask>()
                .Where(x => x.Name == "a")
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.IntervalSeconds, (double?)null), Ct);
        }

        // Act
        var renewed = await Queries().RenewClusterTaskLeaseAsync(_fixture.CreateContext(), "a", server, Ct);

        // Assert
        renewed.ShouldBeTrue();
        var lease = (await ReadAsync("a")).LeaseUntil;
        lease.ShouldNotBeNull();
        lease.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    [TimedFact]
    public async Task RenewClusterTaskLease_IntervalLongerThanAMonth_ExtendsTheLease()
    {
        // 30 days in milliseconds does not fit the int SQL Server's DATEADD takes.
        const int ThirtyDays = 30 * 24 * 60 * 60;
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = ThirtyDays, NextRunAt = LongAgo });
        var server = Guid.NewGuid();
        await ClaimAsync(["a"], server);

        // Act
        var renewed = await Queries().RenewClusterTaskLeaseAsync(_fixture.CreateContext(), "a", server, Ct);

        // Assert
        renewed.ShouldBeTrue();
        var row = await ReadAsync("a");
        (row.LeaseUntil!.Value - row.ClaimedAt!.Value).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1));
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
        var sinceRun = row.NextRunAt - row.LastRunAt!.Value;
        sinceRun.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
        sinceRun.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(15));
        (new DateTimeOffset(row.NextRunAt, TimeSpan.Zero).ToUnixTimeMilliseconds() % 10_000).ShouldBe(0);
        row.LastServerId.ShouldBe(server);
        row.LastMessage.ShouldBe("did 3");
        row.LastDurationMs.ShouldBe(12.5);
    }

    [TimedFact]
    public async Task CompleteClusterTask_IdleRunThatOnlyFreesTheLease_LeavesTheNextRunAndReportsNotDue()
    {
        // The release an idle run of a signal task writes: the schedule stays where the claim put it, and
        // the caller is told nothing became due, so it sends no claim after it.
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        var server = Guid.NewGuid();
        await ClaimAsync(["a"], server);
        var claimed = await ReadAsync("a");

        // Act
        var dueNow = await Queries().CompleteClusterTaskAsync(
            _fixture.CreateContext(),
            new ClusterTaskCompletion("a", server, DidWork: false, Rerun: false, Record: false, Status: null, Message: null, DurationMs: 1),
            Ct);

        // Assert
        dueNow.ShouldBeFalse();
        var row = await ReadAsync("a");
        row.LeaseUntil.ShouldBeNull();
        row.ClaimedBy.ShouldBeNull();
        row.NextRunAt.ShouldBe(claimed.NextRunAt);
        row.LastRunAt.ShouldBeNull();
    }

    [TimedFact]
    public async Task CompleteClusterTask_RunRequestedMeanwhile_IsDueAtOnce()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        var server = Guid.NewGuid();
        await ClaimAsync(["a"], server);
        await Queries().RequestClusterTaskRunAsync(_fixture.CreateContext(), "a", Guid.NewGuid(), Ct);

        // Act
        var dueNow = await Queries().CompleteClusterTaskAsync(
            _fixture.CreateContext(),
            new ClusterTaskCompletion("a", server, DidWork: false, Rerun: false, Record: false, Status: null, Message: null, DurationMs: 1),
            Ct);

        // Assert: due now, and the caller is told, so the next claim takes it.
        dueNow.ShouldBeTrue();
        (await ReadAsync("a")).RunRequestedAt.ShouldBeNull();
        var next = await ClaimAsync(["a"], Guid.NewGuid());
        next.Tasks.ShouldHaveSingleItem().Claimed.ShouldBeTrue();
    }

    [TimedFact]
    public async Task ClaimClusterTasks_ServerFromBeforeTheLeaseWithAnOldHeartbeat_IsAliveUntilItsRowIsGone()
    {
        // A server without a coordination version still takes each task's old lock; newer servers must take
        // it too until it is gone (rule 6.23). It writes its heartbeat on its OWN clock, so a database running
        // ahead of the hosts reads a live server's heartbeat as old: the row existing is what counts, never
        // its age.
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        await using (var ctx = _fixture.CreateContext())
        {
            ctx.Set<Server>().Add(new Server { Id = Guid.NewGuid(), StartedTime = LongAgo, LastHeartbeatTime = LongAgo, CoordinationVersion = null });
            ctx.Set<Server>().Add(new Server { Id = Guid.NewGuid(), StartedTime = LongAgo, LastHeartbeatTime = FarAhead, CoordinationVersion = ServerCoordination.LeaseVersion });
            await ctx.SaveChangesAsync(Ct);
        }

        // Act
        var live = await ClaimAsync(["a"], Guid.NewGuid());
        await using (var ctx = _fixture.CreateContext())
        {
            await ctx.Set<Server>()
                .Where(x => x.CoordinationVersion == null)
                .ExecuteDeleteAsync(Ct);
        }

        var gone = await ClaimAsync(["a"], Guid.NewGuid());

        // Assert
        live.LegacyServersAlive.ShouldBeTrue();
        gone.LegacyServersAlive.ShouldBeFalse();
    }

    [TimedFact]
    public async Task ClaimClusterTasks_OtherLiveServer_ReportsItButNotTheClaimingServerItself()
    {
        // A lone server need not release an idle run's lease (rule 6.23): only another live server could claim it.
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        var self = Guid.NewGuid();
        var other = Guid.NewGuid();
        await using (var ctx = _fixture.CreateContext())
        {
            ctx.Set<Server>().Add(new Server { Id = self, StartedTime = DateTime.UtcNow, LastHeartbeatTime = DateTime.UtcNow, CoordinationVersion = ServerCoordination.LeaseVersion });
            ctx.Set<Server>().Add(new Server { Id = other, StartedTime = LongAgo, LastHeartbeatTime = LongAgo, CoordinationVersion = ServerCoordination.LeaseVersion });
            await ctx.SaveChangesAsync(Ct);
        }

        // Act
        var alone = await ClaimAsync(["a"], self);
        await using (var ctx = _fixture.CreateContext())
        {
            await ctx.Set<Server>()
                .Where(x => x.Id == other)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.LastHeartbeatTime, DateTime.UtcNow), Ct);
        }

        var withOther = await ClaimAsync(["a"], self);

        // Assert
        alone.OtherServersAlive.ShouldBeFalse();
        withOther.OtherServersAlive.ShouldBeTrue();
    }

    [TimedFact]
    public async Task RegisterClusterTasks_NewAndExistingTasks_InsertsTheNewOneDueAtOnceAndSetsEveryInterval()
    {
        await SeedAsync(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = FarAhead, LastStatus = "Completed" });

        // Act
        await Queries().RegisterClusterTasksAsync(
            _fixture.CreateContext(),
            [new ClusterTaskRegistration("a", 20), new ClusterTaskRegistration("b", 30)],
            Ct);

        // Assert: the existing row keeps its schedule and history, the new one is due at once.
        var a = await ReadAsync("a");
        a.IntervalSeconds.ShouldBe(20);
        a.NextRunAt.ShouldBe(FarAhead);
        a.LastStatus.ShouldBe("Completed");
        var b = await ReadAsync("b");
        b.IntervalSeconds.ShouldBe(30);
        b.NextRunAt.ShouldBe(LongAgo);
    }

    [TimedFact]
    public async Task RegisterClusterTasks_AnotherServerInsertsTheRowMeanwhile_UpdatesItInsteadOfFailing()
    {
        // Two servers starting together: the other one's insert of the same task is still uncommitted when
        // this registration runs, so this one waits on the key and then finds the row. EF's insert used to
        // fail there with a duplicate key, logged at Error by EF for every lost race.
        await using var other = _fixture.CreateContext();
        await using var transaction = await other.Database.BeginTransactionAsync(Ct);
        other.Set<ClusterTask>().Add(new ClusterTask { Name = "a", IntervalSeconds = 10, NextRunAt = LongAgo });
        await other.SaveChangesAsync(Ct);

        // Act
        var register = Queries().RegisterClusterTasksAsync(_fixture.CreateContext(), [new ClusterTaskRegistration("a", 20)], Ct);
        await WarpTestServer.WaitUntil("the registration to wait on the uncommitted row", async () => register.IsCompleted || await IsWaitingOnALockAsync(), ct: Ct);
        await transaction.CommitAsync(Ct);
        await register;

        // Assert
        var row = await ReadAsync("a");
        row.IntervalSeconds.ShouldBe(20);
    }

    // The next run is a multiple of the interval, between half and one and a half intervals after the
    // claim: tasks that share an interval fall due at the same instant, and one claim covers them.
    private static void ShouldBeNextGridPoint(ClusterTask row, int intervalSeconds)
    {
        (new DateTimeOffset(row.NextRunAt, TimeSpan.Zero).ToUnixTimeMilliseconds() % (intervalSeconds * 1000)).ShouldBe(0);
        var fromClaim = row.NextRunAt - row.ClaimedAt!.Value;
        fromClaim.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(intervalSeconds / 2.0));
        fromClaim.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(intervalSeconds * 1.5));
    }

    // Whether a session of this database is waiting on a row lock: the registration under test, blocked on the
    // other transaction's uncommitted insert.
    private async Task<bool> IsWaitingOnALockAsync()
    {
        await using var ctx = _fixture.CreateContext();
        var sql = ctx.Database.ProviderName?.Contains("SqlServer", StringComparison.Ordinal) == true
            ? "SELECT COUNT(*) AS [Value] FROM sys.dm_exec_requests WHERE blocking_session_id <> 0 AND database_id = DB_ID()"
            : "SELECT COUNT(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()";

        return await ctx.Database.SqlQueryRaw<int>(sql).SingleAsync(Ct) > 0;
    }

    private IWarpSqlQueries<TestContext> Queries() => TestTasks.QueriesFor(_fixture.CreateContext());

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
