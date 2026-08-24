using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.CircuitBreaker;
using Warp.Core.Concurrency;
using Warp.Core.Handlers;
using Warp.Core.RateLimit;
using Warp.Core.Timeout;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

/// <summary>
/// Addon policy axis (supersedes #242): <c>[Timeout]</c> / <c>[Mutex]</c> / <c>[Semaphore]</c> /
/// <c>[RateLimit]</c> are legal on job/message handler classes; <c>AddWarp</c> now rejects (1) a handler
/// attribute on a shape whose execution path cannot honour it (stream handlers, in-memory request
/// handlers — the original #242 silent no-op, kept there), (2) the same policy FAMILY declared on both
/// axes (the publish-stamped contract value would silently shadow the handler one — includes the
/// cross-attribute Mutex+Semaphore case and Retry/CircuitBreaker), and (3) Total-scoped handler
/// timeouts in either form. The "offending" impl types here deliberately do NOT implement a handler
/// interface — the mediator source generator discovers (and would register / fail to compile on) any
/// real handler type in the assembly. They are registered under a handler service type manually; the
/// validator scans the impl type's attributes, which is exactly the production code path.
/// </summary>
[Trait("Category", "NoDb")]
public class AddonAttributeHandlerValidationTests
{
    [TimedFact]
    public void Validate_MutexAttributeOnJobHandler_DoesNotThrow() => AssertAllowed(typeof(MutexOnHandler));

    [TimedFact]
    public void Validate_SemaphoreAttributeOnJobHandler_DoesNotThrow() => AssertAllowed(typeof(SemaphoreOnHandler));

    [TimedFact]
    public void Validate_TimeoutAttributeOnJobHandler_DoesNotThrow() => AssertAllowed(typeof(TimeoutOnHandler));

    [TimedFact]
    public void Validate_RateLimitAttributeOnJobHandler_DoesNotThrow() => AssertAllowed(typeof(RateLimitOnHandler));

    [TimedFact]
    public void Validate_MutexAttributeOnMessageHandler_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddTransient(typeof(IMessageHandler<PolicyMessage>), typeof(MutexOnHandler));

