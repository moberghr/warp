using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Observability;

/// <summary>
/// Requeues were only ever counted when a pipeline behaviour caused them: the dashboard requeue paths and
/// crash recovery wrote a <c>Requeued</c> <see cref="JobLog"/> and no <see cref="Counter"/> at all, so
/// <c>stats:requeued</c> silently under-reported every operator action and every recovered worker crash.
/// <para>
/// The invariant every test here asserts is <b>one counter increment per <c>Requeued</c> log row</b>. That
/// is the only definition of "a requeue happened" the codebase already agrees on, and it is what stops the
/// bulk path from drifting — its row count comes from a different statement than its log rows do.
/// </para>
/// </summary>
[GenerateDatabaseTests]
public abstract class RequeueStatsTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected RequeueStatsTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task RequeueJob_WritesRequeuedTotalAndManualReason()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(NewJob(jobId, State.Failed));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(jobId);

        // Assert — total and reason both written, and both agree with the log rows.
        var readCtx = _fixture.CreateContext();
        var requeuedLogs = await CountRequeuedLogs(readCtx);
        requeuedLogs.ShouldBe(1);

        var counters = await readCtx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);
        Sum(counters, "stats:requeued").ShouldBe(requeuedLogs);
        Sum(counters, "stats:requeued-manual").ShouldBe(requeuedLogs);

        // The hourly buckets exist alongside the lifetime keys — they are what the Counters chart reads,
        // and the manual paths wrote none of them before this change.
        HourlySum(counters, "stats:requeued").ShouldBe(requeuedLogs);
        HourlySum(counters, "stats:requeued-manual").ShouldBe(requeuedLogs);
    }

    [TimedFact]
    public async Task BulkRequeueJobs_CountsOnlyRowsItActuallyFlipped()
    {
        // Arrange — two requeueable jobs plus one already Enqueued, which is a no-op success and must NOT
        // be counted. This is the case where a count taken from the UPDATE's affected-rows would diverge
        // from the log rows.
        var ctx = _fixture.CreateContext();
        var failedId = Guid.NewGuid();
        var completedId = Guid.NewGuid();
        var alreadyEnqueuedId = Guid.NewGuid();
        ctx.Set<Job>().Add(NewJob(failedId, State.Failed));
        ctx.Set<Job>().Add(NewJob(completedId, State.Completed));
        ctx.Set<Job>().Add(NewJob(alreadyEnqueuedId, State.Enqueued));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.BulkRequeueJobs([failedId, completedId, alreadyEnqueuedId]);

        // Assert
        var readCtx = _fixture.CreateContext();
        var requeuedLogs = await CountRequeuedLogs(readCtx);
        requeuedLogs.ShouldBe(2);

        var counters = await readCtx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);
        Sum(counters, "stats:requeued").ShouldBe(requeuedLogs);
        Sum(counters, "stats:requeued-manual").ShouldBe(requeuedLogs);
        HourlySum(counters, "stats:requeued").ShouldBe(requeuedLogs);
        HourlySum(counters, "stats:requeued-manual").ShouldBe(requeuedLogs);
    }

    // RED until the bulk path identifies the rows IT flipped rather than re-querying for a shared clock
    // value. `ScheduleTime == now` is not an ownership token: two callers that stamp the same instant each
    // select BOTH rows, so the loser of the UPDATE — which flipped nothing — still writes a full set of
    // logs and counters. A fake clock makes the shared instant certain rather than a millisecond-wide
    // gamble, but the collision is the same one a real clock produces under concurrent operator actions.
    [TimedFact]
    public async Task BulkRequeueJobs_WhenTwoCallersShareAnInstant_CountsEachRequeueOnce()
    {
        var ctx = _fixture.CreateContext();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        ctx.Set<Job>().Add(NewJob(firstId, State.Failed));
        ctx.Set<Job>().Add(NewJob(secondId, State.Failed));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Both services read the same instant, as two dashboard requeues landing on one tick do.
        var frozen = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);

        var first = TestTasks.CreateJobCommandService(_fixture.CreateContext(), frozen);
        await first.BulkRequeueJobs([firstId, secondId]);

        // The second call flips nothing — both rows are already Enqueued at exactly this instant.
        var second = TestTasks.CreateJobCommandService(_fixture.CreateContext(), frozen);
        await second.BulkRequeueJobs([firstId, secondId]);

        var readCtx = _fixture.CreateContext();
        var requeuedLogs = await CountRequeuedLogs(readCtx);
        var counters = await readCtx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);

        // Two jobs were requeued once each. Anything above two means the second call counted work it did
        // not do — the invariant this whole file exists to hold.
        requeuedLogs.ShouldBe(2);
        Sum(counters, "stats:requeued").ShouldBe(2);
        Sum(counters, "stats:requeued-manual").ShouldBe(2);
    }

    [TimedFact]
    public async Task RecoverStaleJobs_WritesRequeuedTotalAndRecoveryReason()
    {
        // Arrange — two stale Processing jobs whose worker stopped refreshing its keep-alive.
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 2; i++)
        {
            var job = NewJob(Guid.NewGuid(), State.Processing);
            job.LastKeepAlive = DateTime.UtcNow.AddMinutes(-10);
            ctx.Set<Job>().Add(job);
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var result = await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5))
            .RecoverStaleJobsAsync(CancellationToken.None);

        // Assert
        result.Requeued.ShouldBe(2);

        var readCtx = _fixture.CreateContext();
        var requeuedLogs = await CountRequeuedLogs(readCtx);
        requeuedLogs.ShouldBe(2);

        var counters = await readCtx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);
        Sum(counters, "stats:requeued").ShouldBe(requeuedLogs);
        Sum(counters, "stats:requeued-recovery").ShouldBe(requeuedLogs);
        HourlySum(counters, "stats:requeued").ShouldBe(requeuedLogs);
        HourlySum(counters, "stats:requeued-recovery").ShouldBe(requeuedLogs);

        // Recovery is not an operator action — it must not land in the manual bucket.
        Sum(counters, "stats:requeued-manual").ShouldBe(0);
    }

    // RED until the manual path emits the meter. warp.job.requeued is documented as always-on, but it was
    // only fired at worker finalization — a dashboard requeue moved stats:requeued-manual in the DB and
    // nothing on the meter. Under JobMetricsSink = Otel the DB rows are the part that DOESN'T exist, so a
    // Grafana-only operator saw a requeue count strictly below the truth, missing exactly the two reasons
    // (manual, recovery) an on-call engineer looks for after an incident.
    [TimedFact]
    public async Task RequeueJob_EmitsRequeuedMeterWithManualReason()
    {
        // Arrange — unique queue: the meter is process-global and parallel tests requeue too.
        var queue = $"requeue-meter-manual-{Guid.NewGuid():N}";
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        var job = NewJob(jobId, State.Failed);
        job.Queue = queue;
        ctx.Set<Job>().Add(job);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        using var recorder = new RequeueMeterRecorder(queue);

        // Act
        var svc = TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(jobId);

        // Assert
        recorder.Count.ShouldBe(1);
        recorder.Reasons.ShouldBe(["manual"]);
    }

    [TimedFact]
    public async Task RecoverStaleJobs_EmitsRequeuedMeterWithRecoveryReason()
    {
        // Arrange
        var queue = $"requeue-meter-recovery-{Guid.NewGuid():N}";
        var ctx = _fixture.CreateContext();
        var job = NewJob(Guid.NewGuid(), State.Processing);
        job.Queue = queue;
        job.LastKeepAlive = DateTime.UtcNow.AddMinutes(-10);
        ctx.Set<Job>().Add(job);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        using var recorder = new RequeueMeterRecorder(queue);

        // Act
        var result = await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5))
            .RecoverStaleJobsAsync(CancellationToken.None);

        // Assert
        result.Requeued.ShouldBe(1);
        recorder.Count.ShouldBe(1);
        recorder.Reasons.ShouldBe(["recovery"]);
    }

    /// <summary>
    /// Listens on <c>warp.job.requeued</c> for one queue. A class because two tests need it and a meter
    /// listener's callback plumbing drowns the arrange/act/assert shape when written inline.
    /// </summary>
    private sealed class RequeueMeterRecorder : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener = new();
        private readonly List<string?> _reasons = [];
        private long _count;

        public RequeueMeterRecorder(string queue)
        {
            _listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, "warp.job.requeued", StringComparison.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            {
                string? reason = null;
                var matches = false;
                foreach (var tag in tags)
                {
                    if (string.Equals(tag.Key, "queue", StringComparison.Ordinal)
                        && string.Equals(tag.Value?.ToString(), queue, StringComparison.Ordinal))
                    {
                        matches = true;
                    }

                    if (string.Equals(tag.Key, "reason", StringComparison.Ordinal))
                    {
                        reason = tag.Value?.ToString();
                    }
                }

                if (matches)
                {
                    lock (_reasons)
                    {
                        _count += value;
                        _reasons.Add(reason);
                    }
                }
            });
            _listener.Start();
        }

        public long Count
        {
            get
            {
                lock (_reasons)
                {
                    return _count;
                }
            }
        }

        public List<string?> Reasons
        {
            get
            {
                lock (_reasons)
                {
                    return [.. _reasons];
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    [TimedFact]
    public async Task RecoverStaleJobs_NoRestartJob_WritesNoRequeueCounters()
    {
        // Arrange — a stale job that opted out of restart fails instead of requeueing, so nothing in the
        // requeue family may move. Guards the `requeued > 0` condition against counting the whole sweep.
        var ctx = _fixture.CreateContext();
        var job = NewJob(Guid.NewGuid(), State.Processing);
        job.LastKeepAlive = DateTime.UtcNow.AddMinutes(-10);
        job.Metadata = """{"CanBeRestarted":false}""";
        ctx.Set<Job>().Add(job);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var result = await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5))
            .RecoverStaleJobsAsync(CancellationToken.None);

        // Assert
        result.Requeued.ShouldBe(0);
        result.Failed.ShouldBe(1);

        var readCtx = _fixture.CreateContext();
        var counters = await readCtx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);
        Sum(counters, "stats:requeued").ShouldBe(0);
        Sum(counters, "stats:requeued-recovery").ShouldBe(0);
        Sum(counters, "stats:failed").ShouldBe(1);
    }

    private static Job NewJob(Guid id, State state) =>
        new()
        {
            Id = id,
            Kind = JobKind.Job,
            CurrentState = state,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        };

    private static async Task<int> CountRequeuedLogs(TestContext ctx) =>
        await ctx.Set<JobLog>()
            .Where(x => x.EventType == "Requeued")
            .CountAsync(Xunit.TestContext.Current.CancellationToken);

    private static int Sum(List<Counter> counters, string key) =>
        counters
            .Where(x => string.Equals(x.Key, key, StringComparison.Ordinal))
            .Sum(x => x.Value);

    // Hourly bucket rows are "{key}:{yyyy-MM-dd-HH}". Matching on the prefix keeps the assertion free of
    // wall-clock coupling — a test running across an hour boundary still sums to the same total.
    private static int HourlySum(List<Counter> counters, string key) =>
        counters
            .Where(x => x.Key.StartsWith(key + ":", StringComparison.Ordinal))
            .Sum(x => x.Value);
}
