using Shouldly;
using Warp.Core;
using Warp.Core.CircuitBreaker;
using Warp.Core.Concurrency;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Policies;
using Warp.Core.RateLimit;
using Warp.Core.Retry;
using Warp.Core.Timeout;

namespace Warp.Tests.Core;

/// <summary>
/// The policy axis (§8.8) as a pure function: rung-major resolution, stamp-once, and the Total-timeout
/// carve-out that cannot be resolved at execution.
/// </summary>
[Trait("Category", "NoDb")]
public class PolicyResolverTests
{
    [TimedFact]
    public void StampConcurrency_HandlerDeclaration_WinsOverContract()
    {
        var meta = Stamp<IConcurrencyMetadata>(typeof(HandlerWithMutex), typeof(ContractWithMutex));

        meta.ConcurrencyKey.ShouldBe("handler-key");
    }

    [TimedFact]
    public void StampConcurrency_HandlerSemaphore_WinsOverContractMutex()
    {
        // Rung-major: [Mutex] and [Semaphore] share one metadata slot, so searching the whole family on
        // the handler before the contract is what stops the contract shadowing the handler.
        var meta = Stamp<IConcurrencyMetadata>(typeof(HandlerWithSemaphore), typeof(ContractWithMutex));

        meta.ConcurrencyKey.ShouldBe("handler-sema");
        meta.ConcurrencyLimit.ShouldBe(4);
        meta.ConcurrencyMode.ShouldBe(ConcurrencyMode.Wait);
    }

    [TimedFact]
    public void StampConcurrency_TypeCarryingBothAttributes_MutexWins()
    {
        // Nonsense to declare, but it must be deterministic rather than order-dependent.
        var meta = Stamp<IConcurrencyMetadata>(typeof(BareHandler), typeof(ContractWithBothAttributes));

        meta.ConcurrencyKey.ShouldBe("both-mutex");
        meta.ConcurrencyLimit.ShouldBe(1);
    }

    [TimedFact]
    public void StampConcurrency_BareHandler_FallsBackToContract()
    {
        var meta = Stamp<IConcurrencyMetadata>(typeof(BareHandler), typeof(ContractWithMutex));

        meta.ConcurrencyKey.ShouldBe("contract-key");
        meta.ConcurrencyLimit.ShouldBe(1);
    }

    [TimedFact]
    public void StampConcurrency_NoHandlerType_ReadsContract()
    {
        // A directly-staged job (recurring firing) whose handler has not been discovered yet.
        var meta = Stamp<IConcurrencyMetadata>(null, typeof(ContractWithMutex));

        meta.ConcurrencyKey.ShouldBe("contract-key");
    }

    [TimedFact]
    public void StampConcurrency_NothingDeclared_StampsNothing()
    {
        var context = new JobContext();
        PolicyResolver.StampConcurrency(context.GetMetadata<IConcurrencyMetadata>(), typeof(BareHandler), typeof(BareContract));

        context.Metadata.ShouldBeEmpty();
    }

    [TimedFact]
    public void StampConcurrency_MetadataAlreadyPresent_IsNotReResolved()
    {
        // Attributes are never consulted again once the row carries a value, so policy cannot drift.
        var context = new JobContext { HandlerType = typeof(HandlerWithMutex) };
        var meta = context.GetMetadata<IConcurrencyMetadata>();
        meta.ConcurrencyKey = "explicit";

        PolicyResolver.StampConcurrency(meta, context.HandlerType, typeof(ContractWithMutex));

        meta.ConcurrencyKey.ShouldBe("explicit");
    }

    [TimedFact]
    public void StampRateLimit_HandlerDeclaration_StampsEveryField()
    {
        // The execution gate needs Key, Count AND WindowSeconds — a partial stamp silently no-ops.
        var meta = Stamp<IRateLimitMetadata>(typeof(HandlerWithRateLimit), typeof(ContractWithRateLimit));

        meta.RateLimitKey.ShouldBe("handler-rl");
        meta.RateLimitCount.ShouldBe(2);
        meta.RateLimitWindowSeconds.ShouldBe(60);
        meta.RateLimitMode.ShouldBe(RateLimitMode.Wait);
        meta.RateLimitStyle.ShouldBe(RateLimitStyle.Sliding);
    }

    [TimedFact]
    public void StampRetry_HandlerDeclaration_WinsOverContract()
    {
        var meta = Stamp<IRetryMetadata>(typeof(HandlerWithRetry), typeof(ContractWithRetry));

        meta.MaxRetries.ShouldBe(7);
    }

