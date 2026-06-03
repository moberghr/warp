using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker.Services;

namespace Warp.Tests.Messaging;

[GenerateDatabaseTests]
public abstract class MessageRoutingTaskTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected MessageRoutingTaskTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IServiceScopeFactory BuildScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddSingleton<MultiHandlerCounter>();
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    [TimedFact]
    public async Task RunMessageRouting_WithEnqueuedMessage_CreatesChildJobs()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var messageId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = messageId,
            Kind = JobKind.Message,
            CurrentState = State.Enqueued,
            Type = typeof(SingleHandlerMessage).AssemblyQualifiedName,
            Message = "{}",
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var scopeFactory = BuildScopeFactory();

        // Act
        var routeCtx = _fixture.CreateContext();
        var task = TestTasks.CreateMessageRouter(routeCtx, scopeFactory, TimeProvider.System);
        var routed = await task.RunMessageRoutingAsync(CancellationToken.None);

        // Assert
        routed.ShouldBeGreaterThan(0);
        var readCtx = _fixture.CreateContext();
        var children = await readCtx.Set<Job>()
            .Where(j => j.ParentJobId == messageId && j.Kind == JobKind.Job)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        children.Count.ShouldBeGreaterThan(0);
    }

    [TimedFact]
    public async Task RunMessageRouting_SetsMessageToProcessing()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var messageId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = messageId,
            Kind = JobKind.Message,
            CurrentState = State.Enqueued,
            Type = typeof(SingleHandlerMessage).AssemblyQualifiedName,
            Message = "{}",
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var scopeFactory = BuildScopeFactory();

        // Act
        var routeCtx = _fixture.CreateContext();
        var task = TestTasks.CreateMessageRouter(routeCtx, scopeFactory, TimeProvider.System);
        await task.RunMessageRoutingAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var message = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == messageId, Xunit.TestContext.Current.CancellationToken);
        message.ShouldNotBeNull();
        message.CurrentState.ShouldBe(State.Processing);
    }

    [TimedFact]
    public async Task RunMessageRouting_ChildJobsInheritQueue()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var messageId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = messageId,
            Kind = JobKind.Message,
            CurrentState = State.Enqueued,
            Type = typeof(SingleHandlerMessage).AssemblyQualifiedName,
            Message = "{}",
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "critical",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var scopeFactory = BuildScopeFactory();

        // Act
        var routeCtx = _fixture.CreateContext();
        var task = TestTasks.CreateMessageRouter(routeCtx, scopeFactory, TimeProvider.System);
        await task.RunMessageRoutingAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var children = await readCtx.Set<Job>()
            .Where(j => j.ParentJobId == messageId && j.Kind == JobKind.Job)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        children.ShouldAllBe(c => c.Queue == "critical");
    }

    [TimedFact]
    public async Task RunMessageRouting_NoMessages_ReturnsZero()
    {
        // Arrange — empty DB
        var scopeFactory = BuildScopeFactory();

        // Act
        var ctx = _fixture.CreateContext();
        var task = TestTasks.CreateMessageRouter(ctx, scopeFactory, TimeProvider.System);
        var routed = await task.RunMessageRoutingAsync(CancellationToken.None);

        // Assert
        routed.ShouldBe(0);
    }

    [TimedFact]
    public async Task RunMessageRouting_MultipleMessages_RoutesAll()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var messageIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var messageId = Guid.NewGuid();
            messageIds.Add(messageId);
            ctx.Set<Job>().Add(new Job
            {
                Id = messageId,
                Kind = JobKind.Message,
                CurrentState = State.Enqueued,
                Type = typeof(SingleHandlerMessage).AssemblyQualifiedName,
                Message = "{}",
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var scopeFactory = BuildScopeFactory();

        // Act
        var routeCtx = _fixture.CreateContext();
        var task = TestTasks.CreateMessageRouter(routeCtx, scopeFactory, TimeProvider.System);
        var routed = await task.RunMessageRoutingAsync(CancellationToken.None);

        // Assert
        routed.ShouldBe(3);
        var readCtx = _fixture.CreateContext();
        foreach (var mid in messageIds)
        {
            var msg = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == mid, Xunit.TestContext.Current.CancellationToken);
            msg.ShouldNotBeNull();
            msg.CurrentState.ShouldBe(State.Processing);
        }
    }

    /// <summary>
    /// HIGH: No JobLog when message routing finds 0 handlers.
    /// Messages marked Failed should have a log explaining why.
    /// </summary>
    [TimedFact]
    public async Task MessageRouting_NoHandlers_CreatesFailedLogEntry()
    {
        // Arrange: a message with a type that has no registered handlers
        var ctx = _fixture.CreateContext();
        var messageId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = messageId,
            Kind = JobKind.Message,
            CurrentState = State.Enqueued,
            Type = "NonExistent.Type, NonExistent.Assembly",
            Message = "{}",
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act: run message routing
        var routeCtx = _fixture.CreateContext();
        var scopeFactory = CreateScopeFactory();
        var task = TestTasks.CreateMessageRouter(routeCtx, scopeFactory, TimeProvider.System);
        await task.RunMessageRoutingAsync(CancellationToken.None);

        // Assert: message should be Failed with a log entry explaining why
        var readCtx = _fixture.CreateContext();
        var message = await readCtx.Set<Job>().FindAsync([messageId], Xunit.TestContext.Current.CancellationToken);
        message.ShouldNotBeNull();
        message.CurrentState.ShouldBe(State.Failed);

        var logs = await readCtx.Set<JobLog>().Where(l => l.JobId == messageId).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        logs.ShouldNotBeEmpty("Failed message should have a log entry explaining the failure");
        logs.ShouldContain(l => l.EventType == "Failed");
    }

    [TimedFact]
    public async Task RunMessageRouting_WritesRoutedJobLog_OnTheMessage_WithHandlerCount()
    {
        // Per-message audit: when MessageRouter picks up a Kind=Message and creates handler
        // children, the message row gets its own "Routed" JobLog entry recording when the
        // pickup happened and how many handlers got fan-out. Closes the per-row observability
        // gap that previously hid MessageRouter pickup latency from the dashboard / dump.
        var ctx = _fixture.CreateContext();
        var messageId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = messageId,
            Kind = JobKind.Message,
            CurrentState = State.Enqueued,
            Type = typeof(MultiRequest).AssemblyQualifiedName,
            Message = "{}",
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var scopeFactory = BuildScopeFactory();

        var routeCtx = _fixture.CreateContext();
        var task = TestTasks.CreateMessageRouter(routeCtx, scopeFactory, TimeProvider.System);
        await task.RunMessageRoutingAsync(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var logs = await readCtx.Set<JobLog>()
            .Where(l => l.JobId == messageId && l.EventType == "Routed")
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        logs.ShouldHaveSingleItem();
        logs[0].Level.ShouldBe("Information");
        logs[0].Message.ShouldBe("Routed to 2 handler(s)");
    }

    [TimedFact]
    public async Task RunMessageRouting_FailedMessage_DoesNotWriteRoutedJobLog()
    {
        // Defensive: a message that fails to route (unknown type) must NOT emit a "Routed"
        // log. The Routed event is reserved for the successful routing path.
        var ctx = _fixture.CreateContext();
        var messageId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = messageId,
            Kind = JobKind.Message,
            CurrentState = State.Enqueued,
            Type = "Definitely.Does.Not.Exist, NoSuchAssembly",
            Message = "{}",
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var scopeFactory = CreateScopeFactory();

        var routeCtx = _fixture.CreateContext();
        var task = TestTasks.CreateMessageRouter(routeCtx, scopeFactory, TimeProvider.System);
        await task.RunMessageRoutingAsync(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var routedLogs = await readCtx.Set<JobLog>()
            .Where(l => l.JobId == messageId && l.EventType == "Routed")
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        routedLogs.ShouldBeEmpty();
    }
}
