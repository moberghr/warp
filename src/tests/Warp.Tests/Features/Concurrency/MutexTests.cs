using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Helper;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Features.Concurrency;

[GenerateDatabaseTests]
public abstract class MutexTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected MutexTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly string[] DefaultQueues = ["default"];

    private static string SerializeMutexMetadata(string key, ConcurrencyMode? mode = null)
    {
        var dict = new Dictionary<string, object> { ["ConcurrencyKey"] = key };
        if (mode != null)
        {
            dict["ConcurrencyMode"] = (int)mode.Value;
        }

        return JsonSerializer.Serialize(dict);
    }

    [TimedFact]
    public Task MutexAttribute_EmptyKey_ThrowsAtConstruction()
    {
        Should.Throw<ArgumentException>(() => new MutexAttribute(string.Empty));
        Should.Throw<ArgumentNullException>(() => new MutexAttribute(null!));

        return Task.CompletedTask;
    }

    [TimedFact]
    public async Task MutexHeld_SecondJobCancelled()
    {
        // Arrange: two jobs with same concurrency key, first already processing
        var ctx = _fixture.CreateContext();
        var job1Id = Guid.NewGuid();
        var job2Id = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = job1Id,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeMutexMetadata("payment:123"),
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = job2Id,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeMutexMetadata("payment:123"),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Pre-hold the lock (simulating job1 is being processed by another worker)
        var lockProvider = new FakeSemaphoreProvider();
        var heldHandle = lockProvider.HoldSlot("warp:concurrency:payment:123");

        // Act: worker processes job2 — should fail to acquire lock
        var worker = CreateWorker(lockProvider);
        await worker.GetAndProcessJob(CancellationToken.None);

        // Cleanup
        await heldHandle.DisposeAsync();

        // Assert: job2 should be cancelled (Deleted)
        var readCtx = _fixture.CreateContext();
        var job2 = await readCtx.Set<Job>().FindAsync([job2Id], CancellationToken.None);
        job2.ShouldNotBeNull();
        job2.CurrentState.ShouldBe(State.Deleted);

        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == job2Id)
            .Where(x => x.EventType == "Deleted")
            .FirstOrDefaultAsync(CancellationToken.None);
        log.ShouldNotBeNull();
        log.Message.ShouldContain("Cancelled");
        log.Message.ShouldContain("payment:123");
        log.Message.ShouldContain("1 slots");
    }

    [TimedFact]
    public async Task DifferentMutexKeys_BothProcess()
    {
        // Arrange: two jobs with different concurrency keys
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeMutexMetadata("payment:123"),
        });
        var job2Id = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = job2Id,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeMutexMetadata("payment:456"),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert: job2 should be processing (not cancelled)
        var readCtx = _fixture.CreateContext();
        var job2 = await readCtx.Set<Job>().FindAsync([job2Id], Xunit.TestContext.Current.CancellationToken);
        job2.ShouldNotBeNull();
        job2.CurrentState.ShouldNotBe(State.Deleted);
    }

    [TimedFact]
    public async Task NoConcurrencyKey_NoMutexCheck()
    {
        // Arrange: job without concurrency key
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert: job should complete normally (no mutex cancellation)
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task MutexFree_JobProcessesNormally()
    {
        // Arrange: job with concurrency key but no other job holds it
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeMutexMetadata("payment:123"),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert: should complete
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task MutexHeld_SetsExpireAtAndDeletedCounter()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var job1Id = Guid.NewGuid();
        var job2Id = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = job1Id,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeMutexMetadata("payment:789"),
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = job2Id,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeMutexMetadata("payment:789"),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var lockProvider = new FakeSemaphoreProvider();
        var heldHandle = lockProvider.HoldSlot("warp:concurrency:payment:789");

        // Act
        var worker = CreateWorker(lockProvider);
        await worker.GetAndProcessJob(CancellationToken.None);
        await heldHandle.DisposeAsync();

        // Assert: ExpireAt should be set (not null)
        var readCtx = _fixture.CreateContext();
        var job2 = await readCtx.Set<Job>().FindAsync([job2Id], CancellationToken.None);
        job2.ShouldNotBeNull();
        job2.ExpireAt.ShouldNotBeNull();

        // Assert: stats:deleted counter should exist
        var counter = await readCtx.Set<Counter>()
            .Where(x => x.Key == "stats:deleted")
            .FirstOrDefaultAsync(CancellationToken.None);
        counter.ShouldNotBeNull();
        counter.Value.ShouldBe(1);
    }

    [TimedFact]
    public async Task MutexHeld_WaitMode_SecondJobRequeued()
    {
        // Arrange: two jobs with same concurrency key in Wait mode, first already processing
        var ctx = _fixture.CreateContext();
        var job1Id = Guid.NewGuid();
        var job2Id = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = job1Id,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeMutexMetadata("payment:wait", ConcurrencyMode.Wait),
        });
        ctx.Set<Job>().Add(new Job
        {
            Id = job2Id,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeMutexMetadata("payment:wait", ConcurrencyMode.Wait),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var lockProvider = new FakeSemaphoreProvider();
        var heldHandle = lockProvider.HoldSlot("warp:concurrency:payment:wait");

        // Act
        var worker = CreateWorker(lockProvider);
        await worker.GetAndProcessJob(CancellationToken.None);
        await heldHandle.DisposeAsync();

        // Assert: job2 should be back in Enqueued, ExpireAt unset, Requeued log entry written
        var readCtx = _fixture.CreateContext();
        var job2 = await readCtx.Set<Job>().FindAsync([job2Id], CancellationToken.None);
        job2.ShouldNotBeNull();
        job2.CurrentState.ShouldBe(State.Enqueued);
        job2.ExpireAt.ShouldBeNull();

        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == job2Id)
            .Where(x => x.EventType == "Enqueued")
            .Where(x => x.Message.Contains("payment:wait"))
            .FirstOrDefaultAsync(CancellationToken.None);
        log.ShouldNotBeNull();
        log.Message.ShouldContain("Requeued");
        log.Message.ShouldContain("payment:wait");
        log.Message.ShouldContain("1 slots");

        // Counter row written for the requeue — surfaces in the dashboard's Requeued metric.
        var requeuedCounter = await readCtx.Set<Counter>()
            .Where(x => x.Key == "stats:requeued")
            .SumAsync(x => x.Value, CancellationToken.None);
        requeuedCounter.ShouldBe(1);

        var hourlyCounter = await readCtx.Set<Counter>()
            .Where(x => x.Key.StartsWith("stats:requeued:"))
            .SumAsync(x => x.Value, CancellationToken.None);
        hourlyCounter.ShouldBe(1);
    }

    [TimedFact]
    public async Task MutexFree_WaitMode_JobProcessesNormally()
    {
        // Arrange: job with Wait mode, no other holder
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeMutexMetadata("payment:free-wait", ConcurrencyMode.Wait),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert: job should complete normally
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task MutexAttribute_SetsKeyAtPublishTime()
    {
        // Arrange: MutexAttributeRequest has [Mutex("static-key")] on the job class
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddSingleton<IWarpSemaphoreProvider>(new FakeSemaphoreProvider());
        new Warp.Core.WarpBuilder<TestContext>(services).AddConcurrency();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<WarpConfiguration>>(new OptionsWrapper<WarpConfiguration>(new WarpConfiguration()));

        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        // Act: enqueue a job type that has [Mutex("static-key")]
        var jobId = await publisher.Enqueue(new MutexAttributeRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert: metadata should contain the mutex key from the attribute
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(CancellationToken.None);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata!);
        metadata.ShouldNotBeNull();
        metadata.ShouldContainKey("ConcurrencyKey");
        metadata["ConcurrencyKey"].ToString().ShouldBe("static-key");
    }

    [TimedFact]
    public async Task WithMutex_SetsKeyInMetadata()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddSingleton<IWarpSemaphoreProvider>(new FakeSemaphoreProvider());
        new Warp.Core.WarpBuilder<TestContext>(services).AddConcurrency();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<WarpConfiguration>>(new OptionsWrapper<WarpConfiguration>(new WarpConfiguration()));

        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        // Act: enqueue with WithMutex extension
        var jobId = await publisher.Enqueue(new UnitRequest(), new JobParameters().WithMutex("dynamic-key"));
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert: metadata should contain the mutex key
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(CancellationToken.None);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata!);
        metadata.ShouldNotBeNull();
        metadata.ShouldContainKey("ConcurrencyKey");
        metadata["ConcurrencyKey"].ToString().ShouldBe("dynamic-key");
    }

    [TimedFact]
    public async Task MutexAttribute_PopulatesLimitOneInMetadata()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddSingleton<IWarpSemaphoreProvider>(new FakeSemaphoreProvider());
        new Warp.Core.WarpBuilder<TestContext>(services).AddConcurrency();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<WarpConfiguration>>(new OptionsWrapper<WarpConfiguration>(new WarpConfiguration()));

        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        // Act
        var jobId = await publisher.Enqueue(new MutexAttributeRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert: metadata should carry Limit = 1
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(CancellationToken.None);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata!);
        metadata.ShouldNotBeNull();
        metadata.ShouldContainKey("ConcurrencyLimit");
        ((JsonElement)metadata["ConcurrencyLimit"]).GetInt32().ShouldBe(1);
    }

    [TimedFact]
    public async Task WithMutex_PopulatesLimitOneInMetadata()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddSingleton<IWarpSemaphoreProvider>(new FakeSemaphoreProvider());
        new Warp.Core.WarpBuilder<TestContext>(services).AddConcurrency();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<WarpConfiguration>>(new OptionsWrapper<WarpConfiguration>(new WarpConfiguration()));

        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        // Act
        var jobId = await publisher.Enqueue(new UnitRequest(), new JobParameters().WithMutex("limit-one-key"));
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert: metadata should carry Limit = 1
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(CancellationToken.None);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata!);
        metadata.ShouldNotBeNull();
        metadata.ShouldContainKey("ConcurrencyLimit");
        ((JsonElement)metadata["ConcurrencyLimit"]).GetInt32().ShouldBe(1);
    }

    [TimedFact]
    public async Task MutexAttribute_WaitMode_PropagatesToMetadata()
    {
        // Arrange: MutexWaitAttributeRequest has [Mutex("static-wait-key", Mode = ConcurrencyMode.Wait)]
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddSingleton<IWarpSemaphoreProvider>(new FakeSemaphoreProvider());
        new Warp.Core.WarpBuilder<TestContext>(services).AddConcurrency();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<WarpConfiguration>>(new OptionsWrapper<WarpConfiguration>(new WarpConfiguration()));

        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        // Act
        var jobId = await publisher.Enqueue(new MutexWaitAttributeRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert: metadata should carry both ConcurrencyKey and Mode = Wait
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(CancellationToken.None);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata!);
        metadata.ShouldNotBeNull();
        metadata.ShouldContainKey("ConcurrencyKey");
        metadata["ConcurrencyKey"].ToString().ShouldBe("static-wait-key");
        metadata.ShouldContainKey("ConcurrencyMode");
        ((JsonElement)metadata["ConcurrencyMode"]).GetInt32().ShouldBe((int)ConcurrencyMode.Wait);
    }

    private WarpWorkerService<TestContext> CreateWorker(FakeSemaphoreProvider? lockProvider = null)
    {
        lockProvider ??= new FakeSemaphoreProvider();
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddSingleton<IWarpSemaphoreProvider>(lockProvider);
        services.AddSingleton(TimeProvider.System);
        new Warp.Core.WarpBuilder<TestContext>(services).AddConcurrency();

        var workerConfig = new OptionsWrapper<WarpWorkerConfiguration>(new WarpWorkerConfiguration
        {
            WorkerCount = 1,
            ServerId = ServerId,
            Queues = DefaultQueues,
        });
        services.AddSingleton<IOptions<WarpWorkerConfiguration>>(workerConfig);
        services.AddSingleton<IOptions<WarpConfiguration>>(workerConfig);

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
}
