using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Admin;

[GenerateDatabaseTests]
public abstract class FailedJobTypeFilterTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected FailedJobTypeFilterTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetFailedJobTypeCounts_ReturnsCorrectGroupings()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 5; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                Type = "TypeA",
            });
        }

        for (var i = 0; i < 3; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                Type = "TypeB",
            });
        }

        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            Type = "TypeC",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetFailedJobTypeCounts();

        // Assert
        result.Count.ShouldBe(3);
        result[0].Type.ShouldBe("TypeA");
        result[0].Count.ShouldBe(5);
        result[1].Type.ShouldBe("TypeB");
        result[1].Count.ShouldBe(3);
        result[2].Type.ShouldBe("TypeC");
        result[2].Count.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetFailedJobTypeCounts_ExcludesNonFailedJobs()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 3; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                Type = "TypeA",
            });
        }

        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            Type = "TypeA",
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            Type = "TypeB",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetFailedJobTypeCounts();

        // Assert
        result.Count.ShouldBe(1);
        result[0].Type.ShouldBe("TypeA");
        result[0].Count.ShouldBe(3);
    }

    [TimedFact]
    public async Task GetFailedJobsByType_ReturnsOnlyMatchingType()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 4; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                Type = "TypeA",
            });
        }

        for (var i = 0; i < 2; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                Type = "TypeB",
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetFailedJobsByType(new BaseListRequest { Page = 0, PageSize = 100 }, "TypeA");

        // Assert
        result.TotalCount.ShouldBe(4);
        result.Items.Count.ShouldBe(4);
        result.Items.ShouldAllBe(j => j.Type == "TypeA");
    }

    [TimedFact]
    public async Task DeleteFailedJobsByType_DeletesAllMatchingJobs()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 5; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                Type = "TypeA",
            });
        }

        for (var i = 0; i < 3; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                Type = "TypeB",
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.DeleteFailedJobsByType("TypeA");

        // Assert
        result.Succeeded.ShouldBe(5);

        var readCtx = _fixture.CreateContext();
        var remainingTypeA = await readCtx.Set<Job>()
            .Where(j => j.Type == "TypeA" && j.CurrentState == State.Failed)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        remainingTypeA.ShouldBe(0);

        var remainingTypeB = await readCtx.Set<Job>()
            .Where(j => j.Type == "TypeB" && j.CurrentState == State.Failed)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        remainingTypeB.ShouldBe(3);
    }

    [TimedFact]
    public async Task RequeueFailedJobsByType_RequeuesAllMatchingJobs()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var typeAIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var id = Guid.NewGuid();
            typeAIds.Add(id);
            ctx.Set<Job>().Add(new Job
            {
                Id = id,
                Kind = JobKind.Job,
                CurrentState = State.Failed,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                Type = "TypeA",
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        var result = await svc.RequeueFailedJobsByType("TypeA");

        // Assert
        result.Succeeded.ShouldBe(3);

        var readCtx = _fixture.CreateContext();
        foreach (var id in typeAIds)
        {
            var job = await readCtx.Set<Job>().FindAsync([id], Xunit.TestContext.Current.CancellationToken);
            job.ShouldNotBeNull();
            job.CurrentState.ShouldBe(State.Enqueued);
        }
    }

    [TimedFact]
    public async Task GetJobsByType_NoStateFilter_ReturnsAllStatesForThatType()
    {
        // #1: the clickable-type list spans every state, unlike GetFailedJobsByType.
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(TypedJob("SyncBooking", State.Completed));
        ctx.Set<Job>().Add(TypedJob("SyncBooking", State.Failed));
        ctx.Set<Job>().Add(TypedJob("SyncBooking", State.Processing));
        ctx.Set<Job>().Add(TypedJob("OtherJob", State.Completed));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetJobsByType(new BaseListRequest { Page = 0, PageSize = 100 }, "SyncBooking", null);

        result.TotalCount.ShouldBe(3);
        result.Items.ShouldAllBe(j => j.Type == "SyncBooking");
    }

    [TimedFact]
    public async Task GetJobsByType_WithStateFilter_ReturnsOnlyThatState()
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(TypedJob("SyncBooking", State.Completed));
        ctx.Set<Job>().Add(TypedJob("SyncBooking", State.Failed));
        ctx.Set<Job>().Add(TypedJob("SyncBooking", State.Failed));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);
        var result = await svc.GetJobsByType(new BaseListRequest { Page = 0, PageSize = 100 }, "SyncBooking", State.Failed);

        result.TotalCount.ShouldBe(2);
        result.Items.ShouldAllBe(j => j.CurrentState == State.Failed);
    }

    private static Job TypedJob(string type, State state) =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = state,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            Type = type,
        };
}
