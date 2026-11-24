using System.Diagnostics;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Logging;
using Warp.Core.RateLimit;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Observability;

/// <summary>
/// Pure unit tests for the warp.rate_limit_check span emitted by RateLimitPipelineBehavior.
/// In-memory IWarpLockProvider + IRateLimitStore doubles — no database required.
/// </summary>
[Trait("Category", "NoDb")]
[Collection("Telemetry")]
public class RateLimitSpanTests
{
    private sealed class TestJob : IJob;

    private sealed class TestJobNoRateLimit : IJob;

    private sealed class NotAJobRequest : IRequest<Unit>;

    private static RateLimitResolver NoAdminResolver() => new((_, _) => Task.FromResult<RateLimitInfo?>(null));

    private sealed class InMemoryStore : IRateLimitStore
    {
        public RateLimitBucket? Bucket { get; set; }

        public Task<RateLimitBucket?> GetAsync(string name, CancellationToken ct) => Task.FromResult(Bucket);

        public Task UpsertAsync(string name, DateTime windowStartUtc, int currentCount, string? timestampsJson, DateTime updatedAt, CancellationToken ct)
        {
            Bucket = new RateLimitBucket
            {
                Name = name,
                WindowStartUtc = windowStartUtc,
                CurrentCount = currentCount,
                TimestampsJson = timestampsJson,
                UpdatedAt = updatedAt,
            };

            return Task.CompletedTask;
        }
    }

    private static (RateLimitPipelineBehavior<T, Unit> Behavior, JobContext JobContext) BuildBehavior<T>(
        IWarpLockProvider lockProvider,
        InMemoryStore store)
        where T : IRequest<Unit>
    {
        var jobContext = new JobContext { JobId = Guid.NewGuid(), TraceId = Guid.NewGuid() };
        var behavior = new RateLimitPipelineBehavior<T, Unit>(
            jobContext,
            lockProvider,
            store,
            NoAdminResolver(),
            TimeProvider.System);

        return (behavior, jobContext);
    }

    [TimedFact]
    public async Task AcquiredPath_EmitsSpanWithOutcomeAcquired()
    {
        using var harness = new ActivityListenerHarness();
        var store = new InMemoryStore();
        var (behavior, jobContext) = BuildBehavior<TestJob>(new FakeLockProvider(), store);
        jobContext.Metadata["RateLimitKey"] = "user-42";
        jobContext.Metadata["RateLimitCount"] = 5;
        jobContext.Metadata["RateLimitWindowSeconds"] = 60;

        await behavior.HandleAsync(new TestJob(), (req, ct) => Task.FromResult(Unit.Value), CancellationToken.None);

        var span = harness.FirstByName("warp.rate_limit_check");
        span.ShouldNotBeNull();
        span.Kind.ShouldBe(ActivityKind.Internal);
        span.GetTagItem(WarpTelemetryAttributes.WarpRateLimitKey).ShouldBe("user-42");
        span.GetTagItem(WarpTelemetryAttributes.WarpRateLimitCount).ShouldBe(5);
        span.GetTagItem(WarpTelemetryAttributes.WarpRateLimitWindowSeconds).ShouldBe(60);
        span.GetTagItem(WarpTelemetryAttributes.WarpRateLimitStyle).ShouldBe("Fixed");
        span.GetTagItem(WarpTelemetryAttributes.WarpRateLimitOutcome).ShouldBe(WarpTelemetryAttributes.WarpRateLimitOutcomeAcquired);
    }

    [TimedFact]
    public async Task LimitReachedSkipMode_EmitsSpanWithOutcomeSkipped()
    {
        using var harness = new ActivityListenerHarness();
        var now = DateTime.UtcNow;
        var store = new InMemoryStore
        {
            Bucket = new RateLimitBucket
            {
                Name = "saturated",
                WindowStartUtc = now,
                CurrentCount = 1,
                UpdatedAt = now,
            },
        };
        var (behavior, jobContext) = BuildBehavior<TestJob>(new FakeLockProvider(), store);
        jobContext.Metadata["RateLimitKey"] = "saturated";
        jobContext.Metadata["RateLimitCount"] = 1;
        jobContext.Metadata["RateLimitWindowSeconds"] = 60;

        var nextCalled = false;
        await behavior.HandleAsync(
            new TestJob(),
            (req, ct) =>
            {
                nextCalled = true;

                return Task.FromResult(Unit.Value);
            },
            CancellationToken.None);

        nextCalled.ShouldBeFalse();
        jobContext.Outcome.ShouldNotBeNull();
        jobContext.Outcome.State.ShouldBe(State.Deleted);

        var span = harness.FirstByName("warp.rate_limit_check");
        span.ShouldNotBeNull();
        span.GetTagItem(WarpTelemetryAttributes.WarpRateLimitOutcome).ShouldBe(WarpTelemetryAttributes.WarpRateLimitOutcomeSkipped);
    }

