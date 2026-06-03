using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Helper;
using Warp.Core.RateLimit;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Features.RateLimit;

[GenerateDatabaseTests]
public abstract class RateLimitTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected RateLimitTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly string[] DefaultQueues = ["default"];

    private static string SerializeMetadata(string key, int limit, int windowSeconds, RateLimitMode? mode = null, RateLimitStyle? style = null)
    {
        var dict = new Dictionary<string, object>
        {
            ["RateLimitKey"] = key,
            ["RateLimitCount"] = limit,
            ["RateLimitWindowSeconds"] = windowSeconds,
        };
        if (mode != null)
        {
            dict["RateLimitMode"] = (int)mode.Value;
        }

        if (style != null)
        {
            dict["RateLimitStyle"] = (int)style.Value;
        }

        return JsonSerializer.Serialize(dict);
    }

    [TimedFact]
    public Task RateLimitAttribute_EmptyKey_ThrowsAtConstruction()
    {
        Should.Throw<ArgumentException>(() => new RateLimitAttribute(string.Empty, 1, 60));
        Should.Throw<ArgumentNullException>(() => new RateLimitAttribute(null!, 1, 60));

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task RateLimitAttribute_ZeroCount_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RateLimitAttribute("k", 0, 60));

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task RateLimitAttribute_ZeroPerSeconds_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RateLimitAttribute("k", 1, 0));

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task RateLimitAttribute_PerSecondsExceedsMaxWindow_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RateLimitAttribute("k", 1, RateLimitAttribute.MaxWindowSeconds + 1));

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task RateLimitAttribute_PerSecondsAtMaxWindow_DoesNotThrow()
    {
        var attr = new RateLimitAttribute("k", 1, RateLimitAttribute.MaxWindowSeconds);

        attr.PerSeconds.ShouldBe(RateLimitAttribute.MaxWindowSeconds);

        return Task.CompletedTask;
    }

    [TimedFact]
    public async Task UnderLimit_FixedSkipMode_JobCompletes()
    {
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
            Metadata = SerializeMetadata("rl-under", 2, 60),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);

        var bucket = await readCtx.Set<RateLimitBucket>()
            .Where(x => x.Name == "rl-under")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        bucket.ShouldNotBeNull();
        bucket.CurrentCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task LimitReached_FixedSkipMode_JobCancelled()
    {
        var now = DateTime.UtcNow;
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-full",
            WindowStartUtc = now,
            CurrentCount = 2,
            TimestampsJson = null,
            UpdatedAt = now,
        });
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
            Metadata = SerializeMetadata("rl-full", 2, 60),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Deleted);

        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Deleted")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        log.ShouldNotBeNull();
        log.Message.ShouldContain("Cancelled");
        log.Message.ShouldContain("rl-full");
        log.Message.ShouldContain("2/60s");
    }

    [TimedFact]
    public async Task LimitReached_FixedWaitMode_JobRescheduled()
    {
        var now = DateTime.UtcNow;
        var windowStart = now.AddSeconds(-10);
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-wait-full",
            WindowStartUtc = windowStart,
            CurrentCount = 2,
            TimestampsJson = null,
            UpdatedAt = now,
        });
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
            Metadata = SerializeMetadata("rl-wait-full", 2, 60, RateLimitMode.Wait),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();

        // ScheduleTime should be windowStart + 60s, which is in the future from now
        job.CurrentState.ShouldBe(State.Scheduled);
        job.ScheduleTime.ShouldBeGreaterThan(now);
        job.ExpireAt.ShouldBeNull();

        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Scheduled")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        log.ShouldNotBeNull();
        log.Message.ShouldContain("Throttled");
        log.Message.ShouldContain("rl-wait-full");
    }

    [TimedFact]
    public async Task NoRateLimitKey_NoCheck_JobCompletes()
    {
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

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);

        var bucketCount = await readCtx.Set<RateLimitBucket>()
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        bucketCount.ShouldBe(0);
    }

    [TimedFact]
    public async Task WindowRollover_AllowsAgain()
    {
        // Bucket whose window has already expired — old WindowStartUtc + 60s < now
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-rollover",
            WindowStartUtc = DateTime.UtcNow.AddMinutes(-10),
            CurrentCount = 2,
            TimestampsJson = null,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
        });
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
            Metadata = SerializeMetadata("rl-rollover", 2, 60),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);

        var bucket = await readCtx.Set<RateLimitBucket>()
            .Where(x => x.Name == "rl-rollover")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        bucket.ShouldNotBeNull();
        bucket.CurrentCount.ShouldBe(1);

        // WindowStartUtc must have rolled forward (within current window).
        bucket.WindowStartUtc.ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(-2));
    }

    [TimedFact]
    public async Task Sliding_PrunesOldTimestamps()
    {
        // Two timestamps both older than the 60s window
        var oldTicks = DateTime.UtcNow.AddMinutes(-5).Ticks;
        var olderTicks = DateTime.UtcNow.AddMinutes(-10).Ticks;
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-slide-old",
            WindowStartUtc = default,
            CurrentCount = 2,
            TimestampsJson = JsonSerializer.Serialize(new[] { olderTicks, oldTicks }),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-3),
        });
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
            Metadata = SerializeMetadata("rl-slide-old", 2, 60, style: RateLimitStyle.Sliding),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);

        var bucket = await readCtx.Set<RateLimitBucket>()
            .Where(x => x.Name == "rl-slide-old")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        bucket.ShouldNotBeNull();

        // Pruned both old, appended one new — count is 1.
        bucket.CurrentCount.ShouldBe(1);

        var ticks = JsonSerializer.Deserialize<long[]>(bucket.TimestampsJson!);
        ticks.ShouldNotBeNull();
        ticks.Length.ShouldBe(1);
    }

    [TimedFact]
    public async Task Sliding_LimitReached_SkipMode_JobCancelled()
    {
        // Two recent timestamps inside the window
        var t1 = DateTime.UtcNow.AddSeconds(-30).Ticks;
        var t2 = DateTime.UtcNow.AddSeconds(-10).Ticks;
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-slide-full",
            WindowStartUtc = default,
            CurrentCount = 2,
            TimestampsJson = JsonSerializer.Serialize(new[] { t1, t2 }),
            UpdatedAt = DateTime.UtcNow,
        });
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
            Metadata = SerializeMetadata("rl-slide-full", 2, 60, style: RateLimitStyle.Sliding),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Deleted);
    }

    [TimedFact]
    public async Task WaitMode_AfterRescheduleFires_JobActuallyRuns()
    {
        // Round-trip: throttle → Scheduled → activation flips back → worker re-runs → Completed.
        // No real-time waiting — we drive each phase explicitly (state manipulation + manual
        // ScheduledJobActivation invocation), so the test is deterministic regardless of the
        // configured window length.
        var seedNow = DateTime.UtcNow;
        var seedCtx = _fixture.CreateContext();

        // Bucket at limit=1 with a window starting "now" — guarantees pass 1 throttles
        // and the reschedule lands at exactly seedNow + 60s.
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-roundtrip",
            WindowStartUtc = seedNow,
            CurrentCount = 1,
            TimestampsJson = null,
            UpdatedAt = seedNow,
        });
        var jobId = Guid.NewGuid();
        seedCtx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = seedNow,
            ScheduleTime = seedNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeMetadata("rl-roundtrip", 1, 60, RateLimitMode.Wait),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Pass 1: bucket is full → Wait throttles → Scheduled at seedNow + 60s
        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var afterPass1 = _fixture.CreateContext();
        var throttled = await afterPass1.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        throttled.ShouldNotBeNull();
        throttled.CurrentState.ShouldBe(State.Scheduled);
        throttled.ScheduleTime.ShouldBe(seedNow.AddSeconds(60), TimeSpan.FromSeconds(1));
        throttled.ExpireAt.ShouldBeNull();

        // Simulate "the reschedule time has elapsed AND the window has rolled" by rewriting
        // the two relevant DB fields. This is the deterministic stand-in for waiting 60s.
        await afterPass1.Set<Job>()
            .Where(x => x.Id == jobId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.ScheduleTime, seedNow.AddMinutes(-1)),
                Xunit.TestContext.Current.CancellationToken);
        await afterPass1.Set<RateLimitBucket>()
            .Where(x => x.Name == "rl-roundtrip")
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.WindowStartUtc, seedNow.AddMinutes(-10)),
                Xunit.TestContext.Current.CancellationToken);

        // Manually invoke ScheduledJobActivation: flips Scheduled → Enqueued for the due row.
        var activationCtx = _fixture.CreateContext();
        var activationResult = await Warp.Tests.Helpers.TestTasks
            .CreateScheduledJobActivation(activationCtx, TimeProvider.System)
            .ActivateWithNotifyAsync(CancellationToken.None);
        activationResult.Activated.ShouldBe(1);

        // Pass 2: worker picks the re-enqueued job. Bucket window now reads as expired, so
        // the pipeline resets it, increments to 1, and the handler runs.
        await worker.GetAndProcessJob(CancellationToken.None);

        var afterPass2 = _fixture.CreateContext();
        var completed = await afterPass2.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        completed.ShouldNotBeNull();
        completed.CurrentState.ShouldBe(State.Completed);

        var bucket = await afterPass2.Set<RateLimitBucket>()
            .Where(x => x.Name == "rl-roundtrip")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        bucket.ShouldNotBeNull();
        bucket.CurrentCount.ShouldBe(1);

        // Window should have rolled forward to the floor-aligned current window
        // (definitely not still 10 minutes in the past).
        bucket.WindowStartUtc.ShouldBeGreaterThan(seedNow.AddMinutes(-5));
    }

    [TimedFact]
    public async Task LockContention_RequeuesWithJitteredFutureScheduleTime()
    {
        // Pre-hold the rate-limit lock so the pipeline's TryAcquireAsync returns null
        // (FakeLockProvider returns null immediately when the lock is already held). Job
        // must reschedule to a future time within the configured jitter window (100-500ms).
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
            Metadata = SerializeMetadata("rl-locked", limit: 5, windowSeconds: 60),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var lockProvider = new FakeLockProvider();
        var heldHandle = lockProvider.HoldLock("warp:ratelimit:rl-locked");
        var beforeRun = DateTime.UtcNow;

        var worker = CreateWorker(lockProvider);
        await worker.GetAndProcessJob(CancellationToken.None);

        await heldHandle.DisposeAsync();

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Scheduled);
        job.ScheduleTime.ShouldBeGreaterThanOrEqualTo(beforeRun.AddMilliseconds(100));
        job.ScheduleTime.ShouldBeLessThanOrEqualTo(DateTime.UtcNow.AddMilliseconds(500));

        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Scheduled")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        log.ShouldNotBeNull();
        log.Message.ShouldContain("lock contention");
        log.Message.ShouldContain("rl-locked");
    }

    [TimedFact]
    public async Task Sliding_OverflowedBucket_TrimsAndUsesTrimmedOldestForReschedule()
    {
        // Defensive case: a previous bug (or hand-edit) wrote more timestamps than the limit.
        // Pipeline should reject (over capacity) and compute nextAvailable from the trimmed
        // tail, not from the bloated oldest. With limit=2 and 4 in-window entries, the trimmed
        // oldest is the 3rd entry chronologically, so nextAvailable = t3 + window.
        var now = DateTime.UtcNow;
        var t1 = now.AddSeconds(-50).Ticks;
        var t2 = now.AddSeconds(-40).Ticks;
        var t3 = now.AddSeconds(-30).Ticks;
        var t4 = now.AddSeconds(-10).Ticks;
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-slide-overflow",
            WindowStartUtc = default,
            CurrentCount = 4,
            TimestampsJson = JsonSerializer.Serialize(new[] { t1, t2, t3, t4 }),
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
            Metadata = SerializeMetadata("rl-slide-overflow", limit: 2, windowSeconds: 60, RateLimitMode.Wait, RateLimitStyle.Sliding),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Scheduled);

        // Trimmed pruned[0] is t3 (last 2 of original 4 = [t3, t4]); nextAvailable = t3 + 60s
        var expected = new DateTime(t3, DateTimeKind.Utc).AddSeconds(60);
        job.ScheduleTime.ShouldBe(expected, TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task AdminOverride_BeatsAttributeLimit()
    {
        // Override with limit=1 should make a second job (count=2 from attribute) get cancelled
        var now = DateTime.UtcNow;
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitOverride>().Add(new RateLimitOverride
        {
            Name = "rl-override",
            Count = 1,
            WindowSeconds = 60,
            UpdatedAt = now,
        });

        // Pre-populate bucket at the override limit
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-override",
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
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(-1),
            Queue = "default",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",

            // Attribute would say count=10, override knocks it down to 1
            Metadata = SerializeMetadata("rl-override", 10, 60),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Deleted);

        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Deleted")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        log.ShouldNotBeNull();
        log.Message.ShouldContain("1/60s");
    }

    [TimedFact]
    public async Task RateLimitAttribute_SetsMetadataAtPublishTime()
    {
        var services = BuildPublisherServices();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        var jobId = await publisher.Enqueue(new RateLimitAttributeRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata!);
        metadata.ShouldNotBeNull();
        metadata.ShouldContainKey("RateLimitKey");
        metadata["RateLimitKey"].ToString().ShouldBe("rl-static");
        metadata.ShouldContainKey("RateLimitCount");
        ((JsonElement)metadata["RateLimitCount"]).GetInt32().ShouldBe(2);
        metadata.ShouldContainKey("RateLimitWindowSeconds");
        ((JsonElement)metadata["RateLimitWindowSeconds"]).GetInt32().ShouldBe(60);
    }

    [TimedFact]
    public async Task WithRateLimit_SetsMetadata()
    {
        var services = BuildPublisherServices();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        var jobId = await publisher.Enqueue(
            new UnitRequest(),
            new JobParameters().WithRateLimit("dynamic-key", count: 5, window: TimeSpan.FromSeconds(30)));
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata!);
        metadata.ShouldNotBeNull();
        metadata["RateLimitKey"].ToString().ShouldBe("dynamic-key");
        ((JsonElement)metadata["RateLimitCount"]).GetInt32().ShouldBe(5);
        ((JsonElement)metadata["RateLimitWindowSeconds"]).GetInt32().ShouldBe(30);
    }

    [TimedFact]
    public async Task RateLimitAttribute_WaitMode_PropagatesToMetadata()
    {
        var services = BuildPublisherServices();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        var jobId = await publisher.Enqueue(new RateLimitWaitAttributeRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(job.Metadata!);
        metadata.ShouldNotBeNull();
        metadata.ShouldContainKey("RateLimitMode");
        ((JsonElement)metadata["RateLimitMode"]).GetInt32().ShouldBe((int)RateLimitMode.Wait);
    }

    private ServiceCollection BuildPublisherServices()
    {
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddSingleton<IWarpLockProvider>(new FakeLockProvider());
        services.AddSingleton<IDatabaseExceptionClassifier>(Warp.Tests.Helpers.TestTasks.ClassifierFor(_fixture.CreateContext()));
        services.AddSingleton(TimeProvider.System);
        new Warp.Core.WarpBuilder<TestContext>(services).AddRateLimit();
        services.AddSingleton<IOptions<WarpConfiguration>>(new OptionsWrapper<WarpConfiguration>(new WarpConfiguration()));

        return services;
    }

    private WarpWorkerService<TestContext> CreateWorker(FakeLockProvider? lockProvider = null)
    {
        lockProvider ??= new FakeLockProvider();
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddSingleton<IWarpLockProvider>(lockProvider);
        services.AddSingleton<IDatabaseExceptionClassifier>(Warp.Tests.Helpers.TestTasks.ClassifierFor(_fixture.CreateContext()));
        services.AddSingleton(TimeProvider.System);
        new Warp.Core.WarpBuilder<TestContext>(services).AddRateLimit();

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
