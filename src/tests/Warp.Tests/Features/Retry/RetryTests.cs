using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Retry;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Features.Retry;

[GenerateDatabaseTests]
public abstract class RetryTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly string[] DefaultQueues = ["default"];

    protected RetryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static int GetRetriedTimes(Job job)
    {
        if (job.Metadata == null)
        {
            return 0;
        }

        var meta = MetadataSerializer.Deserialize(job.Metadata);
        if (meta.TryGetValue("RetriedTimes", out var value))
        {
            return Convert.ToInt32(value);
        }

        return 0;
    }

    private WarpWorkerService<TestContext> CreateWorker(int maxRetries = 3, int[]? delays = null, double jitterFactor = 0)
    {
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddSingleton<CounterService>();
        services.AddSingleton<MultiHandlerCounter>();
        services.AddScoped<Warp.Core.Handlers.JobContext>();
        services.AddScoped<Warp.Core.Handlers.IJobContext>(x => x.GetRequiredService<Warp.Core.Handlers.JobContext>());
        new Warp.Core.WarpBuilder<TestContext>(services).AddRetry(o =>
        {
            o.MaxRetries = maxRetries;
            o.Delays = delays ?? [];
            o.JitterFactor = jitterFactor;
        });

        var workerConfig = new OptionsWrapper<WarpWorkerConfiguration>(new WarpWorkerConfiguration
        {
            WorkerCount = 1,
            ServerId = ServerId,
            Queues = DefaultQueues,
        });
        services.AddSingleton<IOptions<WarpWorkerConfiguration>>(workerConfig);
        services.AddSingleton<IOptions<WarpConfiguration>>(workerConfig);
        services.TryAddSingleton(TimeProvider.System);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var groupConfig = new WorkerGroupConfiguration
        {
            WorkerCount = 1,
            Queues = DefaultQueues,
        };

        return new WarpWorkerService<TestContext>(
            WorkerId,
            scopeFactory,
            new NullLogger<WarpWorkerService<TestContext>>(),
            workerConfig,
            groupConfig,
            TimeProvider.System,
            Warp.Tests.Helpers.TestTasks.QueriesFromScope<TestContext>(scopeFactory),
            Warp.Tests.Helpers.TestTasks.NullTransport,
            Warp.Tests.Helpers.TestTasks.NullSignals);
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithMaxRetries3_RetriesThreeTimesThenFails()
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
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 3);

        // Act — process 4 times (1 initial + 3 retries)
        for (var i = 0; i < 4; i++)
        {
            await worker.GetAndProcessJob(CancellationToken.None);
        }

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);
        GetRetriedTimes(job).ShouldBe(3);
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithMaxRetries0_FailsImmediately()
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
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 0);

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);
        GetRetriedTimes(job).ShouldBe(0);
    }

    [TimedFact]
    public async Task GetAndProcessJob_RetryDoesNotIncrementFailedStat()
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
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 2);

        // Act — process once (should be requeued, not failed)
        await worker.GetAndProcessJob(CancellationToken.None);

        await Warp.Tests.Helpers.TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(CancellationToken.None);

        // Assert — stats:failed should NOT be incremented during retry
        var failedStat = await _fixture.CreateContext().Set<Statistic>()
            .Where(x => x.Key == "stats:failed")
            .Select(x => x.Value)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        failedStat.ShouldBe(0);

        // …but stats:requeued SHOULD be incremented (retry shares the same counter as Mutex Wait).
        var requeuedStat = await _fixture.CreateContext().Set<Statistic>()
            .Where(x => x.Key == "stats:requeued")
            .Select(x => x.Value)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        requeuedStat.ShouldBe(1);

        // Verify job was requeued (not failed)
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Enqueued);
        GetRetriedTimes(job).ShouldBe(1);
    }

    [TimedFact]
    public async Task GetAndProcessJob_RetryThenSucceed_CompletesNormally()
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
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 3);

        // Act — first attempt fails and gets requeued
        await worker.GetAndProcessJob(CancellationToken.None);

        // Change job type to succeed on next attempt
        var updateCtx = _fixture.CreateContext();
        await updateCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(p => p.Type, typeof(UnitRequest).AssemblyQualifiedName)
                    .SetProperty(p => p.Message, JsonSerializer.Serialize(new UnitRequest()))
                    .SetProperty(p => p.HandlerType, (string?)null),
                Xunit.TestContext.Current.CancellationToken);

        // Process again — should succeed
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Completed);
        GetRetriedTimes(job).ShouldBe(1);
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithRetryDelays_SetsScheduleTimeInFuture()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = now,
            ScheduleTime = now,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 1, delays: [3600]);

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert — retries with a future ScheduleTime land in Scheduled so the worker fetch
        // (State=Enqueued only) doesn't pick them up early.
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Scheduled);
        job.ScheduleTime.ShouldBeGreaterThan(now.AddSeconds(3500));
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithRetryDelays_LastDelayReusedWhenArrayShorter()
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
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 3, delays: [10, 20]);

        // Act — process 3 times (1 initial + 2 retries)
        for (var i = 0; i < 3; i++)
        {
            // Reset ScheduleTime and flip Scheduled → Enqueued so the worker picks it up again
            // (delayed retries land in Scheduled; the activation task isn't running in this unit test).
            var resetCtx = _fixture.CreateContext();
            await resetCtx.Set<Job>()
                .Where(x => x.Id == jobId)
                .Where(x => x.CurrentState == State.Enqueued || x.CurrentState == State.Scheduled)
                .ExecuteUpdateAsync(
                    x => x.SetProperty(p => p.ScheduleTime, DateTime.UtcNow)
                          .SetProperty(p => p.CurrentState, State.Enqueued),
                    Xunit.TestContext.Current.CancellationToken);

            await worker.GetAndProcessJob(CancellationToken.None);
        }

        // Assert — 3rd retry should use last delay (20s)
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        GetRetriedTimes(job).ShouldBe(3);
        job.ScheduleTime.ShouldBeGreaterThan(DateTime.UtcNow.AddSeconds(15));
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithEmptyRetryDelays_RetriesImmediately()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var originalScheduleTime = DateTime.UtcNow;
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = originalScheduleTime,
            ScheduleTime = originalScheduleTime,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 1, delays: []);

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert — ScheduleTime should still be in the past (no delay applied)
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Enqueued);
        job.ScheduleTime.ShouldBeLessThanOrEqualTo(TimeProvider.System.GetUtcNow().UtcDateTime);
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithRetryDelays_JobNotPickedUpBeforeDelay()
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
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 1, delays: [3600]);

        // Act — first call processes and requeues with 1h delay
        await worker.GetAndProcessJob(CancellationToken.None);

        // Second call should not find the job (ScheduleTime is in the future)
        var didProcess = await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        didProcess.ShouldBeFalse();
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithPerJobRetryDelays_OverridesGlobalConfig()
    {
        // Arrange — job has per-job $retryDelays in metadata
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var metadata = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["RetryDelays"] = new int[] { 7200 },
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = now,
            ScheduleTime = now,
            Queue = "default",

            Metadata = metadata,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Global config has 60s delay, but per-job has 7200s
        var worker = CreateWorker(maxRetries: 1, delays: [60]);

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert — should use per-job delay (7200s), not global (60s)
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ScheduleTime.ShouldBeGreaterThan(now.AddSeconds(7000));
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithMaxRetriesInMetadata_UsesMetadataValue()
    {
        // Arrange — job has per-job $maxRetries in metadata
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var metadata = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["MaxRetries"] = 2,
        });
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

            Metadata = metadata,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Global config has 0 retries, but per-job metadata has 2
        var worker = CreateWorker(maxRetries: 0);

        // Act — process 3 times (1 initial + 2 retries from metadata)
        for (var i = 0; i < 3; i++)
        {
            await worker.GetAndProcessJob(CancellationToken.None);
        }

        // Assert — should have retried 2 times (from metadata), then failed
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);
        GetRetriedTimes(job).ShouldBe(2);
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithRetryAttributeOnHandler_UsesAttributeMaxRetries()
    {
        // Arrange — handler has [Retry(5)], global has 0
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(RetryAttributeHandlerRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new RetryAttributeHandlerRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 0);

        // Act — process once, should be requeued (attribute says 5 retries)
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Enqueued);
        GetRetriedTimes(job).ShouldBe(1);
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithRetryAttributeOnJob_UsesAttributeMaxRetries()
    {
        // Arrange — job class has [Retry(4)], global has 0
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(RetryAttributeJobRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new RetryAttributeJobRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 0);

        // Act — process once, should be requeued (attribute says 4 retries)
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Enqueued);
        GetRetriedTimes(job).ShouldBe(1);
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithRetryAttributeOnBothHandlerAndJob_HandlerWins()
    {
        // Arrange — handler has [Retry(7)], job has [Retry(2)], global has 0
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(RetryAttributeBothRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new RetryAttributeBothRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 0);

        // Act — exhaust all retries from handler attribute (7)
        for (var i = 0; i < 8; i++)
        {
            var resetCtx = _fixture.CreateContext();
            await resetCtx.Set<Job>()
                .Where(x => x.Id == jobId)
                .Where(x => x.CurrentState == State.Enqueued)
                .ExecuteUpdateAsync(
                    x => x.SetProperty(p => p.ScheduleTime, DateTime.UtcNow),
                    Xunit.TestContext.Current.CancellationToken);

            await worker.GetAndProcessJob(CancellationToken.None);
        }

        // Assert — should have used handler's 7 retries (not job's 2)
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);
        GetRetriedTimes(job).ShouldBe(7);
    }

    [TimedFact]
    public async Task GetAndProcessJob_MetadataOverridesRetryAttribute()
    {
        // Arrange — handler has [Retry(5)], but metadata overrides to 1
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var metadata = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["MaxRetries"] = 1,
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(RetryAttributeHandlerRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new RetryAttributeHandlerRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            Metadata = metadata,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 0);

        // Act — process twice (1 initial + 1 retry from metadata)
        for (var i = 0; i < 2; i++)
        {
            await worker.GetAndProcessJob(CancellationToken.None);
        }

        // Assert — should have used metadata's 1 retry (not handler attribute's 5)
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);
        GetRetriedTimes(job).ShouldBe(1);
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithRetryAttributeDelays_UsesAttributeDelays()
    {
        // Arrange — handler has [Retry(3, Delays = [100, 200, 300])]
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(RetryAttributeWithDelaysRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new RetryAttributeWithDelaysRequest()),
            CreateTime = now,
            ScheduleTime = now,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Global has 10s delay, attribute has 100s
        var worker = CreateWorker(maxRetries: 0, delays: [10]);

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert — should use attribute's 100s delay (not global 10s); delayed retries land in Scheduled.
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Scheduled);
        job.ScheduleTime.ShouldBeGreaterThan(now.AddSeconds(90));
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithJitterFactorZero_ScheduleTimeIsExactDelay()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = now,
            ScheduleTime = now,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 1, delays: [100], jitterFactor: 0);

        // Act
        var before = DateTime.UtcNow;
        await worker.GetAndProcessJob(CancellationToken.None);
        var after = DateTime.UtcNow;

        // Assert — ScheduleTime ≈ now + 100s (no jitter)
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ScheduleTime.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(100).AddSeconds(-1));
        job.ScheduleTime.ShouldBeLessThanOrEqualTo(after.AddSeconds(100).AddSeconds(1));
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithJitterFactor_ScheduleTimeWithinJitteredWindowAndVaries()
    {
        // Arrange — run N iterations to verify both the [50s, 150s] window AND that jitter actually varies
        var worker = CreateWorker(maxRetries: 1, delays: [100], jitterFactor: 0.5);
        var scheduleTimes = new List<DateTime>();

        for (var i = 0; i < 10; i++)
        {
            var ctx = _fixture.CreateContext();
            var jobId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            ctx.Set<Job>().Add(new Job
            {
                Id = jobId,
                Kind = JobKind.Job,
                CurrentState = State.Enqueued,
                Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
                Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
                CreateTime = now,
                ScheduleTime = now,
                Queue = "default",
            });
            await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

            var before = DateTime.UtcNow;

            // Act
            await worker.GetAndProcessJob(CancellationToken.None);

            var after = DateTime.UtcNow;

            // Assert — each ScheduleTime ∈ [now + 50s, now + 150s]
            var readCtx = _fixture.CreateContext();
            var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
            job.ScheduleTime.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(50).AddSeconds(-1));
            job.ScheduleTime.ShouldBeLessThanOrEqualTo(after.AddSeconds(150).AddSeconds(1));
            scheduleTimes.Add(job.ScheduleTime);
        }

        // Assert — jitter actually produces variation across iterations (≥2 distinct values)
        scheduleTimes.Distinct().Count().ShouldBeGreaterThanOrEqualTo(2);
    }

    [TimedFact]
    public async Task GetAndProcessJob_RetryRequeued_JobLogRecordsScheduledTime()
    {
        // Regression for PR #124 review F4: jitter may randomize the retry delay by ±100%
        // of the configured value, so the retry log entry must record the resulting
        // ScheduleTime. After the Requeued-split (2026-05), a retry emits two logs: a
        // "Failed" with the exception and a "Scheduled"/"Enqueued" with "Retry scheduled
        // for <timestamp>". Assert against the scheduled log specifically.
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = now,
            ScheduleTime = now,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 3, delays: [30]);

        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Scheduled" || x.EventType == "Enqueued")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        log.ShouldNotBeNull();

        // Assert the log contains the scheduled-for marker and a parseable timestamp close
        // to the persisted ScheduleTime. Exact string equality is brittle across DB backends
        // (Postgres truncates to microseconds while .NET DateTime has 100-ns precision).
        log.Message.ShouldContain("Retry scheduled for ");
        const string marker = "Retry scheduled for ";
        var startIndex = log.Message.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var logged = DateTime.Parse(log.Message[startIndex..], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        (logged - job.ScheduleTime).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithJitterFactorAboveOne_ClampedToOne()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = now,
            ScheduleTime = now,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 1, delays: [100], jitterFactor: 5.0);

        // Act
        var before = DateTime.UtcNow;
        await worker.GetAndProcessJob(CancellationToken.None);
        var after = DateTime.UtcNow;

        // Assert — clamped to 1.0, so ScheduleTime ∈ [now, now + 200s]. Lower bound is
        // `before` (not `before.AddSeconds(-1)`): jitter must never produce a delay so
        // negative that the schedule lands in the past, otherwise the job runs immediately.
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ScheduleTime.ShouldBeGreaterThanOrEqualTo(before);
        job.ScheduleTime.ShouldBeLessThanOrEqualTo(after.AddSeconds(200).AddSeconds(1));
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithJitterFactorBelowZero_ClampedToZero()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = now,
            ScheduleTime = now,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 1, delays: [100], jitterFactor: -1.0);

        // Act
        var before = DateTime.UtcNow;
        await worker.GetAndProcessJob(CancellationToken.None);
        var after = DateTime.UtcNow;

        // Assert — clamped to 0, so ScheduleTime ≈ now + 100s (no jitter)
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ScheduleTime.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(100).AddSeconds(-1));
        job.ScheduleTime.ShouldBeLessThanOrEqualTo(after.AddSeconds(100).AddSeconds(1));
    }

    [TimedFact]
    public async Task GetAndProcessJob_WithEmptyDelaysAndJitter_StillHasNoDelay()
    {
        // Arrange — empty delays with jitter factor should short-circuit (no scheduleTime)
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var originalScheduleTime = DateTime.UtcNow;
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = originalScheduleTime,
            ScheduleTime = originalScheduleTime,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(maxRetries: 1, delays: [], jitterFactor: 0.5);

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert — ScheduleTime should remain in the past (empty-delays short-circuit preserved)
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Enqueued);
        job.ScheduleTime.ShouldBeLessThanOrEqualTo(TimeProvider.System.GetUtcNow().UtcDateTime);
    }
}
