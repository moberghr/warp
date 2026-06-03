using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Helper;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

[GenerateDatabaseTests]
public abstract class PublisherOverloadTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected PublisherOverloadTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Publisher<TestContext> CreatePublisher(TestContext ctx)
    {
        return new Publisher<TestContext>(ctx, TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);
    }

    [TimedFact]
    public async Task Enqueue_WithJobParameters_SetsAllFields()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);
        var parentId = Guid.NewGuid();
        var futureTime = DateTime.UtcNow.AddHours(2);

        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var jobParams = new JobParameters
        {
            Queue = "high-priority",
            ParentId = parentId,
            ScheduleTime = futureTime,
        };

        // Act
        var id = await publisher.Enqueue(new UnitRequest(), jobParams);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.Queue.ShouldBe("high-priority");
        job.ParentJobId.ShouldBe(parentId);
        job.ScheduleTime.ShouldBeGreaterThan(DateTime.UtcNow.AddHours(1));
    }

    [TimedFact]
    public async Task Schedule_WithQueue_SetsScheduleTimeAndQueue()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);
        var futureTime = DateTime.UtcNow.AddHours(2);

        // Act
        var id = await publisher.Schedule(new UnitRequest(), futureTime, "critical");
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.Queue.ShouldBe("critical");
        job.ScheduleTime.ShouldBeGreaterThan(DateTime.UtcNow.AddHours(1));
    }

    [TimedFact]
    public async Task Schedule_WithParent_SetsScheduleTimeAndParent()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);
        var parentId = Guid.NewGuid();
        var futureTime = DateTime.UtcNow.AddHours(2);

        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var id = await publisher.Schedule(new UnitRequest(), futureTime, parentId);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.ParentJobId.ShouldBe(parentId);
        job.ScheduleTime.ShouldBeGreaterThan(DateTime.UtcNow.AddHours(1));
        job.CurrentState.ShouldBe(State.Awaiting);
    }

    [TimedFact]
    public async Task Schedule_WithParentAndQueue_SetsAllFields()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);
        var parentId = Guid.NewGuid();
        var futureTime = DateTime.UtcNow.AddHours(2);

        ctx.Set<Job>().Add(new Job
        {
            Id = parentId,
            Kind = JobKind.Batch,
            CurrentState = State.Awaiting,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var id = await publisher.Schedule(new UnitRequest(), futureTime, parentId, "critical");
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.ParentJobId.ShouldBe(parentId);
        job.Queue.ShouldBe("critical");
        job.ScheduleTime.ShouldBeGreaterThan(DateTime.UtcNow.AddHours(1));
    }
}
