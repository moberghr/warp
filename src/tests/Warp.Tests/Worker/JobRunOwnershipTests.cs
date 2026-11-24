using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Worker;

// A worker's writes to a job it ran (the batched completion and the keep-alive renewal) touch the row only
// while it is still that run's. StaleJobRecovery can take a job back while its handler still runs (a long
// pause, a lost keep-alive), and another claim can then take it: the old run finishing afterwards wrote
// Completed over the new run's row, and its renewals kept the new run's row alive under the old worker.
[GenerateDatabaseTests]
public abstract class JobRunOwnershipTestsBase : IAsyncLifetime
{
    private static readonly DateTime ClaimedAt = new(2026, 4, 17, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ClaimedAgainAt = ClaimedAt.AddMinutes(10);

    private readonly IDatabaseFixture _fixture;

    protected JobRunOwnershipTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteJobsAsync_JobTakenBackAndClaimedAgainWhileTheRunHeldIt_LeavesTheNewClaimsRow(bool sameGroup)
    {
        // Arrange — the same group claiming it again stamps the same id, so only the keep-alive tells the
        // two claims apart.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var run = await ClaimAsync();
        var newClaimer = sameGroup ? run.ClaimedBy : Guid.NewGuid();
        await StampAsync(run.JobId, newClaimer, ClaimedAgainAt);

        // Act
        IReadOnlyCollection<Guid> written;
        await using (var context = _fixture.CreateContext())
        {
            written = await TestTasks.QueriesFor(context).CompleteJobsAsync(context, [Finished(run.JobId)], [run], [Log(run.JobId)], ct);
        }

        // Assert
        written.ShouldBeEmpty();
        var row = await ReadAsync(run.JobId);
        row.CurrentState.ShouldBe(State.Processing);
        row.CurrentWorkerId.ShouldBe(newClaimer);
        row.LastKeepAlive.ShouldBe(ClaimedAgainAt);
        (await LogCountAsync(run.JobId)).ShouldBe(0);
    }

    [TimedFact]
    public async Task CompleteJobsAsync_RowRenewedByTheRunsWorker_WritesIt()
    {
        // Arrange — after the first renewal the row carries the worker and a later keep-alive.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var run = await ClaimAsync();
        await StampAsync(run.JobId, run.WorkerId, ClaimedAgainAt);

        // Act
        IReadOnlyCollection<Guid> written;
        await using (var context = _fixture.CreateContext())
        {
            written = await TestTasks.QueriesFor(context).CompleteJobsAsync(context, [Finished(run.JobId)], [run], [Log(run.JobId)], ct);
        }

        // Assert
        written.ShouldBe([run.JobId]);
        (await ReadAsync(run.JobId)).CurrentState.ShouldBe(State.Completed);
        (await LogCountAsync(run.JobId)).ShouldBe(1);
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenewRunningJobsAsync_JobTakenBackAndClaimedAgainWhileTheRunHeldIt_LeavesTheNewClaimsRow(bool sameGroup)
    {
        // Arrange
        var ct = Xunit.TestContext.Current.CancellationToken;
        var run = await ClaimAsync();
        var newClaimer = sameGroup ? run.ClaimedBy : Guid.NewGuid();
        await StampAsync(run.JobId, newClaimer, ClaimedAgainAt);

        // Act
        await using (var context = _fixture.CreateContext())
        {
            await TestTasks.QueriesFor(context).RenewRunningJobsAsync(context, [run], ClaimedAgainAt.AddMinutes(1), ct);
        }

        // Assert
        var row = await ReadAsync(run.JobId);
        row.CurrentWorkerId.ShouldBe(newClaimer);
        row.LastKeepAlive.ShouldBe(ClaimedAgainAt);
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenewRunningJobsAsync_RowStillTheRuns_RenewsItAndStampsTheWorker(bool renewedBefore)
    {
        // Arrange — the first renewal finds the row as the claim left it, later ones as the last renewal did.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var run = await ClaimAsync();
        if (renewedBefore)
        {
            await StampAsync(run.JobId, run.WorkerId, ClaimedAgainAt);
        }

        var now = ClaimedAgainAt.AddMinutes(1);

        // Act
        await using (var context = _fixture.CreateContext())
        {
            await TestTasks.QueriesFor(context).RenewRunningJobsAsync(context, [run], now, ct);
        }

        // Assert
        var row = await ReadAsync(run.JobId);
        row.CurrentWorkerId.ShouldBe(run.WorkerId);
        row.LastKeepAlive.ShouldBe(now);
    }

    [TimedFact]
    public async Task CompleteJobsAsync_ClaimedAgainByTheSameWorkerAndRenewed_LeavesTheNewClaimsRow()
    {
        // Arrange — #347: the new claim reached the same worker, which renewed it, so the worker id and the
        // keep-alive match the old run's renewed stamps. Only the run id tells the two runs apart.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var run = await ClaimAsync();
        await StampAsync(run.JobId, run.WorkerId, ClaimedAgainAt);
        var newRun = await NewRunAsync(run.JobId);

        // Act
        IReadOnlyCollection<Guid> written;
        await using (var context = _fixture.CreateContext())
        {
            written = await TestTasks.QueriesFor(context).CompleteJobsAsync(context, [Finished(run.JobId)], [run], [Log(run.JobId)], ct);
        }

        // Assert
        written.ShouldBeEmpty();
        var row = await ReadAsync(run.JobId);
        row.CurrentState.ShouldBe(State.Processing);
        row.RunId.ShouldBe(newRun);
        (await LogCountAsync(run.JobId)).ShouldBe(0);
    }

    [TimedFact]
    public async Task RenewRunningJobsAsync_ClaimedAgainByTheSameWorkerAndRenewed_LeavesTheNewClaimsRow()
    {
        // Arrange
        var ct = Xunit.TestContext.Current.CancellationToken;
        var run = await ClaimAsync();
        await StampAsync(run.JobId, run.WorkerId, ClaimedAgainAt);
        await NewRunAsync(run.JobId);

        // Act
        IReadOnlyCollection<Guid> cancelled;
        await using (var context = _fixture.CreateContext())
        {
            cancelled = await TestTasks.QueriesFor(context).RenewRunningJobsAsync(context, [run], ClaimedAgainAt.AddMinutes(1), ct);
        }

        // Assert
        cancelled.ShouldBeEmpty();
        (await ReadAsync(run.JobId)).LastKeepAlive.ShouldBe(ClaimedAgainAt);
    }

    [TimedFact]
    public async Task ClaimEnqueuedJobsAsync_TwoClaimsOfOneJob_StampADifferentRunIdAndTheCompletionClearsIt()
    {
        // Arrange
        var ct = Xunit.TestContext.Current.CancellationToken;
        var jobId = Guid.NewGuid();
        await using (var insert = _fixture.CreateContext())
        {
            insert.Set<Job>().Add(new Job
            {
                Id = jobId,
                Kind = JobKind.Job,
                CurrentState = State.Enqueued,
                Type = "test",
                Message = "{}",
                CreateTime = ClaimedAt,
                ScheduleTime = ClaimedAt,
                Queue = "default",
            });
            await insert.SaveChangesAsync(ct);
        }

        var worker = Guid.NewGuid();

        // Act — claimed, put back as StaleJobRecovery would, claimed again, then completed.
        Job first;
        await using (var context = _fixture.CreateContext())
        {
            first = (await TestTasks.QueriesFor(context).ClaimEnqueuedJobsAsync(context, ["default"], worker, ClaimedAt, 1, null, ct)).Single();
        }

        await using (var context = _fixture.CreateContext())
        {
            await context.Set<Job>()
                .Where(x => x.Id == jobId)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentState, State.Enqueued).SetProperty(p => p.RunId, (Guid?)null), ct);
        }

        Job second;
        await using (var context = _fixture.CreateContext())
        {
            second = (await TestTasks.QueriesFor(context).ClaimEnqueuedJobsAsync(context, ["default"], worker, ClaimedAt, 1, null, ct)).Single();
        }

        IReadOnlyCollection<Guid> written;
        await using (var context = _fixture.CreateContext())
        {
            written = await TestTasks.QueriesFor(context).CompleteJobsAsync(context, [Finished(jobId)], [JobRunOwner.Of(second, Guid.NewGuid())], [Log(jobId)], ct);
        }

        // Assert
        first.RunId.ShouldNotBeNull();
        second.RunId.ShouldNotBeNull();
        second.RunId.ShouldNotBe(first.RunId);
        written.ShouldBe([jobId]);
        (await ReadAsync(jobId)).RunId.ShouldBeNull();
    }

    // A job as a claim leaves it, and the run a worker records from the row the claim returned.
    private async Task<JobRunOwner> ClaimAsync()
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            Type = "test",
            Message = "{}",
            CreateTime = ClaimedAt,
            ScheduleTime = ClaimedAt,
            Queue = "default",
            CurrentWorkerId = Guid.NewGuid(),
            LastKeepAlive = ClaimedAt,
            RunId = Guid.NewGuid(),
        };
        await using (var insert = _fixture.CreateContext())
        {
            insert.Set<Job>().Add(job);
            await insert.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        }

