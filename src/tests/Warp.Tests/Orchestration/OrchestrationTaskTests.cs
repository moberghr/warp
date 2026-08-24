using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Worker.Services;

namespace Warp.Tests.Orchestration;

[GenerateDatabaseTests]
public abstract class OrchestrationTaskTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected OrchestrationTaskTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task RunOrchestration_BatchAllChildrenCompleted_FinalizesBatch()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var batchId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = batchId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 3,
        });

        for (var i = 0; i < 3; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Completed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = batchId,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var orchCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var batch = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == batchId, Xunit.TestContext.Current.CancellationToken);
        batch.ShouldNotBeNull();
        batch.CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task RunOrchestration_BatchSomeChildrenEnqueued_DoesNotFinalize()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var batchId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = batchId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 2,
        });

        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = batchId,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = batchId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var orchCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var batch = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == batchId, Xunit.TestContext.Current.CancellationToken);
        batch.ShouldNotBeNull();
        batch.CurrentState.ShouldBe(State.Awaiting);
    }

    [TimedFact]
    public async Task RunOrchestration_BatchWithFailedChild_OnlyOnSucceeded_BatchFails()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var batchId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = batchId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 2,
            ContinuationOptions = ContinuationOptions.OnlyOnSucceeded,
        });

        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = batchId,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = batchId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var orchCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var batch = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == batchId, Xunit.TestContext.Current.CancellationToken);
        batch.ShouldNotBeNull();
        batch.CurrentState.ShouldBe(State.Failed);
    }

    [TimedFact]
    public async Task RunOrchestration_BatchOnlyFailedChild_OnlyOnSucceeded_BatchFails()
    {
        // Regression guard for the two-step fetch in FinalizeParentsAsync. The sibling
        // test (BatchWithFailedChild_OnlyOnSucceeded_BatchFails) seeds one Completed AND
        // one Failed child — readyParents is non-empty regardless of whether the second
        // query that populates failedParentIdSet works. This variant seeds ONLY a Failed
        // child, so the parent depends on the failedParentIdSet lookup to be marked
        // Failed. A bug that returns an empty set from the second query would silently
        // promote this parent to Completed instead.
        var ctx = _fixture.CreateContext();
        var batchId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = batchId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 1,
            ContinuationOptions = ContinuationOptions.OnlyOnSucceeded,
        });

        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = batchId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var orchCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var batch = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == batchId, Xunit.TestContext.Current.CancellationToken);
        batch.ShouldNotBeNull();
        batch.CurrentState.ShouldBe(State.Failed);
    }

    [TimedFact]
    public async Task RunOrchestration_BatchWithFailedChild_OnAnyFinished_BatchCompletes()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var batchId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = batchId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 2,
            ContinuationOptions = ContinuationOptions.OnAnyFinishedState,
        });

        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = batchId,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = batchId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var orchCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var batch = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == batchId, Xunit.TestContext.Current.CancellationToken);
        batch.ShouldNotBeNull();
        batch.CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task RunOrchestration_MessageAllChildrenCompleted_FinalizesMessage()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var messageId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = messageId,
            Kind = JobKind.Message,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });

        for (var i = 0; i < 2; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Completed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = messageId,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var orchCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var message = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == messageId, Xunit.TestContext.Current.CancellationToken);
        message.ShouldNotBeNull();
        message.CurrentState.ShouldBe(State.Completed);
        message.ExpireAt.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task RunOrchestration_ActivatesContinuationChildren()
    {
        // Arrange: completed batch parent -> awaiting continuation batch child -> awaiting grandchildren
        var ctx = _fixture.CreateContext();
        var parentBatchId = Guid.NewGuid();
        var continuationBatchId = Guid.NewGuid();

        // Parent batch (completed)
        ctx.Set<Job>().Add(new Job
        {
            Id = parentBatchId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 1,
        });

        // Parent batch child (completed) — triggers parent finalization
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentBatchId,
        });

        // Continuation batch (awaiting, child of parent batch)
        ctx.Set<Job>().Add(new Job
        {
            Id = continuationBatchId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentBatchId,
            JobCount = 2,
        });

        // Grandchildren (awaiting, children of continuation batch)
        var grandchildIds = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var gcId = Guid.NewGuid();
            grandchildIds.Add(gcId);
            ctx.Set<Job>().Add(new Job
            {
                Id = gcId,
                Kind = JobKind.Job,
                CurrentState = State.Awaiting,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = continuationBatchId,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act — run orchestration multiple times to finalize parent and then activate continuation
        var orchCtx1 = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx1, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);
        var orchCtx2 = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx2, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        foreach (var gcId in grandchildIds)
        {
            var gc = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == gcId, Xunit.TestContext.Current.CancellationToken);
            gc.ShouldNotBeNull();
            gc.CurrentState.ShouldBe(State.Enqueued);
        }
    }

    [TimedFact]
    public async Task RunOrchestration_FailedParentOnlyOnSucceeded_ContinuationStaysAwaiting()
    {
        // Arrange: failed batch parent (OnlyOnSucceeded) -> awaiting continuation child
        var ctx = _fixture.CreateContext();
        var parentBatchId = Guid.NewGuid();
        var continuationId = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job
        {
            Id = parentBatchId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 1,
            ContinuationOptions = ContinuationOptions.OnlyOnSucceeded,
        });

        // Failed child triggers parent to fail
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentBatchId,
        });

        // Continuation (awaiting, child of failed batch)
        ctx.Set<Job>().Add(new Job
        {
            Id = continuationId,
            Kind = JobKind.Job,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentBatchId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act — finalize parent, then run again
        var orchCtx1 = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx1, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        var orchCtx2 = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx2, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert: continuation stays Awaiting (condition not met, but parent could be requeued)
        var readCtx = _fixture.CreateContext();
        var continuation = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == continuationId, Xunit.TestContext.Current.CancellationToken);
        continuation.ShouldNotBeNull();
        continuation.CurrentState.ShouldBe(State.Awaiting);
    }

    [TimedFact]
    public async Task RunOrchestration_BatchFinalized_ReturnsTrue()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var batchId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = batchId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 1,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = batchId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var orchCtx = _fixture.CreateContext();
        var workDone = await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        workDone.ShouldBeTrue();
    }

    [TimedFact]
    public async Task RunOrchestration_DeletedParent_FailsAwaitingChildren()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Batch,
            CurrentState = State.Deleted,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = childId,
            Kind = JobKind.Job,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var orchCtx = _fixture.CreateContext();
        var workDone = await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        workDone.ShouldBeTrue();
        var readCtx = _fixture.CreateContext();
        var child = await readCtx.Set<Job>().FindAsync([childId], Xunit.TestContext.Current.CancellationToken);
        child.ShouldNotBeNull();
        child.CurrentState.ShouldBe(State.Failed);
        child.ExpireAt.ShouldNotBeNull();

        var log = await readCtx.Set<JobLog>().FirstOrDefaultAsync(x => x.JobId == childId, Xunit.TestContext.Current.CancellationToken);
        log.ShouldNotBeNull();
        log.EventType.ShouldBe("Failed");
    }

    [TimedFact]
    public async Task RunOrchestration_DeletedParent_FailsAwaitingBatchAndGrandchildren()
    {
        // Arrange: deleted parent -> awaiting batch child -> awaiting grandchildren
        var ctx = _fixture.CreateContext();
        var parentId = Guid.NewGuid();
        var batchChildId = Guid.NewGuid();
        var grandchildId = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Batch,
            CurrentState = State.Deleted,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = batchChildId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentId,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = grandchildId,
            Kind = JobKind.Job,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = batchChildId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var orchCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var batchChild = await readCtx.Set<Job>().FindAsync([batchChildId], Xunit.TestContext.Current.CancellationToken);
        batchChild.ShouldNotBeNull();
        batchChild.CurrentState.ShouldBe(State.Failed);

        var grandchild = await readCtx.Set<Job>().FindAsync([grandchildId], Xunit.TestContext.Current.CancellationToken);
        grandchild.ShouldNotBeNull();
        grandchild.CurrentState.ShouldBe(State.Failed);
    }

    [TimedFact]
    public async Task RunOrchestration_FailedParentOnAnyFinished_ActivatesContinuation()
    {
        // Arrange: failed batch parent (OnAnyFinishedState) -> awaiting continuation
        var ctx = _fixture.CreateContext();
        var parentBatchId = Guid.NewGuid();
        var continuationId = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job
        {
            Id = parentBatchId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 1,
            ContinuationOptions = ContinuationOptions.OnAnyFinishedState,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentBatchId,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = continuationId,
            Kind = JobKind.Job,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentBatchId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act — finalize parent (Failed but OnAnyFinished → Completed), then activate continuation
        var orchCtx1 = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx1, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);
        var orchCtx2 = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx2, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var continuation = await readCtx.Set<Job>().FindAsync([continuationId], Xunit.TestContext.Current.CancellationToken);
        continuation.ShouldNotBeNull();
        continuation.CurrentState.ShouldBe(State.Enqueued);
    }

    [TimedFact]
    public async Task RunOrchestration_NoDeletedParent_AwaitingChildStaysAwaiting()
    {
        // Arrange: non-deleted parent -> awaiting child should not be failed
        var ctx = _fixture.CreateContext();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Batch,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = childId,
            Kind = JobKind.Job,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var orchCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var child = await readCtx.Set<Job>().FindAsync([childId], Xunit.TestContext.Current.CancellationToken);
        child.ShouldNotBeNull();
        child.CurrentState.ShouldBe(State.Awaiting);
    }

    [TimedFact]
    public async Task RunOrchestration_AlreadyFinalized_ReturnsNoWork()
    {
        // Arrange: completed batch + completed children
        var ctx = _fixture.CreateContext();
        var batchId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = batchId,
            Kind = JobKind.Batch,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 2,
            ExpireAt = DateTime.UtcNow.AddDays(1),
        });

        for (var i = 0; i < 2; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Completed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = batchId,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var orchCtx = _fixture.CreateContext();
        var workDone = await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        workDone.ShouldBeFalse();
    }

    /// <summary>
    /// When a parent is Deleted, its Awaiting children should be cleaned up (Deleted).
    /// Otherwise they can never run.
    /// </summary>
    [TimedFact]
    public async Task Orchestration_WhenParentDeleted_AwaitingChildrenAreFailed()
    {
        var ctx = _fixture.CreateContext();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Batch,
            CurrentState = State.Deleted,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 1,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = childId,
            Kind = JobKind.Job,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Run orchestration
        var orchCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var child = await readCtx.Set<Job>().FindAsync([childId], Xunit.TestContext.Current.CancellationToken);
        child.ShouldNotBeNull();
        child.CurrentState.ShouldBe(State.Failed, "Awaiting child of a Deleted parent should be Failed");
    }

    /// <summary>
    /// When a parent fails with OnlyOnSucceeded, Awaiting continuations stay Awaiting.
    /// The parent could be requeued and succeed later.
    /// </summary>
    [TimedFact]
    public async Task Orchestration_WhenParentFailedOnlyOnSucceeded_AwaitingContinuationsAreDeleted()
    {
        var ctx = _fixture.CreateContext();

        // Batch parent with one failed child
        var parentId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 1,
            ContinuationOptions = ContinuationOptions.OnlyOnSucceeded,
        });
        ctx.Set<Job>().Add(new Job
        {
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentId,
        });

        // Continuation batch waiting on parent
        var continuationBatchId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = continuationBatchId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = parentId,
        });
        var continuationChildId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = continuationChildId,
            Kind = JobKind.Job,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = continuationBatchId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Run orchestration twice (first finalizes parent, second should clean up continuations)
        var orchCtx1 = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx1, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);
        var orchCtx2 = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx2, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        var readCtx = _fixture.CreateContext();

        // Parent should be Failed (child failed, OnlyOnSucceeded)
        var parent = await readCtx.Set<Job>().FindAsync([parentId], Xunit.TestContext.Current.CancellationToken);
        parent.ShouldNotBeNull();
        parent.CurrentState.ShouldBe(State.Failed);

        // Continuation batch and its child should stay Awaiting (condition not met, but parent could be requeued)
        var contBatch = await readCtx.Set<Job>().FindAsync([continuationBatchId], Xunit.TestContext.Current.CancellationToken);
        contBatch.ShouldNotBeNull();
        contBatch.CurrentState.ShouldBe(State.Awaiting, "Continuation of failed OnlyOnSucceeded parent should stay Awaiting");

        var contChild = await readCtx.Set<Job>().FindAsync([continuationChildId], Xunit.TestContext.Current.CancellationToken);
        contChild.ShouldNotBeNull();
        contChild.CurrentState.ShouldBe(State.Awaiting, "Children of awaiting continuation should also stay Awaiting");
    }

    [TimedFact]
    public async Task RunOrchestration_MessageWithDeletedAndCompletedChildren_CompletesMessage()
    {
        // Addon policy axis: a Skip-mode [Mutex]/[RateLimit] on a message handler deletes the
        // surplus routed child. Deleted is SETTLED, not pending — treating it as pending left the
        // parent Processing forever (also reachable via a manual child delete, a latent hang).
        // Deliberately-skipped work is not failed work, so the message completes.
        var ctx = _fixture.CreateContext();
        var messageId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = messageId,
            Kind = JobKind.Message,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 2,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = messageId,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Deleted,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = messageId,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var orchCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var message = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == messageId, Xunit.TestContext.Current.CancellationToken);
        message.ShouldNotBeNull();
        message.CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task RunOrchestration_MessageWithAllChildrenDeleted_DeletesMessage()
    {
        // Every routed child was policy-skipped (or an operator cancelled a batch's children):
        // the fan-out is settled so the parent must finalize rather than hang — but with no
        // Completed child and no Failed child it finalizes DELETED, not Completed. Reporting a
        // fully-skipped message (or a cancelled batch) as a success would be a false green.
        var ctx = _fixture.CreateContext();
        var messageId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = messageId,
            Kind = JobKind.Message,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 1,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Deleted,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            ParentJobId = messageId,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var orchCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var message = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == messageId, Xunit.TestContext.Current.CancellationToken);
        message.ShouldNotBeNull();
        message.CurrentState.ShouldBe(State.Deleted);
    }

    [TimedFact]
    public async Task RunOrchestration_CancelledBatch_FinalizesDeletedNotCompleted()
    {
        // CancelBatch deletes the descendants but leaves the batch row Processing. The batch must
        // finalize (before the Deleted-children-settle change it hung forever) — and as Deleted:
        // an operator who cancelled a batch must not see it reported as Completed.
        var ctx = _fixture.CreateContext();
        var batchId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = batchId,
            Kind = JobKind.Batch,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            JobCount = 2,
        });

        for (var i = 0; i < 2; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Deleted,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = batchId,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var orchCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateOrchestrator(orchCtx, TimeProvider.System, TimeSpan.FromDays(1)).RunOrchestrationCoreAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var batch = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == batchId, Xunit.TestContext.Current.CancellationToken);
        batch.ShouldNotBeNull();
        batch.CurrentState.ShouldBe(State.Deleted);
    }
}
