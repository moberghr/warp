using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Orchestration;

// Pins the bounded-batch contract of Orchestrator's three phases (FinalizeParents,
// ActivateContinuations, FailChildrenOfDeletedParents). The .Take(ServerTaskBatchSize)
// guard exists so a single orchestration pass cannot churn through an unbounded backlog
// while holding the distributed lock; this is what keeps the loop responsive when the
// system catches up from an outage. The drain test asserts (a) the batch is bounded per
// iteration and (b) successive iterations actually progress — both have to hold for the
// guard to be safe.
[GenerateDatabaseTests]
public abstract class OrchestrationBacklogDrainTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected OrchestrationBacklogDrainTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // FinalizeParents reads ready parents as one branch per kind (rule 6.11). Both kinds must be
    // found in the same pass, and the batch size must cap the two branches TOGETHER, not each.
    [TimedFact]
    public async Task FinalizeParents_MessageAndBatchParentsReady_FinalizesBothKindsWithinOneBatchSize()
    {
        const int batchSize = 3;
        var ct = Xunit.TestContext.Current.CancellationToken;

        var seedCtx = _fixture.CreateContext();
        foreach (var kind in new[] { JobKind.Message, JobKind.Message, JobKind.Batch, JobKind.Batch })
        {
            var parentId = Guid.NewGuid();
            seedCtx.Set<Job>().Add(new Job
            {
                Id = parentId,
                Kind = kind,
                CurrentState = State.Processing,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
            });
            seedCtx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Completed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = parentId,
            });
        }

        await seedCtx.SaveChangesAsync(ct);

        await TestTasks.CreateOrchestrator(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromDays(1), serverTaskBatchSize: batchSize)
            .RunOrchestrationCoreAsync(CancellationToken.None);

        var afterPass1 = await CompletedParentKinds(ct);
        afterPass1.Count.ShouldBe(batchSize);
        afterPass1.ShouldContain(JobKind.Message);
        afterPass1.ShouldContain(JobKind.Batch);

        await TestTasks.CreateOrchestrator(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromDays(1), serverTaskBatchSize: batchSize)
            .RunOrchestrationCoreAsync(CancellationToken.None);

        var afterPass2 = await CompletedParentKinds(ct);
        afterPass2.Count(x => x == JobKind.Message).ShouldBe(2);
        afterPass2.Count(x => x == JobKind.Batch).ShouldBe(2);
    }

    [TimedFact]
    public async Task FinalizeParents_BacklogLargerThanBatchSize_DrainsAcrossIterations()
    {
        const int parentCount = 7;
        const int batchSize = 3;

        var seedCtx = _fixture.CreateContext();
        for (var i = 0; i < parentCount; i++)
        {
            var parentId = Guid.NewGuid();
            seedCtx.Set<Job>().Add(new Job
            {
                Id = parentId,
                Kind = JobKind.Message,
                CurrentState = State.Processing,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
            });
            seedCtx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Completed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = parentId,
            });
        }

        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // First pass — exactly batchSize parents should finalize. Anything beyond that means
        // the .Take(batchSize) guard regressed.
        var pass1Ctx = _fixture.CreateContext();
        await TestTasks.CreateOrchestrator(pass1Ctx, TimeProvider.System, TimeSpan.FromDays(1), serverTaskBatchSize: batchSize)
            .RunOrchestrationCoreAsync(CancellationToken.None);

        var readCtx1 = _fixture.CreateContext();
        var completedAfterPass1 = await readCtx1.Set<Job>()
            .AsNoTracking()
            .Where(j => j.Kind == JobKind.Message && j.CurrentState == State.Completed)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        completedAfterPass1.ShouldBe(batchSize);

        // Successive passes must keep draining — assert until the backlog is empty.
        // Bounded loop count so a regression that fails to make progress surfaces as a
        // test failure rather than a hang.
        var totalPasses = 1;
        while (completedAfterPass1 < parentCount && totalPasses <= parentCount)
        {
            var nextCtx = _fixture.CreateContext();
            await TestTasks.CreateOrchestrator(nextCtx, TimeProvider.System, TimeSpan.FromDays(1), serverTaskBatchSize: batchSize)
                .RunOrchestrationCoreAsync(CancellationToken.None);
            totalPasses++;

            var snapshotCtx = _fixture.CreateContext();
            completedAfterPass1 = await snapshotCtx.Set<Job>()
                .AsNoTracking()
                .Where(j => j.Kind == JobKind.Message && j.CurrentState == State.Completed)
                .CountAsync(Xunit.TestContext.Current.CancellationToken);
        }

        completedAfterPass1.ShouldBe(parentCount);

        // 7 parents / batchSize 3 → ⌈7/3⌉ = 3 passes. Allow a fourth pass to absorb DB
        // visibility skew but anything beyond that means we're doing trivial work per pass.
        totalPasses.ShouldBeLessThanOrEqualTo(4);
    }

    [TimedFact]
    public async Task ActivateContinuations_BacklogLargerThanBatchSize_DrainsAcrossIterations()
    {
        const int continuationCount = 7;
        const int batchSize = 3;

        // One Completed parent owns N awaiting continuation children. ActivateContinuations
        // should flip them Enqueued in batches of `batchSize` per iteration.
        var seedCtx = _fixture.CreateContext();
        var parentId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        seedCtx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Batch,
            CurrentState = State.Completed,
            CreateTime = now,
            ScheduleTime = now,
            Queue = "default",
            ExpireAt = now.AddDays(1),
        });
        for (var i = 0; i < continuationCount; i++)
        {
            seedCtx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Awaiting,
                CreateTime = now,
                ScheduleTime = now,
                Queue = "default",
                ParentJobId = parentId,
            });
        }

        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var pass1Ctx = _fixture.CreateContext();
        await TestTasks.CreateOrchestrator(pass1Ctx, TimeProvider.System, TimeSpan.FromDays(1), serverTaskBatchSize: batchSize)
            .RunOrchestrationCoreAsync(CancellationToken.None);

        var readCtx1 = _fixture.CreateContext();
        var enqueuedAfterPass1 = await readCtx1.Set<Job>()
            .AsNoTracking()
            .Where(j => j.ParentJobId == parentId && j.CurrentState == State.Enqueued)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        enqueuedAfterPass1.ShouldBe(batchSize);

        var totalPasses = 1;
        while (enqueuedAfterPass1 < continuationCount && totalPasses <= continuationCount)
        {
            var nextCtx = _fixture.CreateContext();
            await TestTasks.CreateOrchestrator(nextCtx, TimeProvider.System, TimeSpan.FromDays(1), serverTaskBatchSize: batchSize)
                .RunOrchestrationCoreAsync(CancellationToken.None);
            totalPasses++;

            var snapshotCtx = _fixture.CreateContext();
            enqueuedAfterPass1 = await snapshotCtx.Set<Job>()
                .AsNoTracking()
                .Where(j => j.ParentJobId == parentId && j.CurrentState == State.Enqueued)
                .CountAsync(Xunit.TestContext.Current.CancellationToken);
        }

        enqueuedAfterPass1.ShouldBe(continuationCount);
        totalPasses.ShouldBeLessThanOrEqualTo(4);
    }

    [TimedFact]
    public async Task FailChildrenOfDeletedParents_BacklogLargerThanBatchSize_DrainsAcrossIterations()
    {
        const int orphanCount = 7;
        const int batchSize = 3;

        var seedCtx = _fixture.CreateContext();
        var deletedParentId = Guid.NewGuid();
        seedCtx.Set<Job>().Add(new Job
        {
            Id = deletedParentId,
            Kind = JobKind.Batch,
            CurrentState = State.Deleted,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        for (var i = 0; i < orphanCount; i++)
        {
            seedCtx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Awaiting,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = deletedParentId,
            });
        }

        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var pass1Ctx = _fixture.CreateContext();
        await TestTasks.CreateOrchestrator(pass1Ctx, TimeProvider.System, TimeSpan.FromDays(1), serverTaskBatchSize: batchSize)
            .RunOrchestrationCoreAsync(CancellationToken.None);

        var readCtx1 = _fixture.CreateContext();
        var failedAfterPass1 = await readCtx1.Set<Job>()
            .AsNoTracking()
            .Where(j => j.Kind == JobKind.Job && j.CurrentState == State.Failed)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        failedAfterPass1.ShouldBe(batchSize);

        var totalPasses = 1;
        while (failedAfterPass1 < orphanCount && totalPasses <= orphanCount)
        {
            var nextCtx = _fixture.CreateContext();
            await TestTasks.CreateOrchestrator(nextCtx, TimeProvider.System, TimeSpan.FromDays(1), serverTaskBatchSize: batchSize)
                .RunOrchestrationCoreAsync(CancellationToken.None);
            totalPasses++;

            var snapshotCtx = _fixture.CreateContext();
            failedAfterPass1 = await snapshotCtx.Set<Job>()
                .AsNoTracking()
                .Where(j => j.Kind == JobKind.Job && j.CurrentState == State.Failed)
                .CountAsync(Xunit.TestContext.Current.CancellationToken);
        }

        failedAfterPass1.ShouldBe(orphanCount);
        totalPasses.ShouldBeLessThanOrEqualTo(4);
    }

    private async Task<List<JobKind>> CompletedParentKinds(CancellationToken ct)
    {
        var ctx = _fixture.CreateContext();

        return await ctx.Set<Job>()
            .AsNoTracking()
            .Where(x => x.Kind == JobKind.Message || x.Kind == JobKind.Batch)
            .Where(x => x.CurrentState == State.Completed)
            .Select(x => x.Kind)
            .ToListAsync(ct);
    }
}