        return JobRunOwner.Of(await ReadAsync(job.Id), Guid.NewGuid());
    }

    private async Task StampAsync(Guid jobId, Guid? workerId, DateTime keepAlive)
    {
        await using var context = _fixture.CreateContext();
        await context.Set<Job>()
            .Where(x => x.Id == jobId)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(p => p.CurrentWorkerId, workerId)
                    .SetProperty(p => p.LastKeepAlive, keepAlive),
                Xunit.TestContext.Current.CancellationToken);
    }

    // Another claim of the same row: a fresh run id, the worker stamps left as they are.
    private async Task<Guid> NewRunAsync(Guid jobId)
    {
        var runId = Guid.NewGuid();
        await using var context = _fixture.CreateContext();
        await context.Set<Job>()
            .Where(x => x.Id == jobId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.RunId, runId), Xunit.TestContext.Current.CancellationToken);

        return runId;
    }

    private async Task<Job> ReadAsync(Guid jobId)
    {
        await using var context = _fixture.CreateContext();

        return await context.Set<Job>()
            .AsNoTracking()
            .Where(x => x.Id == jobId)
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<int> LogCountAsync(Guid jobId)
    {
        await using var context = _fixture.CreateContext();

        return await context.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private static Job Finished(Guid jobId) => new()
    {
        Id = jobId,
        CurrentState = State.Completed,
        ScheduleTime = ClaimedAt,
        ExpireAt = ClaimedAgainAt.AddDays(1),
    };

    private static JobLog Log(Guid jobId) => new()
    {
        JobId = jobId,
        EventType = "Completed",
        Timestamp = ClaimedAgainAt,
        Level = "Information",
        Message = $"Job {jobId} completed",
    };
}
