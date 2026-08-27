using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Timeout;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features.Timeout;

[Trait("Category", "NoDb")]
public class TimeoutPipelineBehaviorTests
{
    private static TimeoutPipelineBehavior<UnitRequest, Unit> Build(FakeTimeProvider time, JobContext context, TimeoutOptions? options = null)
    {
        return Build<UnitRequest, Unit>(time, context, options);
    }

    private static TimeoutPipelineBehavior<TRequest, TResponse> Build<TRequest, TResponse>(FakeTimeProvider time, JobContext context, TimeoutOptions? options = null)
        where TRequest : IRequest<TResponse>
    {
        return new TimeoutPipelineBehavior<TRequest, TResponse>(
            context,
            time,
            Options.Create(options ?? new TimeoutOptions()),
            NullLogger<TimeoutPipelineBehavior<TRequest, TResponse>>.Instance);
    }

    [TimedFact]
    public async Task NonJobRequest_PassesThroughWithoutTimeout()
    {
        // request is neither IJob nor IMessage → bail immediately. In-memory IRequest<T> callers
        // wrap their own CancellationToken if they need a deadline. Even with TimeoutSeconds
        // metadata set, the behavior must be a no-op.
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        ctx.Metadata["TimeoutSeconds"] = 1L;
        ctx.Metadata["TimeoutMode"] = (int)TimeoutMode.Delete;

        var behavior = Build<GetGreetingRequest, string>(time, ctx);

        var result = await behavior.HandleAsync(
            new GetGreetingRequest { Name = "test" },
            (req, ct) => Task.FromResult("ok"),
            CancellationToken.None);

        result.ShouldBe("ok");
        ctx.Outcome.ShouldBeNull();
    }

    [TimedFact]
    public async Task IMessageRequest_WithTimeoutMetadata_TimesOut()
    {
        // Messages joined the policy axes: a routed message child with timeout metadata is enforced
        // exactly like a job. (Saga proxies stay exempt — see the policy-exempt test below.)
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        ctx.Metadata["TimeoutSeconds"] = 1L;
        ctx.Metadata["TimeoutMode"] = (int)TimeoutMode.Delete;

        var behavior = Build<SagaShapedMessage, Unit>(time, ctx);

        var handlerStarted = new TaskCompletionSource();
        var handlerTask = behavior.HandleAsync(
            new SagaShapedMessage(),
            async (req, ct) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return Unit.Value;
            },
            CancellationToken.None);

        await handlerStarted.Task;
        time.Advance(TimeSpan.FromSeconds(2));

        await handlerTask;

