using Shouldly;
using Warp.Core.Concurrency;

namespace Warp.Tests.Features.Concurrency;

[Trait("Category", "NoDb")]
public class ConcurrencyLocalHoldsTests
{
    [TimedFact]
    public void IsSaturated_NothingHeld_False()
    {
        new ConcurrencyLocalHolds().IsSaturated("k", 1).ShouldBeFalse();
    }

    [TimedFact]
    public void IsSaturated_HeldUpToLimit_True()
    {
        var holds = new ConcurrencyLocalHolds();
        holds.Enter("k");
        holds.Enter("k");

        holds.IsSaturated("k", 2).ShouldBeTrue();
        holds.IsSaturated("k", 3).ShouldBeFalse();
    }

    [TimedFact]
    public void Exit_ReleasesOneHold_AndForgetsKeyAtZero()
    {
        var holds = new ConcurrencyLocalHolds();
        holds.Enter("k");
        holds.Enter("k");

        holds.Exit("k");
        holds.HeldCount("k").ShouldBe(1);

        holds.Exit("k");
        holds.HeldCount("k").ShouldBe(0);
        holds.IsSaturated("k", 1).ShouldBeFalse();
    }

    [TimedFact]
    public void Exit_NothingHeld_NeverGoesNegative()
    {
        var holds = new ConcurrencyLocalHolds();

        holds.Exit("k");
        holds.Enter("k");

        holds.HeldCount("k").ShouldBe(1);
    }
}
