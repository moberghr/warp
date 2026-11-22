using Shouldly;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Notifications;

// Rule 6.11: a "job finalized" wake names the parents of the jobs that finished, so the Orchestrator
// checks those and sweeps every parent only when it has to. These cover the bookkeeping behind that:
// what a pass is handed, and what travels in the push payload between servers.
[Trait("Category", "NoDb")]
public class FinalizedParentHintsTests
{
    private static readonly TimeSpan Never = TimeSpan.FromDays(1);

    [Fact]
    public void TakeFinalizedParents_FirstPassOfTheProcess_Sweeps()
    {
        var signals = new ServerTaskSignals<TestContext>();

        var (parents, sweep) = signals.TakeFinalizedParents(Never);

        parents.ShouldBeEmpty();
        sweep.ShouldBeTrue();
    }

    [Fact]
    public void TakeFinalizedParents_AfterAWorkerNamedTheParent_HandsOverThatParentAndDoesNotSweep()
    {
        var signals = SignalsPastTheirFirstSweep();
        var parent = Guid.NewGuid();
        signals.SignalJobFinalized(parent);
        signals.SignalJobFinalized(parent);
        signals.SignalJobFinalized(parentJobId: null);

        var (parents, sweep) = signals.TakeFinalizedParents(Never);

        parents.ShouldBe([parent]);
        sweep.ShouldBeFalse();
        signals.TakeFinalizedParents(Never).Parents.ShouldBeEmpty("a pass takes the work with it");
    }

    [Fact]
    public void TakeFinalizedParents_AfterAWakeThatNamedNoParent_Sweeps()
    {
        var signals = SignalsPastTheirFirstSweep();
        signals.SignalJobFinalized();

        signals.TakeFinalizedParents(Never).Sweep.ShouldBeTrue();
        signals.TakeFinalizedParents(Never).Sweep.ShouldBeFalse("one request, one sweep");
    }

    [Fact]
    public void TakeFinalizedParents_SweepIntervalElapsed_Sweeps()
    {
        var signals = SignalsPastTheirFirstSweep();

        signals.TakeFinalizedParents(TimeSpan.Zero).Sweep.ShouldBeTrue();
    }

    [Fact]
    public void TakeFinalizedParents_NoInterval_SweepsEveryPass()
    {
        var signals = SignalsPastTheirFirstSweep();

        signals.TakeFinalizedParents(sweepEvery: null).Sweep.ShouldBeTrue();
    }

    [Fact]
    public void ReturnFinalizedParents_AfterAFailedPass_NextPassGetsTheSameWork()
    {
        var signals = SignalsPastTheirFirstSweep();
        var parent = Guid.NewGuid();
        signals.SignalJobFinalized(parent);
        signals.SignalJobFinalized();
        var taken = signals.TakeFinalizedParents(Never);

        signals.ReturnFinalizedParents(taken.Parents, taken.Sweep);

        var (parents, sweep) = signals.TakeFinalizedParents(Never);
        parents.ShouldBe([parent]);
        sweep.ShouldBeTrue();
    }

    [Fact]
    public void SignalJobFinalized_MoreParentsThanTheSetHolds_FallsBackToOneSweep()
    {
        var signals = SignalsPastTheirFirstSweep();
        for (var i = 0; i < 10_001; i++)
        {
            signals.SignalJobFinalized(Guid.NewGuid());
        }

        var (parents, sweep) = signals.TakeFinalizedParents(Never);

        sweep.ShouldBeTrue("the dropped ids are found by the sweep");
        parents.Length.ShouldBeLessThanOrEqualTo(10_000);
    }

    [Fact]
    public void SignalJobFinalized_WithAParent_StillWakesSubscribers()
    {
        var signals = new ServerTaskSignals<TestContext>();
        var wakes = 0;
        using var subscription = signals.Subscribe(ServerTaskSignal.JobFinalized, () => wakes++);

        signals.SignalJobFinalized(Guid.NewGuid());
        signals.SignalJobFinalized(parentJobId: null);
        signals.SignalJobsFinalized([Guid.NewGuid()]);

        wakes.ShouldBe(3);
    }

    [Fact]
    public void Payload_ParentsOfAFlush_ArriveAsTheSameParents()
    {
        var signals = SignalsPastTheirFirstSweep();
        Guid[] sent = [Guid.NewGuid(), Guid.NewGuid()];

        FinalizedParentsPayload.Signal(signals, FinalizedParentsPayload.Encode(sent));

        var (parents, sweep) = signals.TakeFinalizedParents(Never);
        parents.ShouldBe(sent, ignoreOrder: true);
        sweep.ShouldBeFalse();
    }

    [Fact]
    public void Payload_JobWithNoParent_WakesWithoutASweep()
    {
        var signals = SignalsPastTheirFirstSweep();
        var wakes = 0;
        using var subscription = signals.Subscribe(ServerTaskSignal.JobFinalized, () => wakes++);

        FinalizedParentsPayload.Signal(signals, FinalizedParentsPayload.Encode(parentJobId: null));

        wakes.ShouldBe(1);
        var (parents, sweep) = signals.TakeFinalizedParents(Never);
        parents.ShouldBeEmpty();
        sweep.ShouldBeFalse();
    }

    // null and "" are what a server from before 8.0 sends; the last is a payload this version cannot read.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void Payload_ThatNamesNoParents_AsksForASweep(string? payload)
    {
        var signals = SignalsPastTheirFirstSweep();

        FinalizedParentsPayload.Signal(signals, payload);

        signals.TakeFinalizedParents(Never).Sweep.ShouldBeTrue();
    }

    [Fact]
    public void Payload_MoreParentsThanOneNotificationCarries_IsSentAsUnknown()
    {
        Guid[] tooMany = [.. Enumerable.Range(0, FinalizedParentsPayload.MaxParents + 1).Select(_ => Guid.NewGuid())];

        FinalizedParentsPayload.Encode(tooMany).ShouldBeNull();
        FinalizedParentsPayload.Encode(tooMany[..FinalizedParentsPayload.MaxParents])!.Length.ShouldBeLessThan(8000);
    }

    [Fact]
    public void Transports_JobFinalizedWithParents_RoundTripThePayload()
    {
        var payload = FinalizedParentsPayload.Encode(Guid.NewGuid());

        PostgresNotificationTransport.TryDecode(PostgresNotificationTransport.Encode(NotificationKind.JobFinalized, payload), out var postgres).ShouldBeTrue();
        SqlServerNotificationTransport.TryDecode(SqlServerNotificationTransport.Encode(NotificationKind.JobFinalized, payload), out var sqlServer).ShouldBeTrue();

        postgres.ShouldBe(new Notification(NotificationKind.JobFinalized, payload));
        sqlServer.ShouldBe(new Notification(NotificationKind.JobFinalized, payload));
    }

    private static ServerTaskSignals<TestContext> SignalsPastTheirFirstSweep()
    {
        var signals = new ServerTaskSignals<TestContext>();
        signals.TakeFinalizedParents(Never);

        return signals;
    }
}