    [TimedFact]
    public void StampRetry_ZeroMaxRetries_StampsZeroNotUnset()
    {
        // Distinct from an absent policy, which falls through to the global RetryOptions.
        var meta = Stamp<IRetryMetadata>(typeof(HandlerWithZeroRetry), typeof(BareContract));

        meta.MaxRetries.ShouldBe(0);
    }

    [TimedFact]
    public void StampRetry_AttributeWithoutDelays_LeavesDelaysUnset()
    {
        // Delays stay null so the global schedule still applies — the attribute only overrides the count.
        var context = new JobContext();
        PolicyResolver.StampRetry(context.GetMetadata<IRetryMetadata>(), typeof(HandlerWithRetry), typeof(BareContract));

        context.Metadata.ContainsKey(nameof(IRetryMetadata.RetryDelays)).ShouldBeFalse();
    }

    [TimedFact]
    public void StampRetry_AttributeWithDelays_StampsBoth()
    {
        var meta = Stamp<IRetryMetadata>(typeof(HandlerWithRetryDelays), typeof(BareContract));

        meta.MaxRetries.ShouldBe(3);
        meta.RetryDelays.ShouldBe([7, 9]);
    }

    [TimedFact]
    public void StampRetry_MaxRetriesFromElsewhere_StillStampsAttributeDelays()
    {
        // WithRetry(5) at publish sets MaxRetries only. The attribute's schedule must still apply —
        // the two fields are independent rungs, or a declared [7, 9] silently becomes the global default.
        var context = new JobContext();
        var meta = context.GetMetadata<IRetryMetadata>();
        meta.MaxRetries = 5;

        PolicyResolver.StampRetry(meta, typeof(HandlerWithRetryDelays), typeof(BareContract));

        meta.MaxRetries.ShouldBe(5);
        meta.RetryDelays.ShouldBe([7, 9]);
    }

    [TimedFact]
    public void StampRetry_ExplicitDelays_AreNotOverwrittenByAttribute()
    {
        // Explicit publish metadata outranks every attribute — for Delays as much as for MaxRetries.
        var context = new JobContext();
        var meta = context.GetMetadata<IRetryMetadata>();
        meta.RetryDelays = [1];

        PolicyResolver.StampRetry(meta, typeof(HandlerWithRetryDelays), typeof(BareContract));

        meta.MaxRetries.ShouldBe(3);
        meta.RetryDelays.ShouldBe([1]);
    }

    [TimedFact]
    public void StampRetry_ExplicitMetadata_IsNotOverwritten()
    {
        var context = new JobContext();
        var meta = context.GetMetadata<IRetryMetadata>();
        meta.MaxRetries = 1;

        PolicyResolver.StampRetry(meta, typeof(HandlerWithRetry), typeof(ContractWithRetry));

        meta.MaxRetries.ShouldBe(1);
    }

    [TimedFact]
    public void StampTimeout_HandlerPerAttempt_WinsOverContract()
    {
        var context = new JobContext();
        var meta = context.GetMetadata<ITimeoutMetadata>();

        PolicyResolver.StampTimeout(meta, typeof(HandlerWithTimeout), typeof(ContractWithTimeout))
            .ShouldBe(TimeoutStamp.Stamped);

        meta.TimeoutSeconds.ShouldBe(11);
        meta.TimeoutScope.ShouldBe(TimeoutScope.PerAttempt);
    }

    [TimedFact]
    public void StampTimeout_TotalOnHandler_IsInertNotThrown()
    {
        // WARP002 catches this at build time. The runtime backstop must NOT throw: the resolver runs
        // inside the pipeline, where an outer Retry treats the exception as a handler failure and burns
        // the whole retry budget on a static misconfiguration. Inert + warn-once, like the other shapes.
        var context = new JobContext();
        var meta = context.GetMetadata<ITimeoutMetadata>();

        PolicyResolver.StampTimeout(meta, typeof(HandlerWithTotalTimeout), typeof(BareContract))
            .ShouldBe(TimeoutStamp.TotalOnHandler);

        meta.TimeoutSeconds.ShouldBeNull();
    }

    [TimedFact]
    public void StampTimeout_ContractTotalWithoutDeadline_RefusesRatherThanInventingOne()
    {
        // Computing one now would measure from first pickup, not enqueue — a different meaning.
        var context = new JobContext();
        var meta = context.GetMetadata<ITimeoutMetadata>();

        PolicyResolver.StampTimeout(meta, typeof(BareHandler), typeof(ContractWithTotalTimeout))
            .ShouldBe(TimeoutStamp.TotalWithoutDeadline);

        meta.TimeoutSeconds.ShouldBeNull();
    }

