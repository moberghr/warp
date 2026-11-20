using Shouldly;
using Warp.Core.RateLimit;

namespace Warp.Tests.Features.RateLimit;

[Trait("Category", "NoDb")]
public class RateLimitGateTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 10, DateTimeKind.Utc);

    private static readonly RateLimitPolicy Policy = new(10, 60, RateLimitStyle.Fixed);

    [TimedFact]
    public void TryRefuse_UnknownKey_DefersToDatabase()
    {
        var gate = new RateLimitGate();

        gate.TryRefuse("k", Policy, RateLimitMode.Skip, Now, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void TryRefuse_SkipMode_BeforeClosedUntil_RefusesWithRecordedLimit()
    {
        var gate = new RateLimitGate();
        gate.RecordRejection("k", Policy, effectiveCount: 3, effectiveWindowSeconds: 30, Now, Now.AddSeconds(2), []);

        gate.TryRefuse("k", Policy, RateLimitMode.Skip, Now.AddSeconds(1), out var refusal).ShouldBeTrue();

        refusal.EffectiveCount.ShouldBe(3);
        refusal.EffectiveWindowSeconds.ShouldBe(30);
    }

    [TimedFact]
    public void TryRefuse_AtReopeningInstant_DefersToDatabase()
    {
        var gate = new RateLimitGate();
        gate.RecordRejection("k", Policy, 10, 60, Now, Now.AddSeconds(2), []);

        gate.TryRefuse("k", Policy, RateLimitMode.Skip, Now.AddSeconds(2), out _).ShouldBeFalse();
    }

    [TimedFact]
    public void TryRefuse_ReopeningFarAhead_TrustedForMaxClosedAgeOnly()
    {
        var gate = new RateLimitGate();
        gate.RecordRejection("k", Policy, 10, 60, Now, Now.AddHours(1), []);

        gate.TryRefuse("k", Policy, RateLimitMode.Skip, Now + RateLimitGate.MaxClosedAge - TimeSpan.FromTicks(1), out _).ShouldBeTrue();
        gate.TryRefuse("k", Policy, RateLimitMode.Skip, Now + RateLimitGate.MaxClosedAge, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void TryRefuse_DifferentDeclaredPolicy_DefersToDatabase()
    {
        var gate = new RateLimitGate();
        gate.RecordRejection("k", Policy, 10, 60, Now, Now.AddSeconds(2), []);

        gate.TryRefuse("k", Policy with { Count = 11 }, RateLimitMode.Skip, Now, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void TryRefuse_WaitMode_HandsOutLeasedSlotsInOrderThenDefers()
    {
        var gate = new RateLimitGate();
        var reopen = Now.AddSeconds(2);
        gate.RecordRejection("k", Policy, 10, 60, Now, reopen, [reopen.AddSeconds(6), reopen.AddSeconds(12)]);

        gate.TryRefuse("k", Policy, RateLimitMode.Wait, Now, out var first).ShouldBeTrue();
        gate.TryRefuse("k", Policy, RateLimitMode.Wait, Now, out var second).ShouldBeTrue();
        gate.TryRefuse("k", Policy, RateLimitMode.Wait, Now, out _).ShouldBeFalse("no slot left — the database leases more");

        first.ScheduleTo.ShouldBe(reopen.AddSeconds(6));
        second.ScheduleTo.ShouldBe(reopen.AddSeconds(12));
    }

    [TimedFact]
    public void TryTakeLeasedSlot_SlotBeforeReopening_Discarded()
    {
        // A slot earlier than the key's reopening instant cannot admit; rescheduling onto it would only buy
        // another refusal, so it is dropped and the next usable one handed out.
        var gate = new RateLimitGate();
        gate.RecordRejection("k", Policy, 10, 60, Now, Now, [Now.AddSeconds(1), Now.AddSeconds(30)]);

        gate.TryTakeLeasedSlot("k", Now.AddSeconds(20), out var slot).ShouldBeTrue();

        slot.ShouldBe(Now.AddSeconds(30));
        gate.TryTakeLeasedSlot("k", Now.AddSeconds(20), out _).ShouldBeFalse();
    }

    [TimedFact]
    public void RecordRejection_EffectiveLimitChanged_DropsSlotsSpacedForTheOldLimit()
    {
        var gate = new RateLimitGate();
        gate.RecordRejection("k", Policy, 10, 60, Now, Now, [Now.AddSeconds(6)]);

        gate.RecordRejection("k", Policy, 20, 60, Now, Now, []);

        gate.TryTakeLeasedSlot("k", Now, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void Invalidate_ForgetsRecordedRejection()
    {
        var gate = new RateLimitGate();
        gate.RecordRejection("k", Policy, 10, 60, Now, Now.AddSeconds(2), []);

        gate.Invalidate("k");

        gate.TryRefuse("k", Policy, RateLimitMode.Skip, Now, out _).ShouldBeFalse();
    }

    [TimedTheory]
    [InlineData(60, 1_000, 5)]
    [InlineData(1_000, 1, 20)]
    [InlineData(3, 1, 3)]
    [InlineData(20, 180_000, 1)]
    public void LeaseSize_CappedByHorizonCountAndMaximum(int count, int spacingMs, int expected)
    {
        // 60/min → 1s apart → 5s horizon = 5. 1000/s → capped at 20. 3 per window → never more than the
        // window holds. 20 per hour → one slot is 3 minutes away, so a lease is a single slot: a process
        // must never reserve an hour of capacity it may not use.
        var gate = new RateLimitGate();

        gate.LeaseSize(count, TimeSpan.FromMilliseconds(spacingMs)).ShouldBe(expected);
    }

    [TimedFact]
    public void Ctor_NoActivationCadence_DoesNotStepSlots()
    {
        new RateLimitGate([]).ActivationInterval.ShouldBe(TimeSpan.Zero);
    }

    [TimedFact]
    public void Ctor_ActivationCadence_TakesTheLongestInterval()
    {
        var gate = new RateLimitGate([new Cadence(TimeSpan.FromSeconds(5)), new Cadence(TimeSpan.FromSeconds(10))]);

        gate.ActivationInterval.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [TimedFact]
    public void LeaseSize_InstanceScopedGate_LeasesOneSlot()
    {
        var gate = new RateLimitGate(maxLeaseSize: 1);

        gate.LeaseSize(1_000, TimeSpan.FromMilliseconds(1)).ShouldBe(1);
    }

    private sealed class Cadence(TimeSpan interval) : IScheduledActivationCadence
    {
        public TimeSpan Interval => interval;
    }
}