        Should.NotThrow(() => ServiceConfiguration.ValidateAddonAttributesOnHandlers(services));
    }

    [TimedFact]
    public void Validate_SameFamilyOnBothAxes_Throws()
    {
        // MutexAttributeRequest carries [Mutex] on the contract; MutexOnHandler carries it on the handler.
        var ex = AssertRejectedFor<MutexAttributeRequest>(typeof(MutexOnHandler));

        ex.Message.ShouldContain(nameof(MutexAttributeRequest));
        ex.Message.ShouldContain(nameof(MutexOnHandler));
    }

    [TimedFact]
    public void Validate_MutexOnContractSemaphoreOnHandler_Throws()
    {
        // Cross-attribute, same family: both write the IConcurrencyMetadata slot, so the contract [Mutex]
        // would silently shadow the handler [Semaphore]. Must be rejected as one conflict.
        var ex = AssertRejectedFor<MutexAttributeRequest>(typeof(SemaphoreOnHandler));

        ex.Message.ShouldContain("Mutex/Semaphore");
    }

    [TimedFact]
    public void Validate_RetryOnBothAxes_Throws()
    {
        var ex = AssertRejectedFor<RetryRequest>(typeof(RetryOnHandler));

        ex.Message.ShouldContain("Retry");
    }

    [TimedFact]
    public void Validate_CircuitBreakerOnBothAxes_Throws()
    {
        var ex = AssertRejectedFor<BreakerRequest>(typeof(BreakerOnHandler));

        ex.Message.ShouldContain("CircuitBreaker");
    }

    [TimedFact]
    public void Validate_MutexOnStreamRequestHandler_Throws()
    {
        var services = new ServiceCollection();
        services.AddTransient(typeof(IStreamRequestHandler<PlainStreamRequest, string>), typeof(MutexOnHandler));

        var ex = Should.Throw<InvalidOperationException>(
            () => ServiceConfiguration.ValidateAddonAttributesOnHandlers(services));

        ex.Message.ShouldContain("Mutex");
        ex.Message.ShouldContain(nameof(MutexOnHandler));
    }

    [TimedFact]
    public void Validate_MutexOnInMemoryRequestHandler_Throws()
    {
        var services = new ServiceCollection();
        services.AddTransient(typeof(IRequestHandler<PlainRequest, string>), typeof(MutexOnHandler));

        var ex = Should.Throw<InvalidOperationException>(
            () => ServiceConfiguration.ValidateAddonAttributesOnHandlers(services));

        ex.Message.ShouldContain("Mutex");
    }

    [TimedFact]
    public void Validate_RetryOnInMemoryRequestHandler_DoesNotThrow()
    {
        // Retry/CircuitBreaker were always tolerated (dead code) on non-job handlers; rejecting them there
        // now would be an unspecced breaking change. Only the both-axes conflict is new for them.
        var services = new ServiceCollection();
        services.AddTransient(typeof(IRequestHandler<PlainRequest, string>), typeof(RetryOnHandler));

        Should.NotThrow(() => ServiceConfiguration.ValidateAddonAttributesOnHandlers(services));
    }

    [TimedFact]
    public void Validate_TotalScopeTimeoutOnHandler_Throws()
    {
        var services = new ServiceCollection();
        services.AddTransient(typeof(IJobHandler<ThrowExceptionRequest>), typeof(TotalTimeoutOnHandler));

        var ex = Should.Throw<InvalidOperationException>(
            () => ServiceConfiguration.ValidateAddonAttributesOnHandlers(services));

        ex.Message.ShouldContain("Total");
    }

    [TimedFact]
    public void Validate_HandlerTimeoutUnderTotalScopedGlobalDefault_Throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TimeoutStartupDefaults(HasDefault: true, TimeoutScope.Total));
        services.AddTransient(typeof(IJobHandler<ThrowExceptionRequest>), typeof(TimeoutOnHandler));

        var ex = Should.Throw<InvalidOperationException>(
            () => ServiceConfiguration.ValidateAddonAttributesOnHandlers(services));

        ex.Message.ShouldContain("Total");
        ex.Message.ShouldContain(nameof(TimeoutOnHandler));
    }

    [TimedFact]
    public void Validate_HandlerTimeoutUnderPerAttemptGlobalDefault_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TimeoutStartupDefaults(HasDefault: true, TimeoutScope.PerAttempt));
        services.AddTransient(typeof(IJobHandler<ThrowExceptionRequest>), typeof(TimeoutOnHandler));

        Should.NotThrow(() => ServiceConfiguration.ValidateAddonAttributesOnHandlers(services));
    }

    [TimedFact]
    public void Validate_AttributeOnRequestTypeWithCleanHandler_DoesNotThrow()
    {
        // Contract-axis placement: [Mutex] on the request (MutexAttributeRequest), handler carries nothing.
        var services = new ServiceCollection();
        services.AddTransient<IJobHandler<MutexAttributeRequest>, MutexAttributeCommand>();

        Should.NotThrow(() => ServiceConfiguration.ValidateAddonAttributesOnHandlers(services));
    }

    [TimedFact]
    public void Validate_SelfHandlingJobWithAttribute_DoesNotThrow()
    {
        // A self-handling job (registered as IJobHandler<Self> → Self) carries the attribute on the request
        // axis, which happens to be the same type as the handler. That is correct placement — the validator
        // must NOT reject it (handler type == request type).
        var services = new ServiceCollection();
        services.AddTransient(typeof(IJobHandler<>).MakeGenericType(typeof(SelfHandlingWithMutex)), typeof(SelfHandlingWithMutex));

        Should.NotThrow(() => ServiceConfiguration.ValidateAddonAttributesOnHandlers(services));
    }

    private static void AssertAllowed(Type handlerImpl)
    {
        // ThrowExceptionRequest carries no policy attribute, so the handler-axis declaration is conflict-free.
        var services = new ServiceCollection();
        services.AddTransient(typeof(IJobHandler<ThrowExceptionRequest>), handlerImpl);

        Should.NotThrow(() => ServiceConfiguration.ValidateAddonAttributesOnHandlers(services));
    }

    private static InvalidOperationException AssertRejectedFor<TRequest>(Type handlerImpl)
        where TRequest : IJob
    {
        var services = new ServiceCollection();
        services.AddTransient(typeof(IJobHandler<TRequest>), handlerImpl);

        return Should.Throw<InvalidOperationException>(
            () => ServiceConfiguration.ValidateAddonAttributesOnHandlers(services));
    }

    // Plain attributed classes (NOT handler implementations) — the generator ignores them, so they never
    // register globally and never poison other AddWarp-based tests.
    [Mutex("addon-attr-test")]
    private sealed class MutexOnHandler;

    [Semaphore("addon-attr-test", 3)]
    private sealed class SemaphoreOnHandler;

    [Timeout(30)]
    private sealed class TimeoutOnHandler;

    [Timeout(30, Scope = TimeoutScope.Total)]
    private sealed class TotalTimeoutOnHandler;

    [RateLimit("addon-attr-test", count: 1, perSeconds: 60)]
    private sealed class RateLimitOnHandler;

    [Retry(3)]
    private sealed class RetryOnHandler;

    [CircuitBreaker(Threshold = 3, DurationSeconds = 30)]
    private sealed class BreakerOnHandler;

    [Retry(2)]
    private sealed class RetryRequest : IJob;

    [CircuitBreaker(Threshold = 2, DurationSeconds = 30)]
    private sealed class BreakerRequest : IJob;

    private sealed class PlainRequest : IRequest<string>;

    private sealed class PlainStreamRequest : IStreamRequest<string>;

    private sealed class PolicyMessage : IMessage;

    // Registered as IJobHandler<SelfHandlingWithMutex> → itself: handler type == request type, so [Mutex]
    // here is correct request-axis placement and must not be rejected. Implements IJob to satisfy the
    // IJobHandler<in T> where-T:IJob constraint; private+nested keeps it off the source generator.
    [Mutex("addon-attr-test")]
    private sealed class SelfHandlingWithMutex : IJob;
}
