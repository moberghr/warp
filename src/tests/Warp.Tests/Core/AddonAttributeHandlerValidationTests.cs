using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Handlers;
using Warp.Core.RateLimit;
using Warp.Core.Timeout;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

/// <summary>
/// #242: <c>[Timeout]</c> / <c>[Mutex]</c> / <c>[Semaphore]</c> / <c>[RateLimit]</c> are read only from the
/// request/job type; placed on a handler class they compile but are a silent no-op, so <c>AddWarp</c> now
/// rejects them loudly at registration (<see cref="ServiceConfiguration.ValidateAddonAttributesOnHandlers"/>).
/// The "offending" impl types here deliberately do NOT implement a handler interface — the mediator source
/// generator discovers (and would register / fail to compile on) any real handler type in the assembly, so
/// a genuine offending handler cannot live here. They are registered under a handler service type manually;
/// the validator scans the impl type's attributes, which is exactly the production code path.
/// </summary>
[Trait("Category", "NoDb")]
public class AddonAttributeHandlerValidationTests
{
    [TimedFact]
    public void Validate_MutexAttributeOnHandler_Throws() => AssertRejected(typeof(MutexOnHandler), "Mutex");

    [TimedFact]
    public void Validate_SemaphoreAttributeOnHandler_Throws() => AssertRejected(typeof(SemaphoreOnHandler), "Semaphore");

    [TimedFact]
    public void Validate_TimeoutAttributeOnHandler_Throws() => AssertRejected(typeof(TimeoutOnHandler), "Timeout");

    [TimedFact]
    public void Validate_RateLimitAttributeOnHandler_Throws() => AssertRejected(typeof(RateLimitOnHandler), "RateLimit");

    [TimedFact]
    public void Validate_AttributeOnRequestTypeWithCleanHandler_DoesNotThrow()
    {
        // Correct placement: [Mutex] on the request (MutexAttributeRequest), handler (MutexAttributeCommand)
        // carries nothing. The validator scans the handler impl, finds no addon attribute → allowed.
        var services = new ServiceCollection();
        services.AddTransient<IJobHandler<MutexAttributeRequest>, MutexAttributeCommand>();

        Should.NotThrow(() => ServiceConfiguration.ValidateAddonAttributesOnHandlers(services));
    }

    private static void AssertRejected(Type offendingImpl, string expectedName)
    {
        var services = new ServiceCollection();
        services.AddTransient(typeof(IJobHandler<ThrowExceptionRequest>), offendingImpl);

        var ex = Should.Throw<InvalidOperationException>(
            () => ServiceConfiguration.ValidateAddonAttributesOnHandlers(services));

        ex.Message.ShouldContain(expectedName);
        ex.Message.ShouldContain(offendingImpl.Name);
    }

    // Plain attributed classes (NOT handler implementations) — the generator ignores them, so they never
    // register globally and never poison other AddWarp-based tests.
    [Mutex("addon-attr-test")]
    private sealed class MutexOnHandler;

    [Semaphore("addon-attr-test", 3)]
    private sealed class SemaphoreOnHandler;

    [Timeout(30)]
    private sealed class TimeoutOnHandler;

    [RateLimit("addon-attr-test", count: 1, perSeconds: 60)]
    private sealed class RateLimitOnHandler;
}
