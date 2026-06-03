using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Logging;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

[GenerateDatabaseTests]
public abstract class PublisherTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected PublisherTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Publisher<TestContext> CreatePublisher(TestContext ctx)
    {
        return new Publisher<TestContext>(ctx, TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);
    }

    [TimedFact]
    public async Task Publish_TimeoutMessage_SchedulesWithDelay()
    {
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);
        var before = DateTime.UtcNow;

        var id = await publisher.Publish(new TimeoutSampleMessage());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.Kind.ShouldBe(JobKind.Message);
        job.CurrentState.ShouldBe(State.Scheduled);
        var delay = job.ScheduleTime - before;
        delay.ShouldBeGreaterThan(TimeSpan.FromSeconds(4));
    }

    [TimedFact]
    public async Task Publish_TimeoutMessageWithZeroDelay_IsImmediate()
    {
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);

        var id = await publisher.Publish(new ZeroDelayTimeoutMessage());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Enqueued);
    }

    private sealed class TimeoutSampleMessage : ITimeoutMessage
    {
        public TimeSpan Delay => TimeSpan.FromSeconds(5);
    }

    private sealed class ZeroDelayTimeoutMessage : ITimeoutMessage
    {
        public TimeSpan Delay => TimeSpan.Zero;
    }

    [TimedFact]
    public async Task Publish_CreatesMessageKindJobWithEnqueuedState()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);

        // Act
        var id = await publisher.Publish(new SingleHandlerMessage());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.Kind.ShouldBe(JobKind.Message);
        job.CurrentState.ShouldBe(State.Enqueued);
    }

    [TimedFact]
    public async Task Publish_WithQueue_SetsQueueOnJob()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);

        // Act
        var id = await publisher.Publish(new SingleHandlerMessage(), "critical");
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.Queue.ShouldBe("critical");
    }

    [TimedFact]
    public async Task Publish_SetsTraceIdToJobId()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);

        // Act
        var id = await publisher.Publish(new SingleHandlerMessage());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.TraceId.ShouldBe(job.Id);
    }

    [TimedFact]
    public async Task Enqueue_CreatesJobKindJobWithEnqueuedState()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);

        // Act
        var id = await publisher.Enqueue(new UnitRequest());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.Kind.ShouldBe(JobKind.Job);
        job.CurrentState.ShouldBe(State.Enqueued);
    }

    [TimedFact]
    public async Task Enqueue_WithParentId_SetsParentAndAwaitingState()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);
        var parentId = Guid.NewGuid();

        // Insert a parent job so FK is satisfied
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
        var id = await publisher.Enqueue(new UnitRequest(), parentId);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.ParentJobId.ShouldBe(parentId);
        job.CurrentState.ShouldBe(State.Awaiting);
    }

    [TimedFact]
    public async Task Enqueue_CreatesJobLog()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);

        // Act
        var id = await publisher.Enqueue(new UnitRequest());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var logs = await readCtx.Set<JobLog>().Where(l => l.JobId == id).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        logs.Count.ShouldBe(1);
        logs[0].EventType.ShouldBe("Created");
    }

    [TimedFact]
    public async Task Schedule_SetsScheduleTime()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);
        var futureTime = DateTime.UtcNow.AddHours(2);

        // Act
        var id = await publisher.Schedule(new UnitRequest(), futureTime);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.ScheduleTime.ShouldBeGreaterThan(DateTime.UtcNow.AddHours(1));
    }

    [TimedFact]
    public async Task Enqueue_WithParent_InheritsParentTrace_SameContext()
    {
        // Arrange: parent and child created in same context before SaveChanges
        // (matches real usage: publisher.Enqueue parent, publisher.Enqueue child, then SaveChangesAsync)
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);

        var parentId = await publisher.Enqueue(new UnitRequest());
        var childId = await publisher.Enqueue(new UnitRequest(), parentId);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var parent = await readCtx.Set<Job>().FindAsync([parentId], Xunit.TestContext.Current.CancellationToken);
        var child = await readCtx.Set<Job>().FindAsync([childId], Xunit.TestContext.Current.CancellationToken);
        parent.ShouldNotBeNull();
        child.ShouldNotBeNull();
        child.TraceId.ShouldBe(parent.TraceId);
    }

    [TimedFact]
    public async Task Enqueue_WithParent_InheritsParentTrace_SeparateContext()
    {
        // Arrange: parent already committed to DB
        var setupCtx = _fixture.CreateContext();
        var setupPublisher = CreatePublisher(setupCtx);
        var parentId = await setupPublisher.Enqueue(new UnitRequest());
        await setupCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act: child created in new context
        var actCtx = _fixture.CreateContext();
        var actPublisher = CreatePublisher(actCtx);
        var childId = await actPublisher.Enqueue(new UnitRequest(), parentId);
        await actCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var parent = await readCtx.Set<Job>().FindAsync([parentId], Xunit.TestContext.Current.CancellationToken);
        var child = await readCtx.Set<Job>().FindAsync([childId], Xunit.TestContext.Current.CancellationToken);
        parent.ShouldNotBeNull();
        child.ShouldNotBeNull();
        child.TraceId.ShouldBe(parent.TraceId);
    }

    [TimedFact]
    public async Task Enqueue_WithoutParent_GetsOwnTrace()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);

        // Act
        var jobId = await publisher.Enqueue(new UnitRequest());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.TraceId.ShouldBe(jobId);
    }

    [TimedFact]
    public async Task Publish_InsideExecutionContext_InheritsTraceAndSpawnedBy()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);
        var parentJobId = Guid.NewGuid();
        var parentTraceId = Guid.NewGuid();

        JobExecutionContext.Current = new JobExecutionInfo
        {
            JobId = parentJobId,
            TraceId = parentTraceId,
        };

        try
        {
            // Act
            var id = await publisher.Publish(new SingleHandlerMessage());
            await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

            // Assert
            var readCtx = _fixture.CreateContext();
            var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
            job.ShouldNotBeNull();
            job.TraceId.ShouldBe(parentTraceId);
            job.SpawnedByJobId.ShouldBe(parentJobId);
        }
        finally
        {
            JobExecutionContext.Current = null;
        }
    }

    [TimedFact]
    public async Task SaveChangesAsync_PersistsEnqueuedJob()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = CreatePublisher(ctx);
        var id = await publisher.Enqueue(new UnitRequest());

        // Act
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([id], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Enqueued);
    }
}
