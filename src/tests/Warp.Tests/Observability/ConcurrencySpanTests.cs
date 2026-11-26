using System.Diagnostics;
using Shouldly;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Logging;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Observability;

/// <summary>
/// Pure unit tests for the warp.concurrency_acquire span emitted by ConcurrencyPipelineBehavior.
/// Uses an in-memory IWarpSemaphoreProvider double — no database required.
/// </summary>
[Trait("Category", "NoDb")]
[Collection("Telemetry")]
public class ConcurrencySpanTests
{
    private sealed class TestJob : IJob;

    private sealed class TestJobNoConcurrency : IJob;

    private sealed class NotAJobRequest : IRequest<Unit>;

    private sealed class ThrowingSemaphoreProvider : IWarpSemaphoreProvider
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string name, int maxCount, TimeSpan timeout, CancellationToken ct)
            => throw new InvalidOperationException("simulated semaphore-provider failure");
    }

    private static ConcurrencyLimitResolver NoAdminResolver() =>
        new((_, _) => Task.FromResult<int?>(null));

    [TimedFact]
    public async Task HandleAsync_AcquiredPath_EmitsConcurrencyAcquireSpanWithAcquiredTrue()
    {
        using var harness = new ActivityListenerHarness();
        var jobContext = new JobContext { JobId = Guid.NewGuid(), TraceId = Guid.NewGuid() };
        jobContext.Metadata["ConcurrencyKey"] = "user-42";
        jobContext.Metadata["ConcurrencyLimit"] = 5;
        var semaphoreProvider = new FakeSemaphoreProvider();
        var behavior = new ConcurrencyPipelineBehavior<TestJob, Unit>(jobContext, semaphoreProvider, NoAdminResolver(), TimeProvider.System);

        await behavior.HandleAsync(new TestJob(), (req, ct) => Task.FromResult(Unit.Value), CancellationToken.None);

        var span = harness.FirstByName("warp.concurrency_acquire");
        span.ShouldNotBeNull();
        span.Kind.ShouldBe(ActivityKind.Internal);
        span.GetTagItem(WarpTelemetryAttributes.WarpConcurrencyKey).ShouldBe("user-42");
        span.GetTagItem(WarpTelemetryAttributes.WarpConcurrencyLimit).ShouldBe(5);
        span.GetTagItem(WarpTelemetryAttributes.WarpConcurrencyAcquired).ShouldBe(true);
    }

    [TimedFact]
    public async Task HandleAsync_HeldByOther_EmitsConcurrencyAcquireSpanWithAcquiredFalseAndShortCircuits()
    {
        using var harness = new ActivityListenerHarness();
        var jobContext = new JobContext { JobId = Guid.NewGuid(), TraceId = Guid.NewGuid() };
        jobContext.Metadata["ConcurrencyKey"] = "user-99";
        jobContext.Metadata["ConcurrencyLimit"] = 1;
        var semaphoreProvider = new FakeSemaphoreProvider();

        // Pre-saturate the slot so the behavior sees it as held.
        var heldHandle = semaphoreProvider.HoldSlot("warp:concurrency-mutex:user-99");

        var behavior = new ConcurrencyPipelineBehavior<TestJob, Unit>(jobContext, semaphoreProvider, NoAdminResolver(), TimeProvider.System);
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

        var span = harness.FirstByName("warp.concurrency_acquire");
        span.ShouldNotBeNull();
        span.GetTagItem(WarpTelemetryAttributes.WarpConcurrencyAcquired).ShouldBe(false);

        await heldHandle.DisposeAsync();
    }

    [TimedFact]
    public async Task HandleAsync_NoConcurrencyKey_NoSpanEmitted()
    {
        using var harness = new ActivityListenerHarness();
        var jobContext = new JobContext { JobId = Guid.NewGuid(), TraceId = Guid.NewGuid() };
        var semaphoreProvider = new FakeSemaphoreProvider();
        var behavior = new ConcurrencyPipelineBehavior<TestJobNoConcurrency, Unit>(jobContext, semaphoreProvider, NoAdminResolver(), TimeProvider.System);

        await behavior.HandleAsync(new TestJobNoConcurrency(), (req, ct) => Task.FromResult(Unit.Value), CancellationToken.None);

        harness.FirstByName("warp.concurrency_acquire").ShouldBeNull();
    }

    [TimedFact]
    public async Task HandleAsync_SemaphoreProviderThrows_SpanIsStillCapturedAndExceptionPropagates()
    {
        using var harness = new ActivityListenerHarness();
        var jobContext = new JobContext { JobId = Guid.NewGuid(), TraceId = Guid.NewGuid() };
        jobContext.Metadata["ConcurrencyKey"] = "user-99";
        jobContext.Metadata["ConcurrencyLimit"] = 1;
        var semaphoreProvider = new ThrowingSemaphoreProvider();
        var behavior = new ConcurrencyPipelineBehavior<TestJob, Unit>(jobContext, semaphoreProvider, NoAdminResolver(), TimeProvider.System);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            behavior.HandleAsync(new TestJob(), (req, ct) => Task.FromResult(Unit.Value), CancellationToken.None));

        // The `using var concurrencySpan = WarpTelemetry.StartConcurrencyActivity()` block must
        // dispose the activity even when TryAcquireAsync throws. Without that lifetime guarantee
        // we'd leak open spans on provider failures (network blips, etc.).
        var span = harness.FirstByName("warp.concurrency_acquire");
        span.ShouldNotBeNull();
        span.GetTagItem(WarpTelemetryAttributes.WarpConcurrencyKey).ShouldBe("user-99");
        span.GetTagItem(WarpTelemetryAttributes.WarpConcurrencyLimit).ShouldBe(1);

        // warp.concurrency.acquired never gets set because TryAcquireAsync threw before returning.
        span.GetTagItem(WarpTelemetryAttributes.WarpConcurrencyAcquired).ShouldBeNull();
    }

    [TimedFact]
    public async Task HandleAsync_RequestNotIJob_NoSpanEmittedAndNextCalled()
    {
        using var harness = new ActivityListenerHarness();
        var jobContext = new JobContext { JobId = Guid.NewGuid(), TraceId = Guid.NewGuid() };
        jobContext.Metadata["ConcurrencyKey"] = "should-be-ignored";
        var semaphoreProvider = new FakeSemaphoreProvider();
        var behavior = new ConcurrencyPipelineBehavior<NotAJobRequest, Unit>(jobContext, semaphoreProvider, NoAdminResolver(), TimeProvider.System);
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
        harness.FirstByName("warp.concurrency_acquire").ShouldBeNull();
    }
}