    [TimedFact]
    public void StampTimeout_ContractTotalWithDeadline_IsStamped()
    {
        var context = new JobContext();
        var meta = context.GetMetadata<ITimeoutMetadata>();
        meta.TimeoutDeadlineUtc = DateTime.UtcNow.AddMinutes(5);

        PolicyResolver.StampTimeout(meta, typeof(BareHandler), typeof(ContractWithTotalTimeout))
            .ShouldBe(TimeoutStamp.Stamped);

        meta.TimeoutScope.ShouldBe(TimeoutScope.Total);
    }

    [TimedFact]
    public void StampTimeout_AlreadyStamped_ReportsAlreadyResolved()
    {
        var context = new JobContext();
        var meta = context.GetMetadata<ITimeoutMetadata>();
        meta.TimeoutSeconds = 3;

        PolicyResolver.StampTimeout(meta, typeof(HandlerWithTimeout), typeof(ContractWithTimeout))
            .ShouldBe(TimeoutStamp.AlreadyResolved);

        meta.TimeoutSeconds.ShouldBe(3);
    }

    [TimedFact]
    public void ResolveCircuitBreaker_HandlerDeclaration_WinsOverContract()
    {
        var attr = PolicyResolver.ResolveCircuitBreaker(typeof(HandlerWithBreaker), typeof(ContractWithBreaker));

        attr.ShouldNotBeNull();
        attr.Threshold.ShouldBe(9);
    }

    [TimedFact]
    public void IsDeclaredOnHandler_ReadsOnlyTheHandlerRung()
    {
        PolicyResolver.IsDeclaredOnHandler<TimeoutAttribute>(typeof(HandlerWithTimeout)).ShouldBeTrue();
        PolicyResolver.IsDeclaredOnHandler<TimeoutAttribute>(typeof(BareHandler)).ShouldBeFalse();
        PolicyResolver.IsDeclaredOnHandler<TimeoutAttribute>(null).ShouldBeFalse();
    }

    [TimedFact]
    public void Resolve_SecondCall_ReturnsCachedInstance()
    {
        // GetCustomAttribute materializes a new instance per call, so reference equality proves the cache.
        var first = PolicyResolver.ResolveCircuitBreaker(typeof(HandlerWithBreaker), typeof(BareContract));
        var second = PolicyResolver.ResolveCircuitBreaker(typeof(HandlerWithBreaker), typeof(BareContract));

        ReferenceEquals(first, second).ShouldBeTrue();
    }

    private static T Stamp<T>(Type? handlerType, Type requestType)
        where T : class, IJobMetadata
    {
        var context = new JobContext { HandlerType = handlerType };
        var meta = context.GetMetadata<T>();

        switch (meta)
        {
            case IConcurrencyMetadata concurrency:
                PolicyResolver.StampConcurrency(concurrency, handlerType, requestType);

                break;

            case IRateLimitMetadata rateLimit:
                PolicyResolver.StampRateLimit(rateLimit, handlerType, requestType);

                break;

            case IRetryMetadata retry:
                PolicyResolver.StampRetry(retry, handlerType, requestType);

                break;
        }

        return meta;
    }

    // Never handler implementations, so the generator ignores them and they poison no other test.
    [Mutex("handler-key")]
    private sealed class HandlerWithMutex;

    [Semaphore("handler-sema", 4)]
    private sealed class HandlerWithSemaphore;

    [Mutex("contract-key")]
    private sealed class ContractWithMutex;

    [Mutex("both-mutex")]
    [Semaphore("both-sema", 5)]
    private sealed class ContractWithBothAttributes;

    [RateLimit("handler-rl", count: 2, perSeconds: 60, Mode = RateLimitMode.Wait, Style = RateLimitStyle.Sliding)]
    private sealed class HandlerWithRateLimit;

    [RateLimit("contract-rl", count: 1, perSeconds: 3600)]
    private sealed class ContractWithRateLimit;

    [Retry(7)]
    private sealed class HandlerWithRetry;

    [Retry(0)]
    private sealed class HandlerWithZeroRetry;

    [Retry(3, Delays = [7, 9])]
    private sealed class HandlerWithRetryDelays;

    [Retry(2)]
    private sealed class ContractWithRetry;

    [Timeout(11)]
    private sealed class HandlerWithTimeout;

    [Timeout(30, Scope = TimeoutScope.Total)]
    private sealed class HandlerWithTotalTimeout;

    [Timeout(22)]
    private sealed class ContractWithTimeout;

    [Timeout(45, Scope = TimeoutScope.Total)]
    private sealed class ContractWithTotalTimeout;

    [CircuitBreaker(Threshold = 9, DurationSeconds = 30)]
    private sealed class HandlerWithBreaker;

    [CircuitBreaker(Threshold = 2, DurationSeconds = 30)]
    private sealed class ContractWithBreaker;

    private sealed class BareHandler;

    private sealed class BareContract;
}
