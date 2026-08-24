using Shouldly;
using Warp.Core.Concurrency;
using Warp.Core.Handlers;
using Warp.Core.Timeout;

namespace Warp.Tests.Core;

[Trait("Category", "NoDb")]
public class AddonAttributeResolverTests
{
    [TimedFact]
    public void Resolve_HandlerAttribute_WinsOverContract()
    {
        // Both-axes declarations are rejected at startup, so this ordering is unobservable in production —
        // the test pins the resolver as a pure function regardless.
        var attr = AddonAttributeResolver.Resolve<MutexAttribute>(typeof(HandlerWithMutex), typeof(ContractWithMutex));

        attr.ShouldNotBeNull();
        attr.Key.ShouldBe("handler-key");
    }

    [TimedFact]
    public void Resolve_NoHandlerAttribute_FallsBackToContract()
    {
        var attr = AddonAttributeResolver.Resolve<MutexAttribute>(typeof(BareHandler), typeof(ContractWithMutex));

        attr.ShouldNotBeNull();
        attr.Key.ShouldBe("contract-key");
    }

    [TimedFact]
    public void Resolve_NullHandlerType_ReadsContract()
    {
        var attr = AddonAttributeResolver.Resolve<MutexAttribute>(null, typeof(ContractWithMutex));

        attr.ShouldNotBeNull();
        attr.Key.ShouldBe("contract-key");
    }

    [TimedFact]
    public void Resolve_NoAttributeAnywhere_ReturnsNull()
    {
        var attr = AddonAttributeResolver.Resolve<TimeoutAttribute>(typeof(BareHandler), typeof(BareContract));

        attr.ShouldBeNull();
    }

    [TimedFact]
    public void Resolve_SecondCall_ReturnsCachedInstance()
    {
        // GetCustomAttribute materializes a NEW attribute instance on every reflection call — reference
        // equality across two resolves proves the reflection ran once and the cache answered the second
        // (SC10: zero attribute resolution per attempt after the first per (process, type)).
        var first = AddonAttributeResolver.Resolve<MutexAttribute>(typeof(HandlerWithMutex), typeof(BareContract));
        var second = AddonAttributeResolver.Resolve<MutexAttribute>(typeof(HandlerWithMutex), typeof(BareContract));

        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Mutex("handler-key")]
    private sealed class HandlerWithMutex;

    [Mutex("contract-key")]
    private sealed class ContractWithMutex;

    private sealed class BareHandler;

    private sealed class BareContract;
}
