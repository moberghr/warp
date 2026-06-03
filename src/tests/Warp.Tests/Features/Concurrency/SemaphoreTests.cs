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
public abstract class SemaphoreTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected SemaphoreTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public Task SemaphoreAttribute_LimitZero_ThrowsAtConstruction()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new SemaphoreAttribute("k", 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new SemaphoreAttribute("k", -1));

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task SemaphoreAttribute_EmptyKey_ThrowsAtConstruction()
    {
        Should.Throw<ArgumentException>(() => new SemaphoreAttribute(string.Empty, 5));
        Should.Throw<ArgumentNullException>(() => new SemaphoreAttribute(null!, 5));

        return Task.CompletedTask;
    }

    [TimedFact]
    public async Task SemaphoreAttribute_PropagatesKeyLimitAndModeWaitDefault()
    {
        // Arrange: SemaphoreAttributeRequest has [Semaphore("static-semaphore-key", 5)]
        var services = BuildServices();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        // Act
        var jobId = await publisher.Enqueue(new SemaphoreAttributeRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert: metadata should carry ConcurrencyKey, Limit, and default Mode = Wait
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(CancellationToken.None);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata!);
        metadata.ShouldNotBeNull();
        metadata.ShouldContainKey("ConcurrencyKey");
        metadata["ConcurrencyKey"].ToString().ShouldBe("static-semaphore-key");
        metadata.ShouldContainKey("ConcurrencyLimit");
        ((JsonElement)metadata["ConcurrencyLimit"]).GetInt32().ShouldBe(5);
        metadata.ShouldContainKey("ConcurrencyMode");
        ((JsonElement)metadata["ConcurrencyMode"]).GetInt32().ShouldBe((int)ConcurrencyMode.Wait);
    }

    [TimedFact]
    public async Task SemaphoreAttribute_ExplicitMode_PropagatesKeyLimitAndExplicitMode()
    {
        // Arrange: SemaphoreSkipAttributeRequest has [Semaphore("...", 5, Mode = Skip)]
        var services = BuildServices();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        // Act
        var jobId = await publisher.Enqueue(new SemaphoreSkipAttributeRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert: metadata should reflect explicit Mode = Skip
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(CancellationToken.None);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata!);
        metadata.ShouldNotBeNull();
        metadata.ShouldContainKey("ConcurrencyKey");
        metadata["ConcurrencyKey"].ToString().ShouldBe("static-semaphore-skip-key");
        metadata.ShouldContainKey("ConcurrencyLimit");
        ((JsonElement)metadata["ConcurrencyLimit"]).GetInt32().ShouldBe(5);
        metadata.ShouldContainKey("ConcurrencyMode");
        ((JsonElement)metadata["ConcurrencyMode"]).GetInt32().ShouldBe((int)ConcurrencyMode.Skip);
    }

    [TimedFact]
    public async Task WithSemaphore_SetsKeyLimitAndModeInMetadata()
    {
        // Arrange
        var services = BuildServices();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        // Act: enqueue with WithSemaphore extension (default Mode = Wait)
        var jobId = await publisher.Enqueue(new UnitRequest(), new JobParameters().WithSemaphore("dynamic-semaphore-key", 7));
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(CancellationToken.None);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata!);
        metadata.ShouldNotBeNull();
        metadata.ShouldContainKey("ConcurrencyKey");
        metadata["ConcurrencyKey"].ToString().ShouldBe("dynamic-semaphore-key");
        metadata.ShouldContainKey("ConcurrencyLimit");
        ((JsonElement)metadata["ConcurrencyLimit"]).GetInt32().ShouldBe(7);
        metadata.ShouldContainKey("ConcurrencyMode");
        ((JsonElement)metadata["ConcurrencyMode"]).GetInt32().ShouldBe((int)ConcurrencyMode.Wait);
    }

    [TimedFact]
    public async Task WithSemaphore_ExplicitMode_SetsKeyLimitAndModeInMetadata()
    {
        // Arrange
        var services = BuildServices();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        // Act: enqueue with WithSemaphore + explicit Mode = Skip
        var jobId = await publisher.Enqueue(new UnitRequest(), new JobParameters().WithSemaphore("dynamic-semaphore-skip", 3, ConcurrencyMode.Skip));
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(CancellationToken.None);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata!);
        metadata.ShouldNotBeNull();
        metadata.ShouldContainKey("ConcurrencyKey");
        metadata["ConcurrencyKey"].ToString().ShouldBe("dynamic-semaphore-skip");
        metadata.ShouldContainKey("ConcurrencyLimit");
        ((JsonElement)metadata["ConcurrencyLimit"]).GetInt32().ShouldBe(3);
        metadata.ShouldContainKey("ConcurrencyMode");
        ((JsonElement)metadata["ConcurrencyMode"]).GetInt32().ShouldBe((int)ConcurrencyMode.Skip);
    }

    [TimedFact]
    public async Task Limit5_SixthJobRequeues_WaitMode()
    {
        // Arrange: 5 slots are pre-saturated for the key, sixth job is enqueued in Wait mode.
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
            Metadata = SerializeSemaphoreMetadata("limit-5-wait", 5, ConcurrencyMode.Wait),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var lockProvider = new FakeSemaphoreProvider();
        var heldHandle = lockProvider.HoldSlot("warp:concurrency:limit-5-wait", 5);

        // Act
        var worker = CreateWorker(lockProvider);
        await worker.GetAndProcessJob(CancellationToken.None);
        await heldHandle.DisposeAsync();

        // Assert: job should be requeued, not deleted
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], CancellationToken.None);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Enqueued);
        job.ExpireAt.ShouldBeNull();

        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Enqueued")
            .Where(x => x.Message.Contains("limit-5-wait"))
            .FirstOrDefaultAsync(CancellationToken.None);
        log.ShouldNotBeNull();
        log.Message.ShouldContain("Requeued");
        log.Message.ShouldContain("limit-5-wait");
        log.Message.ShouldContain("5 slots");
    }

    [TimedFact]
    public async Task Limit5_SixthJobDeletes_SkipMode()
    {
        // Arrange: 5 slots pre-saturated, sixth job in Skip mode.
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
            Metadata = SerializeSemaphoreMetadata("limit-5-skip", 5, ConcurrencyMode.Skip),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var lockProvider = new FakeSemaphoreProvider();
        var heldHandle = lockProvider.HoldSlot("warp:concurrency:limit-5-skip", 5);

        // Act
        var worker = CreateWorker(lockProvider);
        await worker.GetAndProcessJob(CancellationToken.None);
        await heldHandle.DisposeAsync();

        // Assert: job deleted with "5 slots" in the log message
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], CancellationToken.None);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Deleted);

        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Deleted")
            .FirstOrDefaultAsync(CancellationToken.None);
        log.ShouldNotBeNull();
        log.Message.ShouldContain("Cancelled");
        log.Message.ShouldContain("limit-5-skip");
        log.Message.ShouldContain("5 slots");
    }

    [TimedFact]
    public async Task SemaphoreLimit5_SlotPartiallyHeld_AcquiresFreeSlot()
    {
        // Arrange: 1 slot of 5 is held — a Limit=5 job should still acquire a free slot.
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
            Metadata = SerializeSemaphoreMetadata("limit-5-partial", 5, ConcurrencyMode.Wait),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var lockProvider = new FakeSemaphoreProvider();
        var heldHandle = lockProvider.HoldSlot("warp:concurrency:limit-5-partial", 1);

        // Act
        var worker = CreateWorker(lockProvider);
        await worker.GetAndProcessJob(CancellationToken.None);
        await heldHandle.DisposeAsync();

        // Assert: job completes — there were 4 free slots out of 5
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], CancellationToken.None);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task ClassWithBothMutexAndSemaphore_MutexAttributeWins_PopulatesLimitOne()
    {
        // Arrange: MutexAndSemaphoreRequest has BOTH [Mutex] and [Semaphore] on the same class
        // — ConcurrencyPublishBehavior matches [Mutex] first and skips [Semaphore].
        var services = BuildServices();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        // Act
        var jobId = await publisher.Enqueue(new MutexAndSemaphoreRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Assert: metadata reflects the [Mutex] attribute (Limit = 1, Mode = Skip default)
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(CancellationToken.None);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata!);
        metadata.ShouldNotBeNull();
        metadata.ShouldContainKey("ConcurrencyKey");
        metadata["ConcurrencyKey"].ToString().ShouldBe("dual-attribute-key");
        metadata.ShouldContainKey("ConcurrencyLimit");
        ((JsonElement)metadata["ConcurrencyLimit"]).GetInt32().ShouldBe(1);
        metadata.ShouldContainKey("ConcurrencyMode");
        ((JsonElement)metadata["ConcurrencyMode"]).GetInt32().ShouldBe((int)ConcurrencyMode.Skip);
    }

    [TimedFact]
    public async Task AdminLimitOverridesAttributeLimit()
    {
        // Arrange: admin row says limit=10, metadata says Limit=5. Pre-saturate 5 of 10 slots.
        // Pipeline must call TryAcquire with maxCount=10 (admin wins) and find slot 5 free.
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<ConcurrencyLimit>().Add(new ConcurrencyLimit
        {
            Name = "admin-override-key",
            Limit = 10,
            UpdatedAt = DateTime.UtcNow,
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var jobId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
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
            Metadata = SerializeSemaphoreMetadata("admin-override-key", 5, ConcurrencyMode.Wait),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var lockProvider = new FakeSemaphoreProvider();
        var heldHandle = lockProvider.HoldSlot("warp:concurrency:admin-override-key", 5);

        // Act
        var worker = CreateWorker(lockProvider);
        await worker.GetAndProcessJob(CancellationToken.None);
        await heldHandle.DisposeAsync();

        // Assert: 5 slots free under admin limit 10 → job acquires and completes.
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], CancellationToken.None);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task AdminLimitOverridesAttributeLimit_RestrictsBelowAttributeLimit()
    {
        // Arrange: admin row says limit=2, metadata says Limit=5. Pre-saturate 2 slots.
        // Admin limit 2 wins → all 2 slots held → Wait mode requeues.
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<ConcurrencyLimit>().Add(new ConcurrencyLimit
        {
            Name = "admin-restricts-key",
            Limit = 2,
            UpdatedAt = DateTime.UtcNow,
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var jobId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
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
            Metadata = SerializeSemaphoreMetadata("admin-restricts-key", 5, ConcurrencyMode.Wait),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var lockProvider = new FakeSemaphoreProvider();
        var heldHandle = lockProvider.HoldSlot("warp:concurrency:admin-restricts-key", 2);

        // Act
        var worker = CreateWorker(lockProvider);
        await worker.GetAndProcessJob(CancellationToken.None);
        await heldHandle.DisposeAsync();

        // Assert: admin limit 2 with 2 held → no slot available → Wait requeues.
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], CancellationToken.None);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Enqueued);

        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Enqueued")
            .Where(x => x.Message.Contains("admin-restricts-key"))
            .FirstOrDefaultAsync(CancellationToken.None);
        log.ShouldNotBeNull();
        log.Message.ShouldContain("Requeued");
        log.Message.ShouldContain("admin-restricts-key");

        // Effective limit reported in the log message must be the admin value (2), not 5.
        log.Message.ShouldContain("2 slots");
    }

    [TimedFact]
    public async Task AdminLimitChangeTakesEffectOnNextPickup()
    {
        // The resolver caches lookups per-scope (one job execution scope). A new scope must
        // see admin updates committed since the previous scope started. Two-pickup test:
        //   1. Pickup A: no admin row, [Mutex]-shaped meta (Limit=1), 1 slot held → Deleted.
        //   2. Add admin row limit=5 between pickups.
        //   3. Pickup B: same key, same metadata, same 1 slot held → admin limit=5 wins,
        //      4 free slots → Completed.
        var jobAId = Guid.NewGuid();
        var jobBId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobAId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-2),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeSemaphoreMetadata("admin-change-key", 1, ConcurrencyMode.Skip),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var lockProvider = new FakeSemaphoreProvider();
        var heldHandle = lockProvider.HoldSlot("warp:concurrency:admin-change-key", 1);

        var worker = CreateWorker(lockProvider);

        // Pickup A: no admin row, Limit=1 → full → Deleted (Skip mode).
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtxA = _fixture.CreateContext();
        var jobA = await readCtxA.Set<Job>().FindAsync([jobAId], CancellationToken.None);
        jobA.ShouldNotBeNull();
        jobA.CurrentState.ShouldBe(State.Deleted);

        // Add admin row mid-flight.
        var adminCtx = _fixture.CreateContext();
        adminCtx.Set<ConcurrencyLimit>().Add(new ConcurrencyLimit
        {
            Name = "admin-change-key",
            Limit = 5,
            UpdatedAt = DateTime.UtcNow,
        });
        await adminCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Enqueue a second job with the same key + same Limit=1 metadata.
        var ctxB = _fixture.CreateContext();
        ctxB.Set<Job>().Add(new Job
        {
            Id = jobBId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeSemaphoreMetadata("admin-change-key", 1, ConcurrencyMode.Skip),
        });
        await ctxB.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Pickup B: fresh scope must observe admin limit=5 → 4 free slots → completes.
        await worker.GetAndProcessJob(CancellationToken.None);
        await heldHandle.DisposeAsync();

        var readCtxB = _fixture.CreateContext();
        var jobB = await readCtxB.Set<Job>().FindAsync([jobBId], CancellationToken.None);
        jobB.ShouldNotBeNull();
        jobB.CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task AdminLimitAppliesToBothMutexAndSemaphoreKeys()
    {
        // The pipeline resolves admin overrides by ConcurrencyKey, regardless of whether the
        // metadata came from [Mutex] or [Semaphore]. So a [Mutex("k")] job under admin limit=5
        // becomes effectively a 5-slot semaphore at the storage level — documented behavior.
        // Arrange: admin row says limit=5; metadata is Mutex-shaped (Limit=1, Mode=Skip).
        // Pre-saturate 1 slot. Under admin limit 5 there are 4 free slots → job acquires.
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<ConcurrencyLimit>().Add(new ConcurrencyLimit
        {
            Name = "admin-mutex-key",
            Limit = 5,
            UpdatedAt = DateTime.UtcNow,
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var jobId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
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
            Metadata = SerializeSemaphoreMetadata("admin-mutex-key", 1, ConcurrencyMode.Skip),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var lockProvider = new FakeSemaphoreProvider();
        var heldHandle = lockProvider.HoldSlot("warp:concurrency:admin-mutex-key", 1);

        // Act
        var worker = CreateWorker(lockProvider);
        await worker.GetAndProcessJob(CancellationToken.None);
        await heldHandle.DisposeAsync();

        // Assert: the Mutex-shaped meta would have produced a "1 slot, full" outcome under
        // its own limit, but admin limit 5 widens the effective limit → job completes.
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], CancellationToken.None);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task MissingMetaLimitDefaultsToOne()
    {
        // Arrange: no admin row; metadata is Mutex-shaped (Limit=1). Pre-saturate the single
        // slot. Effective limit must be 1 → Skip mode deletes the job.
        var jobId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
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
            Metadata = SerializeSemaphoreMetadata("default-one-key", 1, ConcurrencyMode.Skip),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var lockProvider = new FakeSemaphoreProvider();
        var heldHandle = lockProvider.HoldSlot("warp:concurrency:default-one-key", 1);

        // Act
        var worker = CreateWorker(lockProvider);
        await worker.GetAndProcessJob(CancellationToken.None);
        await heldHandle.DisposeAsync();

        // Assert: deleted with "1 slots" in the log message — proves no admin row + Limit=1
        // produced effective limit = 1.
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], CancellationToken.None);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Deleted);

        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Deleted")
            .FirstOrDefaultAsync(CancellationToken.None);
        log.ShouldNotBeNull();
        log.Message.ShouldContain("1 slots");
    }

    [TimedFact]
    public async Task MetadataWithoutLimitField_DefaultsToOne()
    {
        // Arrange: metadata has ConcurrencyKey + Mode but no Limit field at all (e.g. legacy
        // serialization, manual insert). The fallback chain reads (admin) ?? (meta.Limit) ?? 1.
        // With no admin row and meta.Limit deserialized as null, effective limit must be 1.
        var jobId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
        var metaWithoutLimit = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ConcurrencyKey"] = "no-limit-field-key",
            ["ConcurrencyMode"] = (int)ConcurrencyMode.Skip,
        });
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
            Metadata = metaWithoutLimit,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var lockProvider = new FakeSemaphoreProvider();
        var heldHandle = lockProvider.HoldSlot("warp:concurrency:no-limit-field-key", 1);

        // Act
        var worker = CreateWorker(lockProvider);
        await worker.GetAndProcessJob(CancellationToken.None);
        await heldHandle.DisposeAsync();

        // Assert: trailing `?? 1` reached → "1 slots" in log message.
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], CancellationToken.None);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Deleted);

        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Deleted")
            .FirstOrDefaultAsync(CancellationToken.None);
        log.ShouldNotBeNull();
        log.Message.ShouldContain("1 slots");
    }

    [TimedFact]
    public async Task HandlerThrows_SlotReleasesImmediately_SiblingCanAcquire()
    {
        // Characterization test for the explicit non-feature: slots are bound to handler execution,
        // not to the job's lifecycle. A failed handler releases its slot immediately; a sibling job
        // (or the failed job's eventual retry) can acquire that slot on the very next pickup.
        // Hangfire's "strict mode" deliberately holds slots through retry — we don't, because
        // holding a slot through a retry's backoff window leaves the resource idle, reducing
        // effective throughput below the cap. This test locks in our chosen semantic so a future
        // refactor that accidentally introduced retain-through-retry would be caught.
        var lockProvider = new FakeSemaphoreProvider();

        // Job 1: throws inside the handler. Configure with a Mutex-shape (limit=1) so the slot
        // contention is unambiguous — if the slot leaks, Job 2 below cannot acquire.
        var job1Id = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = job1Id,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(ThrowingConcurrencyRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeSemaphoreMetadata("release-test-key", 1, ConcurrencyMode.Skip),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker(lockProvider);

        // Act 1: process the throwing job. Slot is acquired, handler throws, finally releases it.
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert intermediate: the failed job is recorded as Failed (no Retry addon registered in
        // the test container, so the throw becomes Failed, not Scheduled).
        var readCtx1 = _fixture.CreateContext();
        var job1 = await readCtx1.Set<Job>().FindAsync([job1Id], CancellationToken.None);
        job1.ShouldNotBeNull();
        job1.CurrentState.ShouldBe(State.Failed);

        // Act 2: enqueue a sibling that needs the same slot, then process it. If the slot leaked,
        // the sibling would be cancelled (Skip mode) at the contention point.
        var job2Id = Guid.NewGuid();
        var ctx2 = _fixture.CreateContext();
        ctx2.Set<Job>().Add(new Job
        {
            Id = job2Id,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeSemaphoreMetadata("release-test-key", 1, ConcurrencyMode.Skip),
        });
        await ctx2.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert: sibling completed (slot was free → acquired → ran to completion). If the slot
        // had leaked from job1's failure, this would be Deleted with a "full (1 slots)" log.
        var readCtx2 = _fixture.CreateContext();
        var job2 = await readCtx2.Set<Job>().FindAsync([job2Id], CancellationToken.None);
        job2.ShouldNotBeNull();
        job2.CurrentState.ShouldBe(State.Completed);
    }

    private static string SerializeSemaphoreMetadata(string key, int limit, ConcurrencyMode mode)
    {
        var dict = new Dictionary<string, object>
        {
            ["ConcurrencyKey"] = key,
            ["ConcurrencyLimit"] = limit,
            ["ConcurrencyMode"] = (int)mode,
        };

        return JsonSerializer.Serialize(dict);
    }

    private WarpWorkerService<TestContext> CreateWorker(FakeSemaphoreProvider lockProvider)
    {
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
            ServerId = Guid.NewGuid(),
            Queues = ["default"],
        });
        services.AddSingleton<IOptions<WarpWorkerConfiguration>>(workerConfig);
        services.AddSingleton<IOptions<WarpConfiguration>>(workerConfig);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var groupConfig = new WorkerGroupConfiguration
        {
            WorkerCount = 1,
            Queues = ["default"],
        };

        return new WarpWorkerService<TestContext>(
            Guid.NewGuid(),
            scopeFactory,
            new NullLogger<WarpWorkerService<TestContext>>(),
            workerConfig,
            groupConfig,
            TimeProvider.System,
            Warp.Tests.Helpers.TestTasks.QueriesFromScope<TestContext>(scopeFactory),
            Warp.Tests.Helpers.TestTasks.NullTransport,
            Warp.Tests.Helpers.TestTasks.NullSignals);
    }

    private ServiceCollection BuildServices()
    {
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
        return services;
    }
}
