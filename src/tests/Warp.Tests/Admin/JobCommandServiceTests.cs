using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Admin;

[GenerateDatabaseTests]
public abstract class JobCommandServiceTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected JobCommandServiceTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task DeleteJob_SetsStateToDeleted()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.DeleteJob(jobId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Deleted);
    }

    [TimedFact]
    public async Task DeleteJob_SetsExpireAt()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.DeleteJob(jobId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.ExpireAt.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task DeleteJob_CreatesDeletedLog()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.DeleteJob(jobId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var logs = await readCtx.Set<JobLog>().Where(l => l.JobId == jobId).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        logs.ShouldContain(l => l.EventType == "Deleted");
    }

    [TimedFact]
    public async Task DeleteJob_AlreadyDeleted_NoOp()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Deleted,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ExpireAt = DateTime.UtcNow.AddDays(1),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act & Assert — should not throw
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.DeleteJob(jobId);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Deleted);
    }

    [TimedFact]
    public async Task RequeueJob_SetsStateToEnqueued()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(jobId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Enqueued);
    }

    [TimedFact]
    public async Task RequeueJob_ClearsExpireAt()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ExpireAt = DateTime.UtcNow.AddDays(1),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(jobId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.ExpireAt.ShouldBeNull();
    }

    [TimedFact]
    public async Task RequeueJob_WithMessageParent_ResetsParentToProcessing()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Message,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ExpireAt = DateTime.UtcNow.AddDays(1),
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = childId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentId,
            ExpireAt = DateTime.UtcNow.AddDays(1),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(childId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var parent = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == parentId, Xunit.TestContext.Current.CancellationToken);
        parent.ShouldNotBeNull();
        parent.CurrentState.ShouldBe(State.Processing);
    }

    [TimedFact]
    public async Task RequeueJob_WithBatchParent_ResetsParentToAwaiting()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Batch,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 1,
            ExpireAt = DateTime.UtcNow.AddDays(1),
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = childId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentId,
            ExpireAt = DateTime.UtcNow.AddDays(1),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(childId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var parent = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == parentId, Xunit.TestContext.Current.CancellationToken);
        parent.ShouldNotBeNull();
        parent.CurrentState.ShouldBe(State.Awaiting);
    }

    [TimedFact]
    public async Task RequeueJob_MessageSpawnedJob_KeepsHandlerType()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        const string handlerType = "Some.Handler.Type, SomeAssembly";

        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Message,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = childId,
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentId,
            HandlerType = handlerType,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(childId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == childId, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.HandlerType.ShouldBe(handlerType);
    }

    [TimedFact]
    public async Task RequeueJob_DirectJob_ClearsHandlerType()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            HandlerType = "Some.Handler.Type, SomeAssembly",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(jobId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.HandlerType.ShouldBeNull();
    }

    [TimedFact]
    public async Task BulkDeleteJobs_DeletesMultiple()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = id,
                Kind = JobKind.Job,
                CurrentState = State.Completed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.BulkDeleteJobs(ids);

        // Assert
        result.Succeeded.ShouldBe(3);
        var readCtx = _fixture.CreateContext();
        var jobs = await readCtx.Set<Job>().Where(j => ids.Contains(j.Id)).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        jobs.ShouldAllBe(j => j.CurrentState == State.Deleted);
    }

    [TimedFact]
    public async Task BulkDeleteJobs_MixedStates_DeletesAllAndUpdatesCounters()
    {
        var ctx = _fixture.CreateContext();
        var completedId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        var enqueuedId = Guid.NewGuid();
        var scheduledId = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job { Id = completedId, Kind = JobKind.Job, CurrentState = State.Completed, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow, Queue = "default" });
        ctx.Set<Job>().Add(new Job { Id = failedId, Kind = JobKind.Job, CurrentState = State.Failed, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow, Queue = "default" });
        ctx.Set<Job>().Add(new Job { Id = enqueuedId, Kind = JobKind.Job, CurrentState = State.Enqueued, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow, Queue = "default" });
        ctx.Set<Job>().Add(new Job { Id = scheduledId, Kind = JobKind.Job, CurrentState = State.Scheduled, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow.AddHours(1), Queue = "default" });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.BulkDeleteJobs([completedId, failedId, enqueuedId, scheduledId]);

        result.Succeeded.ShouldBe(4);
        result.Skipped.ShouldBe(0);

        var readCtx = _fixture.CreateContext();
        var jobs = await readCtx.Set<Job>().Where(j => new[] { completedId, failedId, enqueuedId, scheduledId }.Contains(j.Id)).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        jobs.ShouldAllBe(j => j.CurrentState == State.Deleted);
        jobs.ShouldAllBe(j => j.ExpireAt != null);

        // Aggregated counters: +4 deleted, -1 succeeded (Completed), -1 failed (Failed).
        // Enqueued and Scheduled have no source-state counter.
        var counters = await readCtx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);
        counters.Where(c => string.Equals(c.Key, "stats:deleted", StringComparison.Ordinal)).Sum(c => c.Value).ShouldBe(4);
        counters.Where(c => string.Equals(c.Key, "stats:succeeded", StringComparison.Ordinal)).Sum(c => c.Value).ShouldBe(-1);
        counters.Where(c => string.Equals(c.Key, "stats:failed", StringComparison.Ordinal)).Sum(c => c.Value).ShouldBe(-1);
    }

    [TimedFact]
    public async Task BulkDeleteJobs_ProcessingJob_SignalsGracefulCancellation()
    {
        var ctx = _fixture.CreateContext();
        var processingId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = processingId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CancellationMode = CancellationMode.None,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.BulkDeleteJobs([processingId]);

        result.Succeeded.ShouldBe(1);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == processingId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Processing);
        job.CancellationMode.ShouldBe(CancellationMode.Graceful);

        var logs = await readCtx.Set<JobLog>().Where(l => l.JobId == processingId).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        logs.ShouldContain(l => l.EventType == "CancellationRequested");
    }

    [TimedFact]
    public async Task BulkDeleteJobs_AlreadyDeleted_CountedAsSucceeded()
    {
        var ctx = _fixture.CreateContext();
        var deletedId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = deletedId,
            Kind = JobKind.Job,
            CurrentState = State.Deleted,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ExpireAt = DateTime.UtcNow.AddDays(1),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.BulkDeleteJobs([deletedId]);

        result.Succeeded.ShouldBe(1);
        result.Skipped.ShouldBe(0);
    }

    [TimedFact]
    public async Task BulkDeleteJobs_DuplicateIds_CountedAsSucceeded()
    {
        // Matches 1-by-1: DeleteJob(A); DeleteJob(A) returns silently because state is already
        // Deleted, both count as Succeeded. Bulk dedupes the input and credits the duplicate.
        var ctx = _fixture.CreateContext();
        var id = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job { Id = id, Kind = JobKind.Job, CurrentState = State.Failed, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow, Queue = "default" });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.BulkDeleteJobs([id, id, id]);

        result.Succeeded.ShouldBe(3);
        result.Skipped.ShouldBe(0);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Deleted);

        // Counter must reflect a single delete, not three.
        var counters = await readCtx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);
        counters.Where(c => string.Equals(c.Key, "stats:deleted", StringComparison.Ordinal)).Sum(c => c.Value).ShouldBe(1);
        counters.Where(c => string.Equals(c.Key, "stats:failed", StringComparison.Ordinal)).Sum(c => c.Value).ShouldBe(-1);
    }

    [TimedFact]
    public async Task BulkDeleteJobs_PhantomIds_CountedAsSkipped()
    {
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.BulkDeleteJobs([Guid.NewGuid(), Guid.NewGuid()]);

        result.Succeeded.ShouldBe(0);
        result.Skipped.ShouldBe(2);
    }

    [TimedFact]
    public async Task BulkDeleteJobs_LargeBatch_DeletesAll()
    {
        var ctx = _fixture.CreateContext();
        var ids = Enumerable.Range(0, 1200).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            ctx.Set<Job>().Add(new Job { Id = id, Kind = JobKind.Job, CurrentState = State.Failed, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow, Queue = "default" });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.BulkDeleteJobs(ids);

        result.Succeeded.ShouldBe(1200);

        var readCtx = _fixture.CreateContext();
        var remainingNonDeleted = await readCtx.Set<Job>()
            .Where(j => ids.Contains(j.Id))
            .Where(j => j.CurrentState != State.Deleted)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        remainingNonDeleted.ShouldBe(0);
    }

    [TimedFact]
    public async Task BulkDeleteJobs_ConcurrentRequeue_NeverCorruptsState()
    {
        // Concurrent Bulk Delete + Single Requeue on the same Failed job: each transition is
        // atomic (DB row lock + conditional UPDATE / SELECT FOR UPDATE), so the final state
        // is always one of {Deleted, Enqueued} — never a torn write.
        var ctx = _fixture.CreateContext();
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            ctx.Set<Job>().Add(new Job { Id = id, Kind = JobKind.Job, CurrentState = State.Failed, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow, Queue = "default" });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var deleteSvc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());

        var deleteTask = deleteSvc.BulkDeleteJobs(ids);
        var requeueTasks = ids.Select(id => Task.Run(async () =>
        {
            // Fresh service per task — DbContext is not thread-safe.
            var requeueSvc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
            try
            {
                await requeueSvc.RequeueJob(id);
            }
            catch
            {
                // Requeue may legitimately fail if the row was just deleted between read and lock.
            }
        }));

        await Task.WhenAll([deleteTask, .. requeueTasks]);

        var readCtx = _fixture.CreateContext();
        var jobs = await readCtx.Set<Job>().Where(j => ids.Contains(j.Id)).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        jobs.Count.ShouldBe(50);
        jobs.ShouldAllBe(j => j.CurrentState == State.Deleted || j.CurrentState == State.Enqueued);
    }

    [TimedFact]
    public async Task BulkRequeueJobs_RequeuesMultiple()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = id,
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.BulkRequeueJobs(ids);

        // Assert
        result.Succeeded.ShouldBe(3);
        var readCtx = _fixture.CreateContext();
        var jobs = await readCtx.Set<Job>().Where(j => ids.Contains(j.Id)).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        jobs.ShouldAllBe(j => j.CurrentState == State.Enqueued);
    }

    [TimedFact]
    public async Task BulkRequeueJobs_MessageParent_KeepsHandlerTypeAndBumpsParentOnce()
    {
        // Many children of a single Message parent — the optimized path locks the parent once
        // and bumps JobCount by N (not N parent-locks).
        var ctx = _fixture.CreateContext();
        var parentId = Guid.NewGuid();
        var childIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        const string handlerType = "Some.Handler.Type, SomeAssembly";

        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Message,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 0,
            ExpireAt = DateTime.UtcNow.AddDays(1),
        });
        foreach (var id in childIds)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = id,
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = parentId,
                HandlerType = handlerType,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.BulkRequeueJobs(childIds);

        result.Succeeded.ShouldBe(5);

        var readCtx = _fixture.CreateContext();
        var children = await readCtx.Set<Job>().Where(j => childIds.Contains(j.Id)).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        children.ShouldAllBe(j => j.CurrentState == State.Enqueued);
        children.ShouldAllBe(j => j.HandlerType == handlerType);

        var parent = await readCtx.Set<Job>().FirstAsync(j => j.Id == parentId, Xunit.TestContext.Current.CancellationToken);
        parent.JobCount.ShouldBe(5);
        parent.CurrentState.ShouldBe(State.Processing);
        parent.ExpireAt.ShouldBeNull();
    }

    [TimedFact]
    public async Task BulkRequeueJobs_BatchParent_FlipsParentToAwaiting()
    {
        var ctx = _fixture.CreateContext();
        var parentId = Guid.NewGuid();
        var childIds = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();

        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Batch,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 0,
        });
        foreach (var id in childIds)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = id,
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = parentId,
                HandlerType = "ignored.for.batch",
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.BulkRequeueJobs(childIds);

        var readCtx = _fixture.CreateContext();
        var parent = await readCtx.Set<Job>().FirstAsync(j => j.Id == parentId, Xunit.TestContext.Current.CancellationToken);
        parent.JobCount.ShouldBe(3);
        parent.CurrentState.ShouldBe(State.Awaiting);

        // Batch parent → children's HandlerType cleared (parent.Kind != Message)
        var children = await readCtx.Set<Job>().Where(j => childIds.Contains(j.Id)).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        children.ShouldAllBe(j => j.HandlerType == null);
    }

    [TimedFact]
    public async Task BulkRequeueJobs_DirectJob_ClearsHandlerTypeAndUpdatesCounters()
    {
        var ctx = _fixture.CreateContext();
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = ids[i],
                Kind = JobKind.Job,
                CurrentState = i < 2 ? State.Completed : State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                HandlerType = "Some.Handler",
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.BulkRequeueJobs(ids);

        result.Succeeded.ShouldBe(4);

        var readCtx = _fixture.CreateContext();
        var jobs = await readCtx.Set<Job>().Where(j => ids.Contains(j.Id)).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        jobs.ShouldAllBe(j => j.CurrentState == State.Enqueued);
        jobs.ShouldAllBe(j => j.HandlerType == null);
        jobs.ShouldAllBe(j => j.ExpireAt == null);

        var counters = await readCtx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);
        counters.Where(c => string.Equals(c.Key, "stats:succeeded", StringComparison.Ordinal)).Sum(c => c.Value).ShouldBe(-2);
        counters.Where(c => string.Equals(c.Key, "stats:failed", StringComparison.Ordinal)).Sum(c => c.Value).ShouldBe(-2);
    }

    [TimedFact]
    public async Task BulkRequeueJobs_DuplicateIds_CountedAsSucceeded()
    {
        var ctx = _fixture.CreateContext();
        var id = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job { Id = id, Kind = JobKind.Job, CurrentState = State.Failed, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow, Queue = "default" });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.BulkRequeueJobs([id, id, id]);

        result.Succeeded.ShouldBe(3);
        result.Skipped.ShouldBe(0);

        var readCtx = _fixture.CreateContext();
        var counters = await readCtx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);

        // Counter reflects a single Failed→Enqueued transition, not three.
        counters.Where(c => string.Equals(c.Key, "stats:failed", StringComparison.Ordinal)).Sum(c => c.Value).ShouldBe(-1);
    }

    [TimedFact]
    public async Task BulkRequeueJobs_AlreadyEnqueued_CountedAsSucceeded()
    {
        var ctx = _fixture.CreateContext();
        var id = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = id,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.BulkRequeueJobs([id]);

        result.Succeeded.ShouldBe(1);
        result.Skipped.ShouldBe(0);
    }

    [TimedFact]
    public async Task BulkRequeueJobs_LargeBatch_RequeuesAll()
    {
        var ctx = _fixture.CreateContext();
        var ids = Enumerable.Range(0, 1200).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            ctx.Set<Job>().Add(new Job { Id = id, Kind = JobKind.Job, CurrentState = State.Failed, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow, Queue = "default" });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.BulkRequeueJobs(ids);

        result.Succeeded.ShouldBe(1200);

        var readCtx = _fixture.CreateContext();
        var stillFailed = await readCtx.Set<Job>()
            .Where(j => ids.Contains(j.Id))
            .Where(j => j.CurrentState != State.Enqueued)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        stillFailed.ShouldBe(0);
    }

    [TimedFact]
    public async Task BulkRequeueJobs_TwoConcurrentBulks_DoNotDeadlockAndConvergeJobCount()
    {
        // Two BulkRequeueJobs callers race for the same parent's children. With sorted parent
        // locks in Phase 2 plus sorted ids in Phase 1 UPDATEs, neither caller can hold parent
        // locks in opposing orders — no cross-caller deadlock cycle is possible. Final
        // parent.JobCount must equal the number of children actually transitioned Failed→Enqueued
        // (each row transitions at most once across the two callers via the conditional WHERE).
        var ctx = _fixture.CreateContext();
        var parentA = Guid.NewGuid();
        var parentB = Guid.NewGuid();
        var childIdsA = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        var childIdsB = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();

        ctx.Set<Job>().Add(new Job { Id = parentA, Kind = JobKind.Batch, CurrentState = State.Completed, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow, Queue = "default", JobCount = 0 });
        ctx.Set<Job>().Add(new Job { Id = parentB, Kind = JobKind.Batch, CurrentState = State.Completed, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow, Queue = "default", JobCount = 0 });
        foreach (var id in childIdsA)
        {
            ctx.Set<Job>().Add(new Job { Id = id, Kind = JobKind.Job, CurrentState = State.Failed, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow, Queue = "default", ParentJobId = parentA });
        }

        foreach (var id in childIdsB)
        {
            ctx.Set<Job>().Add(new Job { Id = id, Kind = JobKind.Job, CurrentState = State.Failed, CreateTime = DateTime.UtcNow, ScheduleTime = DateTime.UtcNow, Queue = "default", ParentJobId = parentB });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Caller 1 sees parents in {A, B} order; Caller 2 sees them in {B, A} order. Without
        // sorting at Phase 2, this would be the canonical opposing-lock-order deadlock setup.
        var caller1Ids = childIdsA.Concat(childIdsB).ToArray();
        var caller2Ids = childIdsB.Concat(childIdsA).ToArray();

        var svc1 = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var svc2 = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());

        var task1 = Task.Run(() => svc1.BulkRequeueJobs(caller1Ids));
        var task2 = Task.Run(() => svc2.BulkRequeueJobs(caller2Ids));

        await Task.WhenAll(task1, task2);

        var readCtx = _fixture.CreateContext();
        var pA = await readCtx.Set<Job>().FirstAsync(j => j.Id == parentA, Xunit.TestContext.Current.CancellationToken);
        var pB = await readCtx.Set<Job>().FirstAsync(j => j.Id == parentB, Xunit.TestContext.Current.CancellationToken);

        // Each Failed→Enqueued transition bumps exactly one parent. Both callers race on the
        // same children but the conditional UPDATE ensures only one wins per row.
        pA.JobCount.ShouldBe(childIdsA.Length);
        pB.JobCount.ShouldBe(childIdsB.Length);

        var allChildren = await readCtx.Set<Job>().Where(j => caller1Ids.Contains(j.Id)).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        allChildren.ShouldAllBe(j => j.CurrentState == State.Enqueued);
    }

    [TimedFact]
    public async Task BulkRequeueJobs_ConcurrentDelete_NeverCorruptsParentJobCount()
    {
        // Concurrent BulkRequeue + single Delete on the same Failed children: the conditional
        // UPDATE + parent lock combination guarantees parent.JobCount equals exactly the count
        // of children that ended up Enqueued, never over-bumped by raced-out children.
        var ctx = _fixture.CreateContext();
        var parentId = Guid.NewGuid();
        var childIds = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToArray();

        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Batch,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 0,
        });
        foreach (var id in childIds)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = id,
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = parentId,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var requeueSvc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());

        var requeueTask = requeueSvc.BulkRequeueJobs(childIds);
        var deleteTasks = childIds.Select(id => Task.Run(async () =>
        {
            // Fresh service per task — DbContext is not thread-safe.
            var deleteSvc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
            try
            {
                await deleteSvc.DeleteJob(id);
            }
            catch
            {
                // Delete may fail if the row state moved during the race.
            }
        }));

        await Task.WhenAll([requeueTask, .. deleteTasks]);

        var readCtx = _fixture.CreateContext();
        var children = await readCtx.Set<Job>().Where(j => childIds.Contains(j.Id)).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        children.ShouldAllBe(j => j.CurrentState == State.Deleted || j.CurrentState == State.Enqueued);

        // Invariant: parent.JobCount bumps exactly once per successful Failed→Enqueued transition.
        // - Lower bound: any child currently Enqueued was requeued and survived (no later delete).
        //   So JobCount >= count(Enqueued).
        // - Upper bound: at most snapshot.Count children could have been requeued; some of those
        //   may have been deleted-after-requeue, which doesn't decrement parent.JobCount (a
        //   pre-existing semantic shared with single DeleteJob). So JobCount <= snapshot.Count.
        // The point of the test: bulk requeue never over-bumps from raced-out children whose
        // conditional UPDATE didn't actually fire.
        var actuallyEnqueued = children.Count(j => j.CurrentState == State.Enqueued);
        var parent = await readCtx.Set<Job>().FirstAsync(j => j.Id == parentId, Xunit.TestContext.Current.CancellationToken);
        parent.JobCount.ShouldBeGreaterThanOrEqualTo(actuallyEnqueued);
        parent.JobCount.ShouldBeLessThanOrEqualTo(childIds.Length);
    }
}
