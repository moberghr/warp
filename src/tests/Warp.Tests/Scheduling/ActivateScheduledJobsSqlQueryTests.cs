using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Scheduling;

// Pins the contract of IWarpSqlQueries.ActivateScheduledJobsAsync — the atomic
// UPDATE-RETURNING (PG) / UPDATE-OUTPUT (MSSQL) that folds the previous
// SELECT-DISTINCT-then-UPDATE pair into one round-trip.
// Covers what the integration tests don't reach: per-row state filters, distinct-queue
// return shape, future-dated rows untouched, idempotency.
[GenerateDatabaseTests]
public abstract class ActivateScheduledJobsSqlQueryTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ActivateScheduledJobsSqlQueryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task ActivateScheduledJobsAsync_DueRow_FlippedToEnqueuedAndIdQueueScheduleTimeReturned()
    {
        var now = DateTime.UtcNow;
        var scheduledAt = now.AddMinutes(-1);
        var jobId = await SeedScheduledJobAsync("q1", scheduleTime: scheduledAt);

        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        await using var ctx = _fixture.CreateContext();
        var returned = await queries.ActivateScheduledJobsAsync(ctx, now, default);

        returned.ShouldHaveSingleItem();
        returned[0].Id.ShouldBe(jobId);
        returned[0].Queue.ShouldBe("q1");

        // The UPDATE doesn't touch ScheduleTime — the value carried back is the row's existing
        // (pre-activation) schedule time so the per-row JobLog can record "was scheduled at X".
        // SQL Server's datetime2 stores microsecond precision; allow 1 ms tolerance for any
        // round-trip rounding across providers.
        returned[0].ScheduleTime.ShouldBe(scheduledAt, TimeSpan.FromMilliseconds(1));

        await using var readCtx = _fixture.CreateContext();
        var states = await readCtx.Set<Job>().AsNoTracking().Select(j => j.CurrentState).ToListAsync();
        states.ShouldAllBe(s => s == State.Enqueued);
    }

    [TimedFact]
    public async Task ActivateScheduledJobsAsync_FutureRow_NotTouched()
    {
        var now = DateTime.UtcNow;
        var jobId = await SeedScheduledJobAsync("q1", scheduleTime: now.AddMinutes(10));

        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        await using var ctx = _fixture.CreateContext();
        var returned = await queries.ActivateScheduledJobsAsync(ctx, now, default);

        returned.ShouldBeEmpty();

        await using var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().AsNoTracking().FirstAsync(j => j.Id == jobId);
        job.CurrentState.ShouldBe(State.Scheduled);
    }

    [TimedFact]
    public async Task ActivateScheduledJobsAsync_NoDueRows_ReturnsEmpty()
    {
        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        await using var ctx = _fixture.CreateContext();
        var returned = await queries.ActivateScheduledJobsAsync(ctx, DateTime.UtcNow, default);

        returned.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task ActivateScheduledJobsAsync_MultipleQueues_ReturnsOnePerActivatedRow()
    {
        var now = DateTime.UtcNow;
        await SeedScheduledJobAsync("q1", scheduleTime: now.AddSeconds(-30));
        await SeedScheduledJobAsync("q2", scheduleTime: now.AddSeconds(-30));
        await SeedScheduledJobAsync("q1", scheduleTime: now.AddSeconds(-30));   // second row on q1

        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        await using var ctx = _fixture.CreateContext();
        var returned = await queries.ActivateScheduledJobsAsync(ctx, now, default);

        // The contract returns one entry per ACTIVATED row (not distinct queues) — the
        // caller deduplicates for notification fan-out. Verifying the raw shape here.
        returned.Count.ShouldBe(3);
        returned.Count(r => string.Equals(r.Queue, "q1", StringComparison.Ordinal)).ShouldBe(2);
        returned.Count(r => string.Equals(r.Queue, "q2", StringComparison.Ordinal)).ShouldBe(1);
    }

    [TimedFact]
    public async Task ActivateScheduledJobsAsync_AlreadyEnqueuedRow_NotTouchedNorReturned()
    {
        var now = DateTime.UtcNow;
        var jobId = Guid.NewGuid();
        await using var seedCtx = _fixture.CreateContext();
        seedCtx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Queue = "q1",
            Type = "TestType",
            Message = "{}",
            CreateTime = now,
            ScheduleTime = now.AddMinutes(-1),
        });
        await seedCtx.SaveChangesAsync();

        var queries = TestTasks.QueriesFor(_fixture.CreateContext());
        await using var actCtx = _fixture.CreateContext();
        var returned = await queries.ActivateScheduledJobsAsync(actCtx, now, default);

        returned.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task ActivateScheduledJobsAsync_WithAmbientTransaction_BindsCommandAndCommitsAtomically()
    {
        // Production path: ScheduledJobActivation runs inside ServerTaskLoop's xact-lock
        // wrap, so the raw DbCommand inside ActivateScheduledJobsAsync must bind to the
        // ambient transaction (`cmd.Transaction = ctx.Database.CurrentTransaction.GetDbTransaction()`).
        // On SQL Server the bind is mandatory — running a command on a connection that
        // already has an open tx without binding throws "BeginExecuteReader requires the
        // command to have a transaction." PG is more lenient but the bind is still required
        // for the activation to participate in the outer commit/rollback.
        var now = DateTime.UtcNow;
        var jobId = await SeedScheduledJobAsync("q1", scheduleTime: now.AddMinutes(-1));

        var queries = TestTasks.QueriesFor(_fixture.CreateContext());

        await using var actCtx = _fixture.CreateContext();
        await using var outerTx = await actCtx.Database.BeginTransactionAsync();

        var returned = await queries.ActivateScheduledJobsAsync(actCtx, now, default);
        returned.ShouldHaveSingleItem();
        returned[0].Queue.ShouldBe("q1");

        await outerTx.CommitAsync();

        await using var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().AsNoTracking().FirstAsync(j => j.Id == jobId);
        job.CurrentState.ShouldBe(State.Enqueued);
    }

    [TimedFact]
    public async Task ActivateScheduledJobsAsync_WithAmbientTransactionRolledBack_ChangesDoNotPersist()
    {
        // Symmetric correctness check: rolling back the outer tx must roll back the
        // activation too. Proves the command actually participated in the outer tx and
        // did not silently commit on its own connection.
        var now = DateTime.UtcNow;
        var jobId = await SeedScheduledJobAsync("q1", scheduleTime: now.AddMinutes(-1));

        var queries = TestTasks.QueriesFor(_fixture.CreateContext());

        await using var actCtx = _fixture.CreateContext();
        await using var outerTx = await actCtx.Database.BeginTransactionAsync();

        var returned = await queries.ActivateScheduledJobsAsync(actCtx, now, default);
        returned.ShouldHaveSingleItem();

        await outerTx.RollbackAsync();

        await using var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().AsNoTracking().FirstAsync(j => j.Id == jobId);
        job.CurrentState.ShouldBe(State.Scheduled, "outer rollback must undo the activation");
    }

    [TimedFact]
    public async Task ActivateScheduledJobsAsync_CalledTwice_SecondCallNoOp()
    {
        var now = DateTime.UtcNow;
        await SeedScheduledJobAsync("q1", scheduleTime: now.AddMinutes(-1));

        var queries = TestTasks.QueriesFor(_fixture.CreateContext());

        await using var ctx1 = _fixture.CreateContext();
        var first = await queries.ActivateScheduledJobsAsync(ctx1, now, default);
        first.Count.ShouldBe(1);

        await using var ctx2 = _fixture.CreateContext();
        var second = await queries.ActivateScheduledJobsAsync(ctx2, now, default);
        second.ShouldBeEmpty();
    }

    private async Task<Guid> SeedScheduledJobAsync(string queue, DateTime scheduleTime)
    {
        await using var ctx = _fixture.CreateContext();
        var id = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = id,
            Kind = JobKind.Job,
            CurrentState = State.Scheduled,
            Queue = queue,
            Type = "TestType",
            Message = "{}",
            CreateTime = scheduleTime.AddMinutes(-5),
            ScheduleTime = scheduleTime,
        });
        await ctx.SaveChangesAsync();

        return id;
    }
}
