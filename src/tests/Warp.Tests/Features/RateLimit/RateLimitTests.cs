using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Features.RateLimit;

[GenerateDatabaseTests]
public abstract class RateLimitTestsBase : IAsyncLifetime
{
    // Counter increments are summed here rather than written as rows. Tests that assert on
    // Counter/Statistic rows flush it with TestTasks.FlushCountersAsync before reading.
    private readonly WarpCounterBuffer _counterBuffer = new();

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
    public async Task TokenBucket_FreshKey_AllowsInitialBurst_AndLeavesRemainingTokens()
    {
        // #240: a fresh key starts full (capacity = count). One start consumes one token, leaving count-1.
        var jobId = Guid.NewGuid();
        var seedCtx = _fixture.CreateContext();
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
            Metadata = SerializeMetadata("rl-tb-fresh", 2, 60, style: RateLimitStyle.TokenBucket),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);

        var bucket = await readCtx.Set<RateLimitBucket>()
            .Where(x => x.Name == "rl-tb-fresh")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        bucket.ShouldNotBeNull();
        JsonSerializer.Deserialize<double>(bucket.TimestampsJson!).ShouldBe(1.0, 0.001);
    }

    [TimedFact]
    public async Task TokenBucket_Empty_SkipMode_JobCancelled()
    {
        // Bucket empty (0 tokens, just refilled), low refill rate (4/60s) so no full token accrues during
        // the test → the start is dropped in Skip mode.
        var now = DateTime.UtcNow;
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-tb-empty-skip",
            WindowStartUtc = now,
            CurrentCount = 0,
            TimestampsJson = JsonSerializer.Serialize(0.0),
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
            Metadata = SerializeMetadata("rl-tb-empty-skip", 4, 60, RateLimitMode.Skip, RateLimitStyle.TokenBucket),
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
    public async Task TokenBucket_Empty_WaitMode_ReschedulesToNextTokenRefillInstant()
    {
        // Bucket empty, capacity 1 / 60s → rate 1 token / 60s, so the next token refills ~60s out. A slow
        // rate (vs 1/1s) keeps the bucket reliably empty for the test's duration — no flake from a token
        // accruing mid-test — and lets us assert the reschedule lands at the refill INSTANT (~now + 60s),
        // not merely "in the future" and not a fixed-window boundary.
        var now = DateTime.UtcNow;
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-tb-empty-wait",
            WindowStartUtc = now,
            CurrentCount = 0,
            TimestampsJson = JsonSerializer.Serialize(0.0),
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
            Metadata = SerializeMetadata("rl-tb-empty-wait", 1, 60, RateLimitMode.Wait, RateLimitStyle.TokenBucket),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Scheduled);

        // ~60s to refill one token (small slack for the tokens accrued during test execution).
        job.ScheduleTime.ShouldBeGreaterThan(now.AddSeconds(45));
        job.ScheduleTime.ShouldBeLessThan(now.AddSeconds(75));
    }

    [TimedFact]
    public async Task TokenBucket_BucketRowHoldsSlidingTickArray_DegradesGracefullyNotThrows()
    {
        // The bucket row is keyed by name only, so a key reused across styles (or a hand-edit) can leave a
        // sliding-window tick ARRAY in TimestampsJson where the token bucket expects a scalar. Parsing must
        // degrade to an empty bucket instead of throwing a JsonException out of the pipeline (which would
        // fail the job on every attempt). With an empty bucket + no accrual, a Skip-mode start is Deleted —
        // crucially NOT Failed with an exception.
        var now = DateTime.UtcNow;
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-tb-crossstyle",
            WindowStartUtc = now,
            CurrentCount = 2,
            TimestampsJson = JsonSerializer.Serialize(new[] { now.AddSeconds(-10).Ticks, now.AddSeconds(-5).Ticks }),
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
            Metadata = SerializeMetadata("rl-tb-crossstyle", 4, 60, RateLimitMode.Skip, RateLimitStyle.TokenBucket),
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
    public async Task TokenBucket_RefillsOverElapsedTime_AllowsAfterEnoughElapsed()
    {
        // Bucket last refilled 3s ago with 0 tokens; at 1 token/sec (5/5s) ~3 tokens have accrued, so the
        // start is allowed — proves the lazy continuous refill from the elapsed time since last accept.
        var now = DateTime.UtcNow;
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-tb-refill",
            WindowStartUtc = now.AddSeconds(-3),
            CurrentCount = 0,
            TimestampsJson = JsonSerializer.Serialize(0.0),
            UpdatedAt = now.AddSeconds(-3),
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
            Metadata = SerializeMetadata("rl-tb-refill", 5, 5, RateLimitMode.Skip, RateLimitStyle.TokenBucket),
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);
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
    public async Task WithRateLimit_SetsMetadata()
    {
        var services = BuildPublisherServices();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisherCtx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(publisherCtx, Options.Create(new WarpConfiguration()), TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

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
    public async Task KnownExhaustedKey_SkipMode_SecondJobRefusedFromMemory()
    {
        // Job 1 is refused by the database. The bucket row is then deleted — a database evaluation of job 2
        // would now see a fresh key and ADMIT it, so a refusal can only have come from the in-process gate.
        var clock = new FakeTimeProvider(CacheTestStart);
        await SeedFullFixedBucketAsync("rl-cache-skip", count: 1);
        var job1 = await SeedRateLimitedJobAsync("rl-cache-skip", 1, 60, RateLimitMode.Skip, CacheTestStart.UtcDateTime.AddMinutes(-2));
        var job2 = await SeedRateLimitedJobAsync("rl-cache-skip", 1, 60, RateLimitMode.Skip, CacheTestStart.UtcDateTime.AddMinutes(-1));
        var worker = CreateWorker(timeProvider: clock);

        await worker.GetAndProcessJob(CancellationToken.None);
        await DeleteBucketAsync("rl-cache-skip");
        await worker.GetAndProcessJob(CancellationToken.None);

        (await ReadStateAsync(job1)).ShouldBe(State.Deleted);
        (await ReadStateAsync(job2)).ShouldBe(State.Deleted);
        (await ReadBucketAsync("rl-cache-skip")).ShouldBeNull("a cached refusal must not touch the bucket");
    }

    [TimedFact]
    public async Task CachedRejection_PastMaxClosedAge_ReevaluatedAgainstDatabase()
    {
        // The window still has 50s to run, but a cached refusal is trusted for MaxClosedAge at most (an admin
        // may have raised the limit on another server). Past it, job 2 goes back to the database, which —
        // with the bucket row gone — admits it.
        var clock = new FakeTimeProvider(CacheTestStart);
        await SeedFullFixedBucketAsync("rl-cache-age", count: 1);
        var job1 = await SeedRateLimitedJobAsync("rl-cache-age", 1, 60, RateLimitMode.Skip, CacheTestStart.UtcDateTime.AddMinutes(-2));
        var job2 = await SeedRateLimitedJobAsync("rl-cache-age", 1, 60, RateLimitMode.Skip, CacheTestStart.UtcDateTime.AddMinutes(-1));
        var worker = CreateWorker(timeProvider: clock);

        await worker.GetAndProcessJob(CancellationToken.None);
        await DeleteBucketAsync("rl-cache-age");
        clock.Advance(RateLimitGate.MaxClosedAge + TimeSpan.FromSeconds(1));
        await worker.GetAndProcessJob(CancellationToken.None);

        (await ReadStateAsync(job1)).ShouldBe(State.Deleted);
        (await ReadStateAsync(job2)).ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task AdminLimitRaised_ForgetsCachedRejection()
    {
        var clock = new FakeTimeProvider(CacheTestStart);
        await SeedFullFixedBucketAsync("rl-cache-admin", count: 1);
        var job1 = await SeedRateLimitedJobAsync("rl-cache-admin", 1, 60, RateLimitMode.Skip, CacheTestStart.UtcDateTime.AddMinutes(-2));
        var job2 = await SeedRateLimitedJobAsync("rl-cache-admin", 1, 60, RateLimitMode.Skip, CacheTestStart.UtcDateTime.AddMinutes(-1));
        var (worker, services) = CreateWorkerWithServices(timeProvider: clock);

        await worker.GetAndProcessJob(CancellationToken.None);
        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IRateLimitManager>()
                .AddOrUpdateLimit("rl-cache-admin", 5, 60, Xunit.TestContext.Current.CancellationToken);
        }

        await worker.GetAndProcessJob(CancellationToken.None);

        (await ReadStateAsync(job1)).ShouldBe(State.Deleted);
        (await ReadStateAsync(job2)).ShouldBe(State.Completed, "raising the limit on this process must take effect for the next job");
    }

    [TimedFact]
    public async Task WaitMode_FixedWindow_ThrottledJobsFillEachWindowThenTheNext()
    {
        // 2 per 60s. Three throttled jobs used to share one reopening instant, where only two fit. Now the
        // first two are scheduled at the next window's start (a Fixed window admits its count at once) and
        // the third at the start of the window after, and the cursor is left at the first position not used.
        var clock = new FakeTimeProvider(CacheTestStart);
        await SeedFullFixedBucketAsync("rl-slots", count: 2);
        var jobs = new[]
        {
            await SeedRateLimitedJobAsync("rl-slots", 2, 60, RateLimitMode.Wait, CacheTestStart.UtcDateTime.AddMinutes(-3)),
            await SeedRateLimitedJobAsync("rl-slots", 2, 60, RateLimitMode.Wait, CacheTestStart.UtcDateTime.AddMinutes(-2)),
            await SeedRateLimitedJobAsync("rl-slots", 2, 60, RateLimitMode.Wait, CacheTestStart.UtcDateTime.AddMinutes(-1)),
        };
        var worker = CreateWorker(timeProvider: clock);

        for (var i = 0; i < jobs.Length; i++)
        {
            await worker.GetAndProcessJob(CancellationToken.None);
        }

        var reopen = WindowStart.AddSeconds(60);
        (await ReadScheduleTimeAsync(jobs[0])).ShouldBe(reopen);
        (await ReadScheduleTimeAsync(jobs[1])).ShouldBe(reopen);
        (await ReadScheduleTimeAsync(jobs[2])).ShouldBe(reopen.AddSeconds(60));
        (await ReadBucketAsync("rl-slots")).ShouldNotBeNull().NextSlotUtc.ShouldBe(reopen.AddSeconds(90));
    }

    [TimedFact]
    public async Task WaitMode_TokenBucket_ThrottledJobsSpacedAtRefillRate()
    {
        // 4 per 60s refills one token every 15s. An empty bucket's first throttled job waits for the next
        // token; the second waits for the one after, not for the same instant.
        var clock = new FakeTimeProvider(CacheTestStart);
        var now = CacheTestStart.UtcDateTime;
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-tb-slots",
            WindowStartUtc = now,
            CurrentCount = 0,
            TimestampsJson = JsonSerializer.Serialize(0.0),
            UpdatedAt = now,
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        var job1 = await SeedRateLimitedJobAsync("rl-tb-slots", 4, 60, RateLimitMode.Wait, now.AddMinutes(-2), RateLimitStyle.TokenBucket);
        var job2 = await SeedRateLimitedJobAsync("rl-tb-slots", 4, 60, RateLimitMode.Wait, now.AddMinutes(-1), RateLimitStyle.TokenBucket);
        var worker = CreateWorker(timeProvider: clock);

        await worker.GetAndProcessJob(CancellationToken.None);
        await worker.GetAndProcessJob(CancellationToken.None);

        (await ReadScheduleTimeAsync(job1)).ShouldBe(now.AddSeconds(15));
        (await ReadScheduleTimeAsync(job2)).ShouldBe(now.AddSeconds(30));
    }

    [TimedFact]
    public async Task WaitMode_LeasedSlotBlock_ServedFromMemoryUntilExhausted()
    {
        // 60 per 60s → one slot a second, so a lease takes LeaseHorizon (5s) worth: five slots. Job 1 leases
        // them; the bucket row is then deleted, so jobs 2-5 can only be placed from the leased block. Job 6
        // finds the block empty, goes to the database, sees a fresh key and runs.
        var clock = new FakeTimeProvider(CacheTestStart);
        await SeedFullFixedBucketAsync("rl-lease", count: 60);
        var jobs = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            jobs.Add(await SeedRateLimitedJobAsync("rl-lease", 60, 60, RateLimitMode.Wait, CacheTestStart.UtcDateTime.AddMinutes(-10 + i)));
        }

        var worker = CreateWorker(timeProvider: clock);

        await worker.GetAndProcessJob(CancellationToken.None);
        var reopen = WindowStart.AddSeconds(60);
        (await ReadBucketAsync("rl-lease")).ShouldNotBeNull().NextSlotUtc.ShouldBe(reopen.AddSeconds(5));
        await DeleteBucketAsync("rl-lease");

        for (var i = 1; i < 6; i++)
        {
            await worker.GetAndProcessJob(CancellationToken.None);
        }

        for (var i = 0; i < 5; i++)
        {
            (await ReadScheduleTimeAsync(jobs[i])).ShouldBe(reopen, "a Fixed window admits its count at once, so its slots all start with it");
        }

        (await ReadStateAsync(jobs[5])).ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task WaitMode_TwoServers_DrawFromOneCursor()
    {
        // Two containers = two servers, each with its own gate. The cursor lives on the bucket row, so the
        // second server's throttled job lands in the NEXT window, not in the one already promised away.
        var clock = new FakeTimeProvider(CacheTestStart);
        await SeedFullFixedBucketAsync("rl-two-servers", count: 1);
        var job1 = await SeedRateLimitedJobAsync("rl-two-servers", 1, 60, RateLimitMode.Wait, CacheTestStart.UtcDateTime.AddMinutes(-2));
        var job2 = await SeedRateLimitedJobAsync("rl-two-servers", 1, 60, RateLimitMode.Wait, CacheTestStart.UtcDateTime.AddMinutes(-1));

        await CreateWorker(timeProvider: clock).GetAndProcessJob(CancellationToken.None);
        await CreateWorker(timeProvider: clock).GetAndProcessJob(CancellationToken.None);

        var reopen = WindowStart.AddSeconds(60);
        (await ReadScheduleTimeAsync(job1)).ShouldBe(reopen);
        (await ReadScheduleTimeAsync(job2)).ShouldBe(reopen.AddSeconds(60));
    }

    [TimedFact]
    public async Task AdmittedJob_LockReleasedBeforeHandlerRuns()
    {
        // The lock guards the check-and-increment only. It used to stay held until the behaviour returned,
        // across the handler, so admitted jobs on a key ran one at a time and every other worker queued on
        // the lock for the handler's full duration. Pin the handler and prove the lock is free meanwhile.
        var locks = new FakeLockProvider();
        var barrier = new BarrierSignal();
        var jobId = await SeedRateLimitedJobAsync("rl-lock-scope", 5, 60, RateLimitMode.Skip, DateTime.UtcNow.AddMinutes(-1), requestType: typeof(BarrierRequest));
        var worker = CreateWorker(locks, barrier: barrier);

        var running = worker.GetAndProcessJob(CancellationToken.None);
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
            .ShouldBeTrue("the handler must be running before the lock is probed");

        var probe = await locks.TryAcquireAsync("warp:ratelimit:rl-lock-scope", TimeSpan.Zero, Xunit.TestContext.Current.CancellationToken);
        var lockWasFree = probe != null;
        if (probe != null)
        {
            await probe.DisposeAsync();
        }

        barrier.CanFinish.Release();
        await running;

        lockWasFree.ShouldBeTrue("the rate-limit lock must not be held while the handler runs");
        (await ReadStateAsync(jobId)).ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task WaitMode_WindowShorterThanActivation_TakesOneWindowPerActivationStep()
    {
        // 2 per 2s, but throttled jobs only come back on a 10s activation tick, and a batch released on one
        // tick only ever meets the window open at that moment. So each 10s step gets 2 jobs (one window's
        // worth): two at the reopening instant, the third a full activation step later, not 2s later.
        var clock = new FakeTimeProvider(CacheTestStart);
        var windowStart = CacheTestStart.UtcDateTime;
        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = "rl-short-window",
            WindowStartUtc = windowStart,
            CurrentCount = 2,
            UpdatedAt = windowStart,
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        var jobs = new[]
        {
            await SeedRateLimitedJobAsync("rl-short-window", 2, 2, RateLimitMode.Wait, windowStart.AddMinutes(-3)),
            await SeedRateLimitedJobAsync("rl-short-window", 2, 2, RateLimitMode.Wait, windowStart.AddMinutes(-2)),
            await SeedRateLimitedJobAsync("rl-short-window", 2, 2, RateLimitMode.Wait, windowStart.AddMinutes(-1)),
        };
        var worker = CreateWorker(timeProvider: clock, activationInterval: TimeSpan.FromSeconds(10));

        for (var i = 0; i < jobs.Length; i++)
        {
            await worker.GetAndProcessJob(CancellationToken.None);
        }

        var reopen = windowStart.AddSeconds(2);
        (await ReadScheduleTimeAsync(jobs[0])).ShouldBe(reopen);
        (await ReadScheduleTimeAsync(jobs[1])).ShouldBe(reopen);
        (await ReadScheduleTimeAsync(jobs[2])).ShouldBe(reopen.AddSeconds(10));
    }

    // 10s into a minute, so a 60s Fixed window seeded at WindowStart has 50s left to run.
    private static readonly DateTimeOffset CacheTestStart = new(2026, 11, 19, 12, 0, 10, TimeSpan.Zero);

    private static readonly DateTime WindowStart = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private async Task SeedFullFixedBucketAsync(string key, int count)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<RateLimitBucket>().Add(new RateLimitBucket
        {
            Name = key,
            WindowStartUtc = WindowStart,
            CurrentCount = count,
            UpdatedAt = WindowStart,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SeedRateLimitedJobAsync(
        string key, int count, int windowSeconds, RateLimitMode mode, DateTime scheduleTime, RateLimitStyle? style = null, Type? requestType = null)
    {
        var ctx = _fixture.CreateContext();
        var id = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = id,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = scheduleTime,
            ScheduleTime = scheduleTime,
            Queue = "default",
            Type = (requestType ?? typeof(UnitRequest)).AssemblyQualifiedName,
            Message = "{}",
            Metadata = SerializeMetadata(key, count, windowSeconds, mode, style),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return id;
    }

    private async Task DeleteBucketAsync(string key) =>
        await _fixture.CreateContext().Set<RateLimitBucket>()
            .Where(x => x.Name == key)
            .ExecuteDeleteAsync(Xunit.TestContext.Current.CancellationToken);

    private async Task<RateLimitBucket?> ReadBucketAsync(string key) =>
        await _fixture.CreateContext().Set<RateLimitBucket>()
            .AsNoTracking()
            .Where(x => x.Name == key)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

    private async Task<State> ReadStateAsync(Guid jobId) =>
        await _fixture.CreateContext().Set<Job>()
            .Where(x => x.Id == jobId)
            .Select(x => x.CurrentState)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

    private async Task<DateTime> ReadScheduleTimeAsync(Guid jobId) =>
        await _fixture.CreateContext().Set<Job>()
            .Where(x => x.Id == jobId)
            .Select(x => x.ScheduleTime)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

    private ServiceCollection BuildPublisherServices()
    {
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddSingleton<IWarpLockProvider>(new FakeLockProvider());
        services.AddSingleton<IDatabaseExceptionClassifier>(Warp.Tests.Helpers.TestTasks.ClassifierFor(_fixture.CreateContext()));
        services.AddSingleton(TimeProvider.System);
        new Warp.Core.WarpBuilder<TestContext>(services).AddRateLimit();
        services.AddSingleton<IOptions<WarpConfiguration>>(new OptionsWrapper<WarpConfiguration>(new WarpConfiguration()));

        return services;
    }

    private WarpWorkerService<TestContext> CreateWorker(
        FakeLockProvider? lockProvider = null, TimeProvider? timeProvider = null, BarrierSignal? barrier = null, TimeSpan? activationInterval = null) =>
        CreateWorkerWithServices(lockProvider, timeProvider, barrier, activationInterval).Worker;

    /// <summary>One worker and the container it runs in — the container holds the process-wide <c>RateLimitGate</c>, so two calls model two servers.</summary>
    private (WarpWorkerService<TestContext> Worker, ServiceProvider Services) CreateWorkerWithServices(
        FakeLockProvider? lockProvider = null, TimeProvider? timeProvider = null, BarrierSignal? barrier = null, TimeSpan? activationInterval = null)
    {
        lockProvider ??= new FakeLockProvider();
        timeProvider ??= TimeProvider.System;
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddSingleton<IWarpLockProvider>(lockProvider);
        services.AddSingleton<IDatabaseExceptionClassifier>(Warp.Tests.Helpers.TestTasks.ClassifierFor(_fixture.CreateContext()));
        services.AddSingleton(timeProvider);
        services.AddSingleton(barrier ?? new BarrierSignal());
        if (activationInterval is { } interval)
        {
            // What the worker registers in a real server; this harness builds the worker by hand.
            services.AddSingleton<IScheduledActivationCadence>(new FixedActivationCadence(interval));
        }

        new Warp.Core.WarpBuilder<TestContext>(services).AddRateLimit();

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

        var worker = new WarpWorkerService<TestContext>(
            WorkerId,
            scopeFactory,
            new NullLogger<WarpWorkerService<TestContext>>(),
            workerConfig,
            groupConfig,
            timeProvider,
            Warp.Tests.Helpers.TestTasks.QueriesFromScope<TestContext>(scopeFactory),
            Warp.Tests.Helpers.TestTasks.NullTransport,
            Warp.Tests.Helpers.TestTasks.NullSignals,
            _counterBuffer);

        return (worker, provider);
    }

    private sealed class FixedActivationCadence(TimeSpan interval) : IScheduledActivationCadence
    {
        public TimeSpan Interval => interval;
    }
}
