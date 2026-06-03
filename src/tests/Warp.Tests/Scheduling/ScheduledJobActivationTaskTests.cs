using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Notifications;
using Warp.Tests.Fixtures;
using Warp.Worker.Services;

namespace Warp.Tests.Scheduling;

[GenerateDatabaseTests]
public abstract class ScheduledJobActivationTaskTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ScheduledJobActivationTaskTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Activate_WhenScheduleTimeElapsed_FlipsToEnqueued()
    {
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Scheduled,
            Queue = "default",
            Type = "TestType",
            Message = "{}",
            CreateTime = now.AddMinutes(-5),
            ScheduleTime = now.AddMinutes(-1),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var actCtx = _fixture.CreateContext();
        var activated = (await Warp.Tests.Helpers.TestTasks.CreateScheduledJobActivation(actCtx, TimeProvider.System).ActivateWithNotifyAsync(CancellationToken.None)).Activated;

        activated.ShouldBe(1);
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Enqueued);
    }

    [TimedFact]
    public async Task Activate_WhenScheduleTimeInFuture_LeavesAlone()
    {
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Scheduled,
            Queue = "default",
            Type = "TestType",
            Message = "{}",
            CreateTime = now,
            ScheduleTime = now.AddMinutes(10),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var actCtx = _fixture.CreateContext();
        var activated = (await Warp.Tests.Helpers.TestTasks.CreateScheduledJobActivation(actCtx, TimeProvider.System).ActivateWithNotifyAsync(CancellationToken.None)).Activated;

        activated.ShouldBe(0);
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Scheduled);
    }

    [TimedFact]
    public async Task Activate_DoesNotTouchEnqueuedRows()
    {
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Queue = "default",
            Type = "TestType",
            Message = "{}",
            CreateTime = now,
            ScheduleTime = now.AddMinutes(-1),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var actCtx = _fixture.CreateContext();
        var activated = (await Warp.Tests.Helpers.TestTasks.CreateScheduledJobActivation(actCtx, TimeProvider.System).ActivateWithNotifyAsync(CancellationToken.None)).Activated;

        activated.ShouldBe(0);
    }

    [TimedFact]
    public async Task Activate_MultipleDueRows_ActivatesAll()
    {
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Scheduled,
                Queue = i % 2 == 0 ? "default" : "critical",
                Type = "TestType",
                Message = "{}",
                CreateTime = now.AddMinutes(-5),
                ScheduleTime = now.AddMinutes(-1),
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var actCtx = _fixture.CreateContext();
        var activated = (await Warp.Tests.Helpers.TestTasks.CreateScheduledJobActivation(actCtx, TimeProvider.System).ActivateWithNotifyAsync(CancellationToken.None)).Activated;

        activated.ShouldBe(5);
        var readCtx = _fixture.CreateContext();
        var remaining = await readCtx.Set<Job>()
            .CountAsync(x => x.CurrentState == State.Scheduled, Xunit.TestContext.Current.CancellationToken);
        remaining.ShouldBe(0);
    }

    [TimedFact]
    public async Task ActivateWithNotify_WritesActivatedJobLog_PerActivatedRow_WithPreviousScheduleTime()
    {
        // Each Scheduled→Enqueued flip writes one JobLog row with EventType="Enqueued" and
        // the row's previous ScheduleTime in the message. Atomic with the state change via
        // the xact-lock transaction that wraps ExecuteAsync — so observers reading via the
        // ServerTaskLoop's lock release point see state=Enqueued AND the audit row together.
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;
        var scheduledAt = now.AddMinutes(-1);
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Scheduled,
            Queue = "default",
            Type = "TestType",
            Message = "{}",
            CreateTime = now.AddMinutes(-5),
            ScheduleTime = scheduledAt,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var actCtx = _fixture.CreateContext();
        var result = await Warp.Tests.Helpers.TestTasks.CreateScheduledJobActivation(actCtx, TimeProvider.System).ActivateWithNotifyAsync(CancellationToken.None);
        result.Activated.ShouldBe(1);

        var readCtx = _fixture.CreateContext();
        var logs = await readCtx.Set<JobLog>()
            .Where(l => l.JobId == jobId && l.EventType == "Enqueued")
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        logs.ShouldHaveSingleItem();
        logs[0].Level.ShouldBe("Information");
        logs[0].Message.ShouldStartWith("Enqueued from Scheduled — was scheduled at ");

        // PG's timestamp(6) truncates the .NET 100-ns tick to microsecond precision, so a
        // string-equality compare on the full round-tripped ISO timestamp can disagree on the
        // last digit. Parse and compare with a microsecond tolerance — same pattern as the
        // CircuitBreaker time-precision tests.
        var emittedSchedule = DateTime.Parse(
            logs[0].Message["Enqueued from Scheduled — was scheduled at ".Length..],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        emittedSchedule.ShouldBe(scheduledAt, TimeSpan.FromMilliseconds(1));
    }

    [TimedFact]
    public async Task ActivateWithNotify_NoDueRows_WritesNoActivatedJobLog()
    {
        // Empty pass: the activation task ran but had nothing to do — must NOT write a JobLog
        // (defensive against an INSERT-without-rows regression).
        var noiseId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = noiseId,
            Kind = JobKind.Job,
            CurrentState = State.Scheduled,
            Queue = "default",
            Type = "TestType",
            Message = "{}",
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddMinutes(10),  // future — not due yet
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var actCtx = _fixture.CreateContext();
        var result = await Warp.Tests.Helpers.TestTasks.CreateScheduledJobActivation(actCtx, TimeProvider.System).ActivateWithNotifyAsync(CancellationToken.None);
        result.Activated.ShouldBe(0);

        // Activation never ran on this row → no Enqueued log written by the activation task.
        var readCtx = _fixture.CreateContext();
        var anyActivationLog = await readCtx.Set<JobLog>()
            .AnyAsync(l => l.JobId == noiseId && l.EventType == "Enqueued", Xunit.TestContext.Current.CancellationToken);
        anyActivationLog.ShouldBeFalse();
    }

    [TimedFact]
    public async Task ActivateWithNotify_FiresOneJobEnqueuedPerDistinctQueue()
    {
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;
        foreach (var queue in new[] { "default", "default", "critical" })
        {
            ctx.Set<Job>().Add(new Job
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = State.Scheduled,
                Queue = queue,
                Type = "TestType",
                Message = "{}",
                CreateTime = now.AddMinutes(-5),
                ScheduleTime = now.AddMinutes(-1),
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var transport = new RecordingTransport();
        var actCtx = _fixture.CreateContext();
        var result = await Warp.Tests.Helpers.TestTasks.CreateScheduledJobActivation(actCtx, TimeProvider.System, transport).ActivateWithNotifyAsync(CancellationToken.None);

        result.Activated.ShouldBe(3);
        transport.Published.Count.ShouldBe(2);
        transport.Published.ShouldContain(n => n.Kind == NotificationKind.JobEnqueued && n.Queue == "default");
        transport.Published.ShouldContain(n => n.Kind == NotificationKind.JobEnqueued && n.Queue == "critical");
    }

    private sealed class RecordingTransport : IWarpNotificationTransport
    {
        public List<Notification> Published { get; } = [];

        public Task PublishAsync(NotificationKind kind, string? queue, CancellationToken ct)
        {
            Published.Add(new Notification(kind, queue));
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<Notification> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }

        public Task ListenerReady { get; } = Task.CompletedTask;
    }
}
