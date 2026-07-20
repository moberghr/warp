using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Adapters;

/// <summary>
/// NoDb coverage for protocol-agnostic adapter Core concerns: case-sensitive adapter identity (F5), the
/// single-<c>TContext</c>-per-process guard on <c>AddAdapters()</c> (F6), the atomic cardinality bound
/// (F8), and the counter-key build↔parse round-trip (F9).
/// </summary>
[Trait("Category", "NoDb")]
public class AdapterCoreTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public void Registry_CaseVariantNames_AreIndependentAdapters()
    {
        // Identity is ordinal (case-SENSITIVE) so memory agrees with the case-sensitive DB rows / counter
        // keys — "Stripe" and "stripe" resolve to their own registrations, never collapse to one (F5).
        var registry = new AdapterRegistry();
        registry.Register("Stripe", new WarpAdapterOptions { GroupLabel = "Upper" });
        registry.Register("stripe", new WarpAdapterOptions { GroupLabel = "Lower" });

        registry.ResolveGroupLabel("Stripe").ShouldBe("Upper");
        registry.ResolveGroupLabel("stripe").ShouldBe("Lower");
    }

    [TimedFact]
    public void AddAdapters_SameContextTwice_IsIdempotent()
    {
        var services = new ServiceCollection();
        new WarpBuilder<TestContext>(services).AddAdapters();

        Should.NotThrow(() => new WarpBuilder<TestContext>(services).AddAdapters());
    }

    [TimedFact]
    public void AddAdapters_DifferentContext_Throws()
    {
        // Two AddWarp<TContext>() builders each calling AddAdapters() would silently bind the whole recording
        // pipeline to the first context and drop the second (TryAddSingleton keeps the first). Reject it (F6).
        var services = new ServiceCollection();
        new WarpBuilder<TestContext>(services).AddAdapters();

        var ex = Should.Throw<InvalidOperationException>(() => new WarpBuilder<SecondaryContext>(services).AddAdapters());
        ex.Message.ShouldContain(nameof(TestContext));
        ex.Message.ShouldContain(nameof(SecondaryContext));
    }

    [TimedFact]
    public async Task CardinalityGuard_ConcurrentNewValuesAtCap_AdmitsExactlyOne()
    {
        // Fill to cap-1 (4 of 5), then race two NEW values through the count-then-add. The atomic bound
        // admits exactly one; the other collapses to {other}. Without the lock both could overshoot (F8).
        var guard = new CardinalityGuard("vendor", "group", maxDistinct: 5, NullLogger.Instance);
        for (var i = 0; i < 4; i++)
        {
            guard.Map($"seed-{i}");
        }

        var barrier = new BarrierSignal();
        var one = MapUnderBarrier(guard, "new-a", barrier);
        var two = MapUnderBarrier(guard, "new-b", barrier);

        await barrier.Running.WaitAsync(Ct);
        await barrier.Running.WaitAsync(Ct);
        barrier.CanFinish.Release(2);

        var results = await Task.WhenAll(one, two);

        results.Count(x => string.Equals(x, CardinalityGuard.OtherValue, StringComparison.Ordinal)).ShouldBe(1);
        results.Count(x => !string.Equals(x, CardinalityGuard.OtherValue, StringComparison.Ordinal)).ShouldBe(1);
    }

    [TimedFact]
    public void CounterKey_BuildThenParse_RoundTripsAllThreeShapes()
    {
        // Builder and parser live in the same type; this pins their agreement so a format change to one
        // that is not mirrored in the other fails the build, never silently zeroes the dashboard (F9).
        AdapterCounterKeys.TryParse(AdapterCounterKeys.Total("vendor", "success"), out var total).ShouldBeTrue();
        total.Adapter.ShouldBe("vendor");
        total.Dimension.ShouldBe(AdapterStatDimension.Total);
        total.Outcome.ShouldBe("success");

        AdapterCounterKeys.TryParse(AdapterCounterKeys.Operation("vendor", "GetOrders", "failed"), out var op).ShouldBeTrue();
        op.Dimension.ShouldBe(AdapterStatDimension.Operation);
        op.Value.ShouldBe("GetOrders");
        op.Outcome.ShouldBe("failed");

        AdapterCounterKeys.TryParse(AdapterCounterKeys.Group("vendor", "shop-eu", "throttled"), out var grp).ShouldBeTrue();
        grp.Dimension.ShouldBe(AdapterStatDimension.Group);
        grp.Value.ShouldBe("shop-eu");
        grp.Outcome.ShouldBe("throttled");
    }

    private static Task<string> MapUnderBarrier(CardinalityGuard guard, string value, BarrierSignal barrier)
        => Task.Run(async () =>
        {
            barrier.Running.Release();
            await barrier.CanFinish.WaitAsync(Ct);

            return guard.Map(value);
        });

    private sealed class SecondaryContext : DbContext;
}