    [TimedFact]
    public async Task LimitReachedWaitMode_EmitsSpanWithOutcomeThrottled()
    {
        using var harness = new ActivityListenerHarness();
        var now = DateTime.UtcNow;
        var store = new InMemoryStore
        {
            Bucket = new RateLimitBucket
            {
                Name = "throttled",
                WindowStartUtc = now,
                CurrentCount = 1,
                UpdatedAt = now,
            },
        };
        var (behavior, jobContext) = BuildBehavior<TestJob>(new FakeLockProvider(), store);
        jobContext.Metadata["RateLimitKey"] = "throttled";
        jobContext.Metadata["RateLimitCount"] = 1;
        jobContext.Metadata["RateLimitWindowSeconds"] = 60;
        jobContext.Metadata["RateLimitMode"] = (int)RateLimitMode.Wait;

        await behavior.HandleAsync(new TestJob(), (req, ct) => Task.FromResult(Unit.Value), CancellationToken.None);

        var span = harness.FirstByName("warp.rate_limit_check");
        span.ShouldNotBeNull();
        span.GetTagItem(WarpTelemetryAttributes.WarpRateLimitOutcome).ShouldBe(WarpTelemetryAttributes.WarpRateLimitOutcomeThrottled);
    }

    [TimedFact]
    public async Task LockContention_EmitsSpanWithOutcomeLockContention()
    {
        using var harness = new ActivityListenerHarness();
        var lockProvider = new FakeLockProvider();

        // Pre-hold the lock so the pipeline's TryAcquireAsync returns null.
        var held = lockProvider.HoldLock("warp:ratelimit:contended");
        var store = new InMemoryStore();
        var (behavior, jobContext) = BuildBehavior<TestJob>(lockProvider, store);
        jobContext.Metadata["RateLimitKey"] = "contended";
        jobContext.Metadata["RateLimitCount"] = 5;
        jobContext.Metadata["RateLimitWindowSeconds"] = 60;

        await behavior.HandleAsync(new TestJob(), (req, ct) => Task.FromResult(Unit.Value), CancellationToken.None);

        await held.DisposeAsync();

        var span = harness.FirstByName("warp.rate_limit_check");
        span.ShouldNotBeNull();
        span.GetTagItem(WarpTelemetryAttributes.WarpRateLimitOutcome).ShouldBe(WarpTelemetryAttributes.WarpRateLimitOutcomeLockContention);
    }

    [TimedFact]
    public async Task NoRateLimitKey_NoSpanEmitted()
    {
        using var harness = new ActivityListenerHarness();
        var (behavior, _) = BuildBehavior<TestJobNoRateLimit>(new FakeLockProvider(), new InMemoryStore());

        await behavior.HandleAsync(new TestJobNoRateLimit(), (req, ct) => Task.FromResult(Unit.Value), CancellationToken.None);

        harness.FirstByName("warp.rate_limit_check").ShouldBeNull();
    }

    [TimedFact]
    public async Task RequestNotIJob_NoSpanEmittedAndNextCalled()
    {
        using var harness = new ActivityListenerHarness();
        var (behavior, jobContext) = BuildBehavior<NotAJobRequest>(new FakeLockProvider(), new InMemoryStore());
        jobContext.Metadata["RateLimitKey"] = "should-be-ignored";
        jobContext.Metadata["RateLimitCount"] = 5;
        jobContext.Metadata["RateLimitWindowSeconds"] = 60;

        var nextCalled = false;
        await behavior.HandleAsync(
            new NotAJobRequest(),
            (req, ct) =>
            {
                nextCalled = true;

                return Task.FromResult(Unit.Value);
            },
            CancellationToken.None);

        nextCalled.ShouldBeTrue();
        harness.FirstByName("warp.rate_limit_check").ShouldBeNull();
    }
}
