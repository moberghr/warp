using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.RateLimit;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Features.RateLimit;

/// <summary>
/// Pins the pipeline-behaviour composition order when both [Mutex] and [RateLimit] are
/// applied to the same job. Concurrency is registered before RateLimit, so Mutex runs
/// outermost. When the mutex is already held, the handler is skipped *and* the rate-limit
/// bucket is NOT incremented — i.e. mutex rejections don't waste rate-limit tokens.
/// </summary>
[GenerateDatabaseTests]
public abstract class RateLimitCompositionTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected RateLimitCompositionTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly string[] DefaultQueues = ["default"];

    private static string SerializeCombinedMetadata(
        string mutexKey,
        ConcurrencyMode mutexMode,
        string rateLimitKey,
        int rateLimitCount,
        int windowSeconds,
        RateLimitMode rateLimitMode)
    {
        // Every metadata key is addon-prefixed (ConcurrencyLimit/ConcurrencyMode for the
        // mutex; RateLimitCount/RateLimitMode/RateLimitWindowSeconds for the rate limit) so
        // both metadata interfaces can coexist on a single job without overwriting each other.
        var dict = new Dictionary<string, object>
        {
            ["ConcurrencyKey"] = mutexKey,
            ["ConcurrencyLimit"] = 1,
            ["ConcurrencyMode"] = (int)mutexMode,
            ["RateLimitKey"] = rateLimitKey,
            ["RateLimitCount"] = rateLimitCount,
            ["RateLimitWindowSeconds"] = windowSeconds,
            ["RateLimitMode"] = (int)rateLimitMode,
        };

        return JsonSerializer.Serialize(dict);
    }

    [TimedFact]
    public async Task MutexHeld_RateLimitBucketNotIncremented()
    {
        // Pre-seed an empty rate-limit bucket so we can assert it's NOT touched when the
        // mutex check rejects ahead of the rate-limit check.
        var now = DateTime.UtcNow;
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "combo-rate",
            WindowStartUtc = now,
            CurrentCount = 0,
            TimestampsJson = null,
            UpdatedAt = now,
        });
        var jobId = Guid.NewGuid();
        seedCtx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = now,
            ScheduleTime = now.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeCombinedMetadata(
                "combo-mutex",
                ConcurrencyMode.Skip,
                "combo-rate",
                rateLimitCount: 10,
                windowSeconds: 60,
                RateLimitMode.Skip),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Pre-hold the mutex slot so ConcurrencyPipelineBehavior short-circuits to Deleted
        // before the rate-limit behaviour gets a chance to acquire its own lock.
        var semaphoreProvider = new FakeSemaphoreProvider();
        var heldMutex = semaphoreProvider.HoldSlot("warp:concurrency:combo-mutex");

        var worker = CreateWorker(semaphoreProvider);
        await worker.GetAndProcessJob(CancellationToken.None);

        await heldMutex.DisposeAsync();

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Deleted);

        // The actual assertion: rate-limit bucket count is still 0. If RateLimit had run
        // before Mutex (or alongside it), this would be 1.
        var bucket = await readCtx.Set<RateLimitBucket>()
            .Where(x => x.Name == "combo-rate")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        bucket.ShouldNotBeNull();
        bucket.CurrentCount.ShouldBe(0);
    }

    [TimedFact]
    public async Task MutexFree_RateLimitBucketIncrementedOnce()
    {
        // Mutex is free → handler runs through both behaviours → bucket increments to 1.
        // Pairs with the rejection test above to confirm: when mutex passes, rate-limit
        // does its work; when mutex fails, rate-limit is skipped.
        var seedCtx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        seedCtx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeCombinedMetadata(
                "combo-mutex",
                ConcurrencyMode.Skip,
                "combo-rate-free",
                rateLimitCount: 10,
                windowSeconds: 60,
                RateLimitMode.Skip),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);

        var bucket = await readCtx.Set<RateLimitBucket>()
            .Where(x => x.Name == "combo-rate-free")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        bucket.ShouldNotBeNull();
        bucket.CurrentCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task DifferentModesPerAttribute_AreObservedIndependently()
    {
        // Mutex uses ConcurrencyMode.Skip; RateLimit uses RateLimitMode.Wait. If the two
        // metadata fields share a dict key (which they did before the RateLimitMode rename),
        // one would silently overwrite the other and the observable behaviour would flip
        // depending on publish-behaviour ordering. This test pins them apart.
        //
        // Setup: mutex free, rate-limit bucket at-limit. Concurrency should pass; RateLimit
        // should throttle (Wait mode) — landing the job in Scheduled state. If RateLimit's
        // mode were overwritten by Concurrency's Skip, the job would be Deleted instead.
        var now = DateTime.UtcNow;
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "modes-disambig-rate",
            WindowStartUtc = now,
            CurrentCount = 1,
            TimestampsJson = null,
            UpdatedAt = now,
        });
        var jobId = Guid.NewGuid();
        seedCtx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = now,
            ScheduleTime = now.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeCombinedMetadata(
                "modes-disambig-mutex",
                ConcurrencyMode.Skip,
                "modes-disambig-rate",
                rateLimitCount: 1,
                windowSeconds: 60,
                RateLimitMode.Wait),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();

        // Scheduled (not Deleted) proves RateLimitMode.Wait was honoured, not overwritten
        // by ConcurrencyMode.Skip via a shared dict key.
        job.CurrentState.ShouldBe(State.Scheduled);
        job.ScheduleTime.ShouldBeGreaterThan(now);
        job.ExpireAt.ShouldBeNull();

        // Bucket count must be unchanged: the Wait rejection happens BEFORE the increment,
        // so a corrupted pipeline that incremented-then-rejected would leave count=2 here.
        var bucket = await readCtx.Set<RateLimitBucket>()
            .Where(x => x.Name == "modes-disambig-rate")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        bucket.ShouldNotBeNull();
        bucket.CurrentCount.ShouldBe(1);
    }

    private WarpWorkerService<TestContext> CreateWorker(FakeSemaphoreProvider? semaphoreProvider = null)
    {
        semaphoreProvider ??= new FakeSemaphoreProvider();
        var lockProvider = new FakeLockProvider();
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddSingleton<IWarpSemaphoreProvider>(semaphoreProvider);
        services.AddSingleton<IWarpLockProvider>(lockProvider);
        services.AddSingleton<IDatabaseExceptionClassifier>(Warp.Tests.Helpers.TestTasks.ClassifierFor(_fixture.CreateContext()));
        services.AddSingleton(TimeProvider.System);

        // Order matters: AddConcurrency must register *before* AddRateLimit so the mutex
        // check runs outermost in the pipeline. This is the same order WarpTestServer
        // uses for production-shaped tests.
        var builder = new Warp.Core.WarpBuilder<TestContext>(services);
        builder.AddConcurrency();
        builder.AddRateLimit();

        var workerConfig = new OptionsWrapper<WarpServerConfiguration>(new WarpServerConfiguration
        {
            WorkerCount = 1,
            ServerId = ServerId,
            Queues = DefaultQueues,
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
