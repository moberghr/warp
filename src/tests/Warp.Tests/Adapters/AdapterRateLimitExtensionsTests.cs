using Shouldly;
using Warp.Core.Adapters;

namespace Warp.Tests.Adapters;

/// <summary>
/// Unit coverage for the chain-walking rate-limit helpers (#284). Pure — no DI, no HTTP. The point of the
/// helpers is that they keep working when a client wraps the refusal (Refit's <c>ApiRequestException</c>),
/// so every test here wraps to some depth.
/// </summary>
[Trait("Category", "NoDb")]
public class AdapterRateLimitExtensionsTests
{
    [TimedFact]
    public void IsAdapterRateLimited_DirectException_IsTrue()
        => new AdapterRateLimitedException("limited").IsAdapterRateLimited().ShouldBeTrue();

    [TimedFact]
    public void IsAdapterRateLimited_WrappedTwoDeep_IsTrue()
    {
        var wrapped = new InvalidOperationException(
            "outer",
            new HttpRequestException("inner", new AdapterRateLimitedException("limited")));

        wrapped.IsAdapterRateLimited().ShouldBeTrue();
    }

    [TimedFact]
    public void IsAdapterRateLimited_InsideNonFirstAggregateBranch_IsTrue()
    {
        // AggregateException.InnerException is only the FIRST branch, so a naive walk misses a refusal
        // raised by one of several concurrent sends.
        var aggregate = new AggregateException(
            new InvalidOperationException("first"),
            new AdapterRateLimitedException("limited"));

        aggregate.IsAdapterRateLimited().ShouldBeTrue();
    }

    [TimedFact]
    public void IsAdapterRateLimited_UnrelatedChain_IsFalse()
        => new InvalidOperationException("outer", new HttpRequestException("inner")).IsAdapterRateLimited().ShouldBeFalse();

    [TimedFact]
    public void IsAdapterRateLimited_Null_IsFalse()
        => ((Exception?)null).IsAdapterRateLimited().ShouldBeFalse();

    [TimedFact]
    public void GetAdapterRetryAfter_WrappedRefusal_ReturnsTheComputedWait()
    {
        var wrapped = new InvalidOperationException(
            "outer",
            new AdapterRateLimitedException("limited", TimeSpan.FromSeconds(7)));

        wrapped.GetAdapterRetryAfter().ShouldBe(TimeSpan.FromSeconds(7));
    }

    [TimedFact]
    public void GetAdapterRetryAfter_RefusalWithoutTiming_IsNull()
        => new AdapterRateLimitedException("limited").GetAdapterRetryAfter().ShouldBeNull();

    [TimedFact]
    public void GetAdapterRetryAfter_UnrelatedChain_IsNull()
        => new InvalidOperationException("outer").GetAdapterRetryAfter().ShouldBeNull();

    [TimedFact]
    public void FindAdapterRateLimited_WrappedRefusal_ReturnsTheRefusalItself()
    {
        var refusal = new AdapterRateLimitedException("limited", TimeSpan.FromSeconds(1));

        new InvalidOperationException("outer", refusal).FindAdapterRateLimited().ShouldBeSameAs(refusal);
    }
}
