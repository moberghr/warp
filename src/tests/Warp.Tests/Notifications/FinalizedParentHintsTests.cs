using System.Diagnostics;
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
    public void TakeFinalizedParents_ScheduledRunAMomentSoonerAfterItsTickThanTheLast_StillSweeps()
    {
        // Scheduled runs are exactly one interval apart on the grid (rule 6.23), give or take the few
        // milliseconds a run takes to start. Asked for a whole interval, a run that started 10 ms sooner
        // than the last did not sweep, and the backstop came every second interval.
        var every = TimeSpan.FromSeconds(10);
        var signals = new ServerTaskSignals<TestContext>();
        var first = Stopwatch.GetTimestamp();
        signals.TakeFinalizedParents(every, first);

        var sweep = signals.TakeFinalizedParents(every, After(first, TimeSpan.FromSeconds(9.99))).Sweep;

        sweep.ShouldBeTrue();
    }

    [Fact]
    public void TakeFinalizedParents_SignalRunHalfwayThroughTheInterval_DoesNotSweep()
    {
        var every = TimeSpan.FromSeconds(10);
        var signals = new ServerTaskSignals<TestContext>();
        var first = Stopwatch.GetTimestamp();
        signals.TakeFinalizedParents(every, first);

        var sweep = signals.TakeFinalizedParents(every, After(first, TimeSpan.FromSeconds(5))).Sweep;

        sweep.ShouldBeFalse();
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
    public void Merge_MoreJobsThanOnePayloadCarries_SplitsAndKeepsEveryId()
    {
        var parent = Guid.NewGuid();
        var jobs = Enumerable.Range(0, FinalizedParentsPayload.MaxIds + 50).Select(_ => Guid.NewGuid()).ToList();

        var merged = FinalizedParentsPayload.Merge(jobs.Select(x => (string?)FinalizedParentsPayload.Encode(parent, x)));

        merged.Count.ShouldBe(2);
        merged.ShouldAllBe(x => x != null);
        var all = string.Concat(merged);
        jobs.ShouldAllBe(x => all.Contains(x.ToString("N"), StringComparison.Ordinal));
        merged.Count(x => x!.Contains(parent.ToString("N"), StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public void Merge_AnUnknownPayloadAmongThem_IsOneUnknown()
    {
        var merged = FinalizedParentsPayload.Merge([FinalizedParentsPayload.Encode(Guid.NewGuid(), Guid.NewGuid()), null]);

        merged.ShouldBe([null]);
    }

    [Fact]
    public void Payload_MoreParentsThanOneNotificationCarries_IsSentAsUnknown()
    {
        Guid[] tooMany = [.. Enumerable.Range(0, FinalizedParentsPayload.MaxIds + 1).Select(_ => Guid.NewGuid())];

        FinalizedParentsPayload.Encode(tooMany).ShouldBeNull();
        FinalizedParentsPayload.Encode(tooMany[..1], tooMany[1..]).ShouldBeNull();
        FinalizedParentsPayload.Encode(tooMany[..1], tooMany[2..])!.Length.ShouldBeLessThan(8000);
    }

    // The second half of a wake: the job itself, once at rest, since a continuation may wait on it.
    [Fact]
    public void TakeSettledJobs_AfterAWorkerNamedTheFinishedJob_HandsItOverOnce()
    {
        var signals = SignalsPastTheirFirstSweep();
        var job = Guid.NewGuid();
        signals.SignalJobFinalized(parentJobId: null, settledJobId: job);
        signals.SignalJobFinalized(parentJobId: null, settledJobId: job);

        signals.TakeSettledJobs().ShouldBe([job]);
        signals.TakeSettledJobs().ShouldBeEmpty();
        signals.TakeFinalizedParents(Never).Sweep.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Payload_FinishedJobsWithOrWithoutParents_ArriveAsSent(bool withParent)
    {
        var signals = SignalsPastTheirFirstSweep();
        Guid[] sentParents = withParent ? [Guid.NewGuid()] : [];
        Guid[] sentSettled = [Guid.NewGuid(), Guid.NewGuid()];

        FinalizedParentsPayload.Signal(signals, FinalizedParentsPayload.Encode(sentParents, sentSettled));

        signals.TakeSettledJobs().ShouldBe(sentSettled, ignoreOrder: true);
        var (parents, sweep) = signals.TakeFinalizedParents(Never);
        parents.ShouldBe(sentParents);
        sweep.ShouldBeFalse();
    }

    [Fact]
    public void Payload_OneJobAndItsParent_ArrivesAsSent()
    {
        var signals = SignalsPastTheirFirstSweep();
        var parent = Guid.NewGuid();
        var job = Guid.NewGuid();

        FinalizedParentsPayload.Signal(signals, FinalizedParentsPayload.Encode(parent, job));

        signals.TakeSettledJobs().ShouldBe([job]);
        signals.TakeFinalizedParents(Never).Parents.ShouldBe([parent]);
    }

    // A large awaited set is swept a slice at a time, each sweep carrying on where the last stopped, so
    // no parent is starved behind the ones before it. Parents this pass was told about jump the queue.
    [Fact]
    public void NextSweepSlice_ThreeSweepsOfTwo_VisitEveryParentAndWrapAround()
    {
        List<Guid> all = [.. Enumerable.Range(0, 5).Select(_ => Guid.NewGuid())];
        var cursor = Guid.Empty;
        var seen = new List<Guid>();

        for (var sweep = 0; sweep < 3; sweep++)
        {
            (var slice, cursor) = Warp.Worker.Services.Orchestrator<TestContext>.NextSweepSlice(all, cursor, [], take: 2);
            slice.Count.ShouldBe(2);
            seen.AddRange(slice);
        }

        seen.Distinct().Count().ShouldBe(5, "six visits over five parents: all of them, one twice");
    }

    [Fact]
    public void NextSweepSlice_ParentNamedThisPass_IsIncludedOutOfTurn()
    {
        List<Guid> all = [.. Enumerable.Range(0, 5).Select(_ => Guid.NewGuid())];
        all.Sort();
        var notAwaited = Guid.NewGuid();

        var (slice, _) = Warp.Worker.Services.Orchestrator<TestContext>.NextSweepSlice(all, Guid.Empty, [all[4], notAwaited], take: 2);

        slice.ShouldBe([all[0], all[1], all[4]], ignoreOrder: true);
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

    private static long After(long timestamp, TimeSpan elapsed) =>
        timestamp + (long)(elapsed.TotalSeconds * Stopwatch.Frequency);

    private static ServerTaskSignals<TestContext> SignalsPastTheirFirstSweep()
    {
        var signals = new ServerTaskSignals<TestContext>();
        signals.TakeFinalizedParents(Never);

        return signals;
    }
}
