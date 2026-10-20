using Microsoft.EntityFrameworkCore;
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
public abstract class JobGroupQueryServiceTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected JobGroupQueryServiceTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetJobGroups_Message_ReturnsMessageKindJobs()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 2; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Message,
                CurrentState = State.Processing,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
            });
        }

        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobGroupQueryService<TestContext>(_fixture.CreateContext());
        var result = await svc.GetJobGroups(JobKind.Message, new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert
        result.TotalCount.ShouldBe(2);
    }

    [TimedFact]
    public async Task GetJobGroups_Batch_ReturnsBatchKindJobs()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
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
                Kind = JobKind.Batch,
                CurrentState = State.Awaiting,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobGroupQueryService<TestContext>(_fixture.CreateContext());
        var result = await svc.GetJobGroups(JobKind.Batch, new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert
        result.TotalCount.ShouldBe(2);
    }

    [TimedFact]
    public async Task GetJobGroups_WithStateFilter_FiltersCorrectly()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Message,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Message,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobGroupQueryService<TestContext>(_fixture.CreateContext());
        var result = await svc.GetJobGroups(JobKind.Message, new BaseListRequest { Page = 0, PageSize = 20 }, state: "completed");

        // Assert
        result.TotalCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetJobGroupById_ReturnsDetailWithSpawnedJobsCount()
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

        for (var i = 0; i < 3; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Enqueued,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                ParentJobId = messageId,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobGroupQueryService<TestContext>(_fixture.CreateContext());
        var result = await svc.GetJobGroupById(messageId);

        // Assert
        result.ShouldNotBeNull();
        result.SpawnedJobsCount.ShouldBe(3);
    }

    [TimedFact]
    public async Task GetJobGroupById_ManyContinuationsAndChattyLogs_CapsListAndKeepsLifecycleOnly()
    {
        var batchId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-10);
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job { Id = batchId, Kind = JobKind.Batch, CurrentState = State.Completed, CreateTime = start, ScheduleTime = start, Queue = "default" });
        for (var i = 0; i < UnifiedJobDetailModel.MaxRelatedJobs + 5; i++)
        {
            ctx.Set<Job>().Add(new Job { Id = Guid.NewGuid(), Kind = JobKind.Batch, ParentJobId = batchId, CurrentState = State.Awaiting, CreateTime = start.AddSeconds(i), ScheduleTime = start, Queue = "default" });
        }

        ctx.Set<JobLog>().Add(new JobLog { JobId = batchId, EventType = "Created", Timestamp = start, Level = "Information", Message = string.Empty });
        ctx.Set<JobLog>().Add(new JobLog { JobId = batchId, EventType = "Log", Timestamp = start.AddSeconds(1), Level = "Information", Message = "output" });
        ctx.Set<JobLog>().Add(new JobLog { JobId = batchId, EventType = "Progress", Timestamp = start.AddSeconds(2), Level = "Information", Message = string.Empty, Name = string.Empty, Value = 50 });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var svc = new JobGroupQueryService<TestContext>(_fixture.CreateContext());
        var result = await svc.GetJobGroupById(batchId);

        result.ShouldNotBeNull();
        result.Continuations.Count.ShouldBe(UnifiedJobDetailModel.MaxRelatedJobs);
        result.ContinuationCount.ShouldBe(UnifiedJobDetailModel.MaxRelatedJobs + 5);
        result.Logs.ShouldHaveSingleItem().EventType.ShouldBe("Created");
    }

    [TimedFact]
    public async Task GetJobGroupById_NonExistent_ReturnsNull()
    {
        // Act
        var svc = new JobGroupQueryService<TestContext>(_fixture.CreateContext());
        var result = await svc.GetJobGroupById(Guid.NewGuid());

        // Assert
        result.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetJobGroupJobs_ReturnsChildJobs()
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
        });

        for (var i = 0; i < 3; i++)
        {
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
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new JobGroupQueryService<TestContext>(_fixture.CreateContext());
        var result = await svc.GetJobGroupJobs(batchId, new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert
        result.TotalCount.ShouldBe(3);
    }

    [TimedFact]
    public async Task GetJobGroupJobCounts_ReturnsStateBreakdown()
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
        var svc = new JobGroupQueryService<TestContext>(_fixture.CreateContext());
        var result = await svc.GetJobGroupJobCounts(batchId);

        // Assert
        result["completed"].ShouldBe(2);
        result["failed"].ShouldBe(1);
    }
}