        ctx.Outcome.ShouldNotBeNull();
        ctx.Outcome!.State.ShouldBe(State.Deleted);
    }

    [TimedFact]
    public async Task PolicyExemptHandler_PassesThroughWithoutTimeout()
    {
        // Saga proxies implement IPolicyExemptHandler: they serialize on their own mutex inside
        // SagaHandlerProxy and applying a timeout to the proxy's HandleAsync would race the mutex
        // hold + SaveChanges. The Limitations section of website/docs/features/sagas.md documents
        // this; this test pins it — even with timeout metadata set, the behavior must be a no-op
        // when the bound handler is exempt.
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid(), HandlerType = typeof(ExemptHandler) };
        ctx.Metadata["TimeoutSeconds"] = 1L;
        ctx.Metadata["TimeoutMode"] = (int)TimeoutMode.Delete;

        var behavior = Build<SagaShapedMessage, Unit>(time, ctx);

        var result = await behavior.HandleAsync(
            new SagaShapedMessage(),
            (req, ct) => Task.FromResult(Unit.Value),
            CancellationToken.None);

        result.ShouldBe(Unit.Value);
        ctx.Outcome.ShouldBeNull();
    }

    [TimedFact]
    public async Task HandlerDeclaredTimeout_ResolvedAndStamped_Enforced()
    {
        // Handler axis: empty metadata, [Timeout(1)] on the bound handler type. The behavior
        // resolves it, stamps it into metadata (pinning — visible on the row after write-back)
        // and enforces it in the same attempt.
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid(), HandlerType = typeof(HandlerWithTimeout) };
        var behavior = Build(time, ctx);

        var handlerStarted = new TaskCompletionSource();
        var handlerTask = behavior.HandleAsync(
            new UnitRequest(),
            async (req, ct) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return Unit.Value;
            },
            CancellationToken.None);

        await handlerStarted.Task;
        time.Advance(TimeSpan.FromSeconds(2));

        await handlerTask;

        ctx.Outcome.ShouldNotBeNull();
        ctx.Outcome!.State.ShouldBe(State.Deleted);
        ctx.Metadata["TimeoutSeconds"].ShouldBe(1);
    }

    [TimedFact]
    public async Task ContractTimeout_EmptyMetadata_ResolvedAtExecution()
    {
        // Contract rung at execution: a directly-staged job (recurring firing) bypassed the publish
        // pipeline, so metadata is empty even though the REQUEST type declares [Timeout(1)]. The
        // execution-side resolver must find and enforce it — the recurring-job fix.
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        var behavior = Build<ContractTimedRequest, Unit>(time, ctx);

        var handlerStarted = new TaskCompletionSource();
        var handlerTask = behavior.HandleAsync(
            new ContractTimedRequest(),
            async (req, ct) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return Unit.Value;
            },
            CancellationToken.None);

        await handlerStarted.Task;
        time.Advance(TimeSpan.FromSeconds(2));

        await handlerTask;

        ctx.Outcome.ShouldNotBeNull();
        ctx.Outcome!.State.ShouldBe(State.Deleted);
        ctx.Metadata["TimeoutSeconds"].ShouldBe(1);
    }

    [TimedFact]
    public async Task PerAttemptGlobalDefault_AppliedTransiently_NeverStamped()
    {
        // The PerAttempt global default moved from publish-stamping to execution (the #236 shape,
        // for Timeout): it is enforced from live options and never written into metadata, so a
        // later handler/contract declaration is not shadowed by a frozen default.
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        var behavior = Build(time, ctx, new TimeoutOptions { Default = TimeSpan.FromSeconds(1) });

        var handlerStarted = new TaskCompletionSource();
        var handlerTask = behavior.HandleAsync(
            new UnitRequest(),
            async (req, ct) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return Unit.Value;
            },
            CancellationToken.None);

        await handlerStarted.Task;
        time.Advance(TimeSpan.FromSeconds(2));

        await handlerTask;

        ctx.Outcome.ShouldNotBeNull();
        ctx.Outcome!.State.ShouldBe(State.Deleted);
        ctx.Metadata.ContainsKey("TimeoutSeconds").ShouldBeFalse();
    }

    [TimedFact]
    public async Task HandlerDeclaredTimeout_WinsOverPerAttemptGlobalDefault()
    {
        // SC6: with a 60s global default configured, the handler's [Timeout(1)] must win — the
        // exact shadowing Retry fixed as #236.
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid(), HandlerType = typeof(HandlerWithTimeout) };
        var behavior = Build(time, ctx, new TimeoutOptions { Default = TimeSpan.FromSeconds(60) });

        var handlerStarted = new TaskCompletionSource();
        var handlerTask = behavior.HandleAsync(
            new UnitRequest(),
            async (req, ct) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return Unit.Value;
            },
            CancellationToken.None);

        await handlerStarted.Task;
        time.Advance(TimeSpan.FromSeconds(2));

        await handlerTask;

        ctx.Outcome.ShouldNotBeNull();
        ctx.Outcome!.State.ShouldBe(State.Deleted);
        ctx.Outcome.LogMessage.ShouldBe("Timed out after 1s");
    }

    [TimedFact]
    public async Task TotalScopedGlobalDefault_NotAppliedAtExecution()
    {
        // A Total-scoped default is publish-stamped (its deadline measures from enqueue); applying
        // it at execution would measure from first pickup. An unstamped job under a Total default
        // (a directly-staged row) therefore runs without a timeout rather than with a redefined one.
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        var behavior = Build(time, ctx, new TimeoutOptions { Default = TimeSpan.FromSeconds(1), DefaultScope = TimeoutScope.Total });

        var result = await behavior.HandleAsync(
            new UnitRequest(),
            (req, ct) => Task.FromResult(Unit.Value),
            CancellationToken.None);

        result.ShouldBe(Unit.Value);
        ctx.Outcome.ShouldBeNull();
        ctx.Metadata.ContainsKey("TimeoutSeconds").ShouldBeFalse();
    }

    [TimedFact]
    public async Task HandlerTotalTimeout_IsInertAndWarnsOnce_NeverThrows()
    {
        // A handler-declared Scope = Total cannot be honoured at execution (WARP002 is the build-time
        // gate). The runtime backstop must not throw from inside the pipeline: an outer Retry would
        // read the WarpException as a handler failure and burn the entire retry budget on a static
        // misconfiguration. Warn once per request type and run the handler without the timeout.
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid(), HandlerType = typeof(HandlerWithTotalTimeout) };
        var logger = new CapturingLogger<TimeoutPipelineBehavior<HandlerTotalRequest, Unit>>();
        var behavior = new TimeoutPipelineBehavior<HandlerTotalRequest, Unit>(
            ctx, time, Options.Create(new TimeoutOptions()), logger);

        var handlerRuns = 0;
        for (var i = 0; i < 2; i++)
        {
            var result = await behavior.HandleAsync(
                new HandlerTotalRequest(),
                (req, ct) =>
                {
                    handlerRuns++;
                    return Task.FromResult(Unit.Value);
                },
                CancellationToken.None);

            result.ShouldBe(Unit.Value);
        }

        handlerRuns.ShouldBe(2);
        ctx.Outcome.ShouldBeNull();
        ctx.Metadata.ContainsKey("TimeoutSeconds").ShouldBeFalse();
        logger.Warnings.ShouldBe(1);
    }

    [TimedFact]
    public async Task ContractTotalTimeout_NoDeadline_RefusedNotRedefined_WarnsOnce()
    {
        // SC5b: a recurring firing whose CONTRACT declares Scope = Total has no publish-time
        // deadline. Inventing one at execution would measure from first pickup instead of enqueue,
        // so the resolver refuses the stamp and the job runs without a timeout from the attribute.
        // The Warning fires exactly ONCE per request type — the dedupe rides a per-closed-generic
        // static flag scoped to this warning kind, so this test still needs its own request type
        // for this warning (other warning kinds have their own flags and don't collide here).
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        var logger = new CapturingLogger<TimeoutPipelineBehavior<ContractTotalTimedRequest, Unit>>();
        var behavior = new TimeoutPipelineBehavior<ContractTotalTimedRequest, Unit>(
            ctx, time, Options.Create(new TimeoutOptions()), logger);

        var result = await behavior.HandleAsync(
            new ContractTotalTimedRequest(),
            (req, ct) => Task.FromResult(Unit.Value),
            CancellationToken.None);

        await behavior.HandleAsync(
            new ContractTotalTimedRequest(),
            (req, ct) => Task.FromResult(Unit.Value),
            CancellationToken.None);

        result.ShouldBe(Unit.Value);
        ctx.Outcome.ShouldBeNull();
        ctx.Metadata.ContainsKey("TimeoutSeconds").ShouldBeFalse();
        ctx.Metadata.ContainsKey("TimeoutDeadlineUtc").ShouldBeFalse();
        logger.Warnings.ShouldBe(1);
    }

    [TimedFact]
    public async Task ContractTotalTimeout_NoDeadline_FallsBackToPerAttemptGlobalDefault()
    {
        // The refusal declines the ATTRIBUTE, after which the job is effectively attribute-less —
        // so a configured PerAttempt global default still applies, exactly as it would to any other
        // unattributed job. Pins the combination the docs describe ("inert" means the Total policy,
        // not a timeout exemption).
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        var behavior = Build<ContractTotalFallbackRequest, Unit>(time, ctx, new TimeoutOptions { Default = TimeSpan.FromSeconds(1) });

        var handlerStarted = new TaskCompletionSource();
        var handlerTask = behavior.HandleAsync(
            new ContractTotalFallbackRequest(),
            async (req, ct) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return Unit.Value;
            },
            CancellationToken.None);

        await handlerStarted.Task;
        time.Advance(TimeSpan.FromSeconds(2));

        await handlerTask;

        ctx.Outcome.ShouldNotBeNull();
        ctx.Outcome!.State.ShouldBe(State.Deleted);
        ctx.Outcome.LogMessage.ShouldBe("Timed out after 1s");
        ctx.Metadata.ContainsKey("TimeoutSeconds").ShouldBeFalse();
    }

    private sealed class SagaShapedMessage : IMessage;

    private sealed class ExemptHandler : IPolicyExemptHandler;

    [Timeout(1)]
    private sealed class HandlerWithTimeout;

    [Timeout(1)]
    private sealed class ContractTimedRequest : IJob;

    [Timeout(30, Scope = TimeoutScope.Total)]
    private sealed class HandlerWithTotalTimeout;

    private sealed class HandlerTotalRequest : IJob;

    [Timeout(30, Scope = TimeoutScope.Total)]
    private sealed class ContractTotalTimedRequest : IJob;

    [Timeout(30, Scope = TimeoutScope.Total)]
    private sealed class ContractTotalFallbackRequest : IJob;

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }

    [TimedFact]
    public async Task NoTimeoutMetadata_PassesThrough()
    {
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        var behavior = Build(time, ctx);

        var result = await behavior.HandleAsync(
            new UnitRequest(),
            (req, ct) => Task.FromResult(Unit.Value),
            CancellationToken.None);

        result.ShouldBe(Unit.Value);
        ctx.Outcome.ShouldBeNull();
    }

    [TimedFact]
    public async Task DeleteMode_HandlerHonoursToken_SetsDeletedOutcome()
    {
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        ctx.Metadata["TimeoutSeconds"] = 1L;
        ctx.Metadata["TimeoutMode"] = (int)TimeoutMode.Delete;
        var behavior = Build(time, ctx);

        var handlerStarted = new TaskCompletionSource();
        var handlerTask = behavior.HandleAsync(
            new UnitRequest(),
            async (req, ct) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return Unit.Value;
            },
            CancellationToken.None);

        await handlerStarted.Task;
        time.Advance(TimeSpan.FromSeconds(2));

        var result = await handlerTask;

        result.ShouldBe(default(Unit));
        ctx.Outcome.ShouldNotBeNull();
        ctx.Outcome!.State.ShouldBe(State.Deleted);
        ctx.Outcome.LogMessage.ShouldBe("Timed out after 1s");
    }

    [TimedFact]
    public async Task FailMode_HandlerHonoursToken_ThrowsTimeoutException()
    {
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        ctx.Metadata["TimeoutSeconds"] = 2L;
        ctx.Metadata["TimeoutMode"] = (int)TimeoutMode.Fail;
        var behavior = Build(time, ctx);

        var handlerStarted = new TaskCompletionSource();
        var handlerTask = behavior.HandleAsync(
            new UnitRequest(),
            async (req, ct) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return Unit.Value;
            },
            CancellationToken.None);

        await handlerStarted.Task;
        time.Advance(TimeSpan.FromSeconds(3));

        var ex = await Should.ThrowAsync<TimeoutException>(handlerTask);
        ex.Message.ShouldContain("2s");
        ctx.Outcome.ShouldBeNull();
    }

    [TimedFact]
    public async Task HandlerCompletesBeforeTimeout_NoOutcome()
    {
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        ctx.Metadata["TimeoutSeconds"] = 60L;
        ctx.Metadata["TimeoutMode"] = (int)TimeoutMode.Delete;
        var behavior = Build(time, ctx);

        var result = await behavior.HandleAsync(
            new UnitRequest(),
            (req, ct) => Task.FromResult(Unit.Value),
            CancellationToken.None);

        result.ShouldBe(Unit.Value);
        ctx.Outcome.ShouldBeNull();
    }

    [TimedFact]
    public async Task TotalScope_DeadlinePast_FiresImmediately()
    {
        var publishMoment = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);
        var time = new FakeTimeProvider(new DateTimeOffset(publishMoment.AddSeconds(120), TimeSpan.Zero));
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        ctx.Metadata["TimeoutSeconds"] = 30L;
        ctx.Metadata["TimeoutMode"] = (int)TimeoutMode.Fail;
        ctx.Metadata["TimeoutScope"] = (int)TimeoutScope.Total;
        ctx.Metadata["TimeoutDeadlineUtc"] = publishMoment.AddSeconds(30);
        var behavior = Build(time, ctx);

        var handlerStarted = new TaskCompletionSource();
        var handlerTask = behavior.HandleAsync(
            new UnitRequest(),
            async (req, ct) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return Unit.Value;
            },
            CancellationToken.None);

        await handlerStarted.Task;

        // Zero-delay CTS fires synchronously on construction; no Advance needed.
        await Should.ThrowAsync<TimeoutException>(handlerTask);
    }

    [TimedFact]
    public async Task TotalScope_RemainingTimeUsed()
    {
        var publishMoment = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);

        // 5s have passed since publish; deadline = publish + 10s, so 5s remaining.
        var time = new FakeTimeProvider(new DateTimeOffset(publishMoment.AddSeconds(5), TimeSpan.Zero));
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        ctx.Metadata["TimeoutSeconds"] = 10L;
        ctx.Metadata["TimeoutMode"] = (int)TimeoutMode.Fail;
        ctx.Metadata["TimeoutScope"] = (int)TimeoutScope.Total;
        ctx.Metadata["TimeoutDeadlineUtc"] = publishMoment.AddSeconds(10);
        var behavior = Build(time, ctx);

        var handlerStarted = new TaskCompletionSource();
        var handlerTask = behavior.HandleAsync(
            new UnitRequest(),
            async (req, ct) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return Unit.Value;
            },
            CancellationToken.None);

        await handlerStarted.Task;

        time.Advance(TimeSpan.FromSeconds(4));

        handlerTask.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(2));

        await Should.ThrowAsync<TimeoutException>(handlerTask);
    }

    [TimedFact]
    public async Task WorkerShutdownDuringHandler_PropagatesOCE_NoTimeoutOutcome()
    {
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        ctx.Metadata["TimeoutSeconds"] = 60L;
        ctx.Metadata["TimeoutMode"] = (int)TimeoutMode.Delete;
        var behavior = Build(time, ctx);

        using var workerCts = new CancellationTokenSource();
        var handlerStarted = new TaskCompletionSource();
        var handlerTask = behavior.HandleAsync(
            new UnitRequest(),
            async (req, ct) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return Unit.Value;
            },
            workerCts.Token);

        await handlerStarted.Task;
        await workerCts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(handlerTask);
        ctx.Outcome.ShouldBeNull();
    }

    [TimedFact]
    public async Task TotalScope_WithoutDeadlineMetadata_FallsBackToPerAttemptBudget()
    {
        // Defensive: if a job is published with Scope = Total but no DeadlineUtc (only reachable
        // via raw Configure<ITimeoutMetadata> bypassing the WithTimeout extension AND the
        // publish behavior), the pipeline must fall through to the seconds budget rather than
        // crash on a null deadline. Same behaviour as PerAttempt: each attempt gets a fresh
        // `TimeoutSeconds`-long timer.
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        ctx.Metadata["TimeoutSeconds"] = 1L;
        ctx.Metadata["TimeoutMode"] = (int)TimeoutMode.Delete;
        ctx.Metadata["TimeoutScope"] = (int)TimeoutScope.Total;

        // intentionally omit TimeoutDeadlineUtc
        var behavior = Build(time, ctx);

        var handlerStarted = new TaskCompletionSource();
        var handlerTask = behavior.HandleAsync(
            new UnitRequest(),
            async (req, ct) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return Unit.Value;
            },
            CancellationToken.None);

        await handlerStarted.Task;
        time.Advance(TimeSpan.FromSeconds(2));

        await handlerTask;

        ctx.Outcome.ShouldNotBeNull();
        ctx.Outcome!.State.ShouldBe(State.Deleted);
        ctx.Outcome.LogMessage.ShouldBe("Timed out (deadline exceeded, 1s total budget)");
    }

    [TimedFact]
    public async Task WorkerShutdownConcurrentWithTimerFire_PropagatesOCE_NoTimeoutOutcome()
    {
        // The catch filter guards against the race where the worker is shutting down at the
        // exact moment a timer fires:
        //   when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        // If both are cancelled, the filter evaluates false and the OCE propagates — worker
        // shutdown wins, no spurious Deleted outcome. Job stays Processing so StaleJobRecovery
        // can re-enqueue it after restart.
        var time = new FakeTimeProvider();
        var ctx = new JobContext { JobId = Guid.NewGuid() };
        ctx.Metadata["TimeoutSeconds"] = 1L;
        ctx.Metadata["TimeoutMode"] = (int)TimeoutMode.Delete;
        var behavior = Build(time, ctx);

        using var workerCts = new CancellationTokenSource();
        var handlerStarted = new TaskCompletionSource();
        var handlerTask = behavior.HandleAsync(
            new UnitRequest(),
            async (req, ct) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
                return Unit.Value;
            },
            workerCts.Token);

        await handlerStarted.Task;

        // Fire BOTH triggers: the inner timer (via fake-time advance) AND the outer worker
        // cancellation. The filter sees cancellationToken.IsCancellationRequested == true, so
        // its guard fails — OCE propagates up. No timeout outcome is set.
        time.Advance(TimeSpan.FromSeconds(2));
        await workerCts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(handlerTask);
        ctx.Outcome.ShouldBeNull();
    }
}
