using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Logging;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Observability;

[GenerateDatabaseTests]
public abstract class ActivityTraceTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly string[] DefaultQueues = ["default"];

    // Harness attached in the constructor — NOT in InitializeAsync — because AsyncLocal mutations
    // made inside an async method don't propagate back to the caller in .NET, so a sentinel set
    // in InitializeAsync would be lost by the time the test method runs. The constructor is
    // synchronous, so its AsyncLocal write is captured into the ExecutionContext that the test
    // method's first await flows into.
    private readonly ActivityListenerHarness _harness;

    protected ActivityTraceTestsBase(IDatabaseFixture fixture)
    {
        _fixture = fixture;
        _harness = new ActivityListenerHarness();
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();

        var ctx = _fixture.CreateContext();
        ctx.Set<Server>().Add(new Server
        {
            Id = ServerId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
            ServiceCount = 1,
        });
        ctx.Set<Warp.Core.Data.Entities.Worker>().Add(new Warp.Core.Data.Entities.Worker
        {
            Id = WorkerId,
            ServerId = ServerId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _harness.Dispose();

        return ValueTask.CompletedTask;
    }

    private (WarpWorkerService<TestContext> Worker, ActivityCapture Capture) CreateWorker()
    {
        var capture = new ActivityCapture();
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging(builder => builder.AddProvider(new JobLoggerProvider()));
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddScoped<Warp.Core.IWarpServerContext>(x => new Warp.Tests.Helpers.TestServerContext(x.GetRequiredService<TestContext>()));
        services.AddSingleton<CounterService>();
        services.AddSingleton<MultiHandlerCounter>();
        services.AddSingleton(capture);
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());

        var workerConfig = new OptionsWrapper<WarpServerConfiguration>(new WarpServerConfiguration
        {
            WorkerCount = 1,
            ServerId = ServerId,
            Queues = DefaultQueues,
            EnableHandlerLogging = true,
        });
        services.AddSingleton<IOptions<WarpServerConfiguration>>(workerConfig);
        services.AddSingleton<IOptions<WarpConfiguration>>(workerConfig);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var groupConfig = new WorkerGroupConfiguration
        {
            WorkerCount = 1,
            Queues = DefaultQueues,
        };

        var worker = new WarpWorkerService<TestContext>(
            WorkerId,
            scopeFactory,
            new NullLogger<WarpWorkerService<TestContext>>(),
            workerConfig,
            groupConfig,
            TimeProvider.System,
            Warp.Tests.Helpers.TestTasks.QueriesFromScope<TestContext>(scopeFactory),
            Warp.Tests.Helpers.TestTasks.NullTransport,
            Warp.Tests.Helpers.TestTasks.NullSignals);

        return (worker, capture);
    }

    [TimedFact]
    public async Task GetAndProcessJob_JobWithTraceId_ActivityHasMatchingTraceId()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var traceId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ActivityCaptureRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ActivityCaptureRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            TraceId = traceId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, capture) = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        capture.TraceId.ShouldBe(traceId.ToString("N"));
    }

    [TimedFact]
    public async Task GetAndProcessJob_JobWithTraceId_ActivityHasNonEmptySpanId()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ActivityCaptureRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ActivityCaptureRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            TraceId = Guid.NewGuid(),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, capture) = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        capture.SpanId.ShouldNotBeNullOrEmpty();
        capture.SpanId.ShouldNotBe("0000000000000000");
    }

    [TimedFact]
    public async Task GetAndProcessJob_JobWithoutTraceId_ActivityUsesJobIdAsTraceId()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ActivityCaptureRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ActivityCaptureRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            TraceId = null,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, capture) = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        capture.TraceId.ShouldBe(jobId.ToString("N"));
    }

    [TimedFact]
    public async Task GetAndProcessJob_JobWithParentSpanId_ActivityHasMatchingParentId()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var parentSpanId = ActivitySpanId.CreateRandom().ToHexString();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ActivityCaptureRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ActivityCaptureRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            TraceId = Guid.NewGuid(),
            ParentSpanId = parentSpanId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, capture) = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        capture.ParentSpanId.ShouldBe(parentSpanId);
    }

    [TimedFact]
    public async Task GetAndProcessJob_JobWithoutParentSpanId_ActivityHasEmptyParentId()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ActivityCaptureRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ActivityCaptureRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            TraceId = Guid.NewGuid(),
            ParentSpanId = null,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, capture) = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        capture.ParentSpanId.ShouldBe("0000000000000000");
    }

    [TimedFact]
    public async Task GetAndProcessJob_AfterExecution_ActivityIsCleared()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ActivityCaptureRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ActivityCaptureRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            TraceId = Guid.NewGuid(),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, _) = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        Activity.Current.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetAndProcessJob_HandlerThrows_ActivityIsCleared()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            TraceId = Guid.NewGuid(),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, _) = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        Activity.Current.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetAndProcessJob_TwoJobs_EachGetsUniqueSpanId()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var traceId = Guid.NewGuid();

        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ActivityCaptureRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ActivityCaptureRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            TraceId = traceId,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ActivityCaptureRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ActivityCaptureRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMilliseconds(1),
            Queue = "default",
            TraceId = traceId,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Use shared capture — second execution overwrites first
        var (worker, capture) = CreateWorker();

        // Act — process first job
        await worker.GetAndProcessJob(CancellationToken.None);
        var firstSpanId = capture.SpanId;

        // Process second job
        await worker.GetAndProcessJob(CancellationToken.None);
        var secondSpanId = capture.SpanId;

        // Assert — same trace, different spans
        firstSpanId.ShouldNotBeNullOrEmpty();
        secondSpanId.ShouldNotBeNullOrEmpty();
        firstSpanId.ShouldNotBe(secondSpanId);
    }

    [TimedFact]
    public async Task GetAndProcessJob_Completed_ActivityHasMessagingAttributes()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ActivityCaptureRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ActivityCaptureRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            TraceId = Guid.NewGuid(),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, capture) = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        capture.Tags["messaging.system"].ShouldBe("warp");
        capture.Tags["messaging.operation.name"].ShouldBe("process");
        capture.Tags["messaging.destination.name"].ShouldBe("default");
        capture.Tags["messaging.message.id"].ShouldBe(jobId.ToString());
        capture.Tags["warp.job.kind"].ShouldBe("Job");
        capture.Tags["warp.job.type"].ShouldNotBeNull();
    }
}
