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
        holds.Enter("k", 2);
        holds.Enter("k", 2);

        holds.IsSaturated("k", 2).ShouldBeTrue();
    }

    [TimedFact]
    public void IsSaturated_SemaphoreSlotsHeld_DoesNotSaturateMutexOnSameKey()
    {
        // On PostgreSQL a mutex locks "k" and a semaphore of 2 locks "k:0"/"k:1" — independent caps (§8.6).
        // Holds taken by a limit-2 request must never refuse a limit-1 request on the same name.
        var holds = new ConcurrencyLocalHolds();
        holds.Enter("k", 2);
        holds.Enter("k", 2);

        holds.IsSaturated("k", 1).ShouldBeFalse();
    }

    [TimedFact]
    public void IsSaturated_LimitChangedMidRun_DoesNotCountHoldsTakenUnderTheOldLimit()
    {
        var holds = new ConcurrencyLocalHolds();
        holds.Enter("k", 3);
        holds.Enter("k", 3);

        holds.IsSaturated("k", 2).ShouldBeFalse("holds under limit 3 say nothing about a limit-2 request — it goes to the database");
    }

    [TimedFact]
    public void Exit_ReleasesOneHold_AndForgetsKeyAtZero()
    {
        var holds = new ConcurrencyLocalHolds();
        holds.Enter("k", 2);
        holds.Enter("k", 2);

        holds.Exit("k", 2);
        holds.HeldCount("k", 2).ShouldBe(1);

        holds.Exit("k", 2);
        holds.HeldCount("k", 2).ShouldBe(0);
        holds.IsSaturated("k", 2).ShouldBeFalse();
    }

    [TimedFact]
    public void Exit_NothingHeld_NeverGoesNegative()
    {
        var holds = new ConcurrencyLocalHolds();

        holds.Exit("k", 1);
        holds.Enter("k", 1);

        holds.HeldCount("k", 1).ShouldBe(1);
    }
}
