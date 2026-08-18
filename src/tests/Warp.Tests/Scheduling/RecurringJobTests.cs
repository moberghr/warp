using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker.Services;

namespace Warp.Tests.Scheduling;

[GenerateDatabaseTests]
public abstract class RecurringJobTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected RecurringJobTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task AddOrUpdateRecurringJob_CreatesRecurringJobInDb()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = new RecurringJobPublisher<TestContext>(ctx, TimeProvider.System, new FakeLockProvider());

        // Act
        await publisher.AddOrUpdateRecurringJob(new UnitRequest(), "test-recurring", "* * * * *");

        // Assert
        var readCtx = _fixture.CreateContext();
        var recurringJob = await readCtx.Set<RecurringJob>()
            .FirstOrDefaultAsync(r => r.Name == "test-recurring", Xunit.TestContext.Current.CancellationToken);

        recurringJob.ShouldNotBeNull();
        recurringJob.Cron.ShouldBe("* * * * *");
        recurringJob.Name.ShouldBe("test-recurring");
        recurringJob.Queue.ShouldBe("default");
    }

    [TimedFact]
    public async Task GetRecurringJobs_ReturnsPaginated()
    {
        // Arrange
        for (var i = 0; i < 3; i++)
        {
            var publisher = new RecurringJobPublisher<TestContext>(_fixture.CreateContext(), TimeProvider.System, new FakeLockProvider());
            await publisher.AddOrUpdateRecurringJob(new UnitRequest(), $"recurring-{i}", "* * * * *");
        }

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        var result = await svc.GetRecurringJobs(new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert
        result.TotalCount.ShouldBe(3);
    }

    [TimedFact]
    public async Task GetRecurringJobById_ReturnsDetail()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = new RecurringJobPublisher<TestContext>(ctx, TimeProvider.System, new FakeLockProvider());
        await publisher.AddOrUpdateRecurringJob(new UnitRequest(), "detail-test", "*/5 * * * *");

        var readCtx = _fixture.CreateContext();
        var rj = await readCtx.Set<RecurringJob>().FirstAsync(r => r.Name == "detail-test", Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        var detail = await svc.GetRecurringJobById(rj.Id);

        // Assert
        detail.ShouldNotBeNull();
        detail.Name.ShouldBe("detail-test");
        detail.Cron.ShouldBe("*/5 * * * *");
    }

    [TimedFact]
    public async Task DeleteRecurringJob_RemovesFromDb()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = new RecurringJobPublisher<TestContext>(ctx, TimeProvider.System, new FakeLockProvider());
        await publisher.AddOrUpdateRecurringJob(new UnitRequest(), "to-delete", "* * * * *");

        var readCtx = _fixture.CreateContext();
        var rj = await readCtx.Set<RecurringJob>().FirstAsync(r => r.Name == "to-delete", Xunit.TestContext.Current.CancellationToken);
        var rjId = rj.Id;

        // Remove RecurringJobLog entries so FK won't block delete
        var detachCtx = _fixture.CreateContext();
        await detachCtx.Set<RecurringJobLog>()
            .Where(l => l.RecurringJobId == rjId)
            .ExecuteDeleteAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        await svc.DeleteRecurringJob(rjId);

        // Assert
        var verifyCtx = _fixture.CreateContext();
        var deleted = await verifyCtx.Set<RecurringJob>().FirstOrDefaultAsync(r => r.Id == rjId, Xunit.TestContext.Current.CancellationToken);
        deleted.ShouldBeNull();
    }

    [TimedFact]
    public async Task TriggerRecurringJob_CreatesJob()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = new RecurringJobPublisher<TestContext>(ctx, TimeProvider.System, new FakeLockProvider());
        await publisher.AddOrUpdateRecurringJob(new UnitRequest(), "trigger-test", "* * * * *");

        var readCtx = _fixture.CreateContext();
        var rj = await readCtx.Set<RecurringJob>().FirstAsync(r => r.Name == "trigger-test", Xunit.TestContext.Current.CancellationToken);

        var jobCountBefore = await _fixture.CreateContext().Set<RecurringJobLog>()
            .Where(l => l.RecurringJobId == rj.Id)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        await svc.TriggerRecurringJob(rj.Id);

        // Assert
        var jobCountAfter = await _fixture.CreateContext().Set<RecurringJobLog>()
            .Where(l => l.RecurringJobId == rj.Id)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);

        jobCountAfter.ShouldBe(jobCountBefore + 1);
    }

    [TimedFact]
    public async Task TriggerRecurringJob_FiresJobEnqueuedNotification()
    {
        // Regression: with DB push enabled, the dashboard "Trigger Now" button used to
        // bypass NotificationDispatch and rely on the 1s polling backstop to discover the
        // newly-enqueued job. The dispatcher should be woken via push the moment the job
        // row lands in State.Enqueued — same contract as Publisher.SaveChangesAsync.
        var ctx = _fixture.CreateContext();
        var publisher = new RecurringJobPublisher<TestContext>(ctx, TimeProvider.System, new FakeLockProvider());
        await publisher.AddOrUpdateRecurringJob(new UnitRequest(), "trigger-push-test", "* * * * *");

        var readCtx = _fixture.CreateContext();
        var rj = await readCtx.Set<RecurringJob>().FirstAsync(r => r.Name == "trigger-push-test", Xunit.TestContext.Current.CancellationToken);

        var transport = new RecordingNotificationTransport();
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, transport, TestTasks.NullSignals);

        await svc.TriggerRecurringJob(rj.Id);

        transport.Published.Count.ShouldBe(1);
        transport.Published[0].Kind.ShouldBe(NotificationKind.JobEnqueued);
        transport.Published[0].Queue.ShouldBe("default");
    }

    [TimedFact]
    public async Task ScheduleRecurringJobs_DueJob_DoesNotWakeBeforeCommit()
    {
        // ExecuteAsync runs inside the server-task host's lock transaction, so the firing is not durable
        // yet when it returns. Waking a worker here would send it querying for a row it cannot see (§8.25).
        await ArrangeDueRecurringJob("wake-precommit-test");

        var transport = new RecordingNotificationTransport();
        var signals = new ServerTaskSignals<TestContext>();
        var woken = 0;
        using var subscription = signals.Subscribe(ServerTaskSignal.JobEnqueued, () => woken++);

        await TestTasks.CreateRecurringJobScheduler(_fixture.CreateContext(), TimeProvider.System, signals, transport)
            .ScheduleRecurringJobsAsync(CancellationToken.None);

        woken.ShouldBe(0);
        transport.Published.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task OnCommitted_AfterDueRecurringJobScheduled_FiresJobEnqueued()
    {
        // Regression: TriggerRecurringJob_FiresJobEnqueuedNotification fixed the manual "Trigger Now"
        // path, but the scheduler itself still enqueued silently — the firing waited out the worker's
        // backoff (up to MaxPollingInterval, which UseDatabasePush raises to 5 minutes) before pickup.
        await ArrangeDueRecurringJob("wake-postcommit-test");

        var transport = new RecordingNotificationTransport();
        var signals = new ServerTaskSignals<TestContext>();
        var woken = 0;
        using var subscription = signals.Subscribe(ServerTaskSignal.JobEnqueued, () => woken++);

        var scheduler = TestTasks.CreateRecurringJobScheduler(_fixture.CreateContext(), TimeProvider.System, signals, transport);
        await scheduler.ExecuteAsync(CancellationToken.None);

        // Act: the host calls this once the lock transaction has committed.
        await scheduler.OnCommittedAsync(CancellationToken.None);

        woken.ShouldBe(1);
        transport.Published.Count.ShouldBe(1);
        transport.Published[0].Kind.ShouldBe(NotificationKind.JobEnqueued);
        transport.Published[0].Queue.ShouldBe("default");
    }

    private async Task<RecurringJob> ArrangeDueRecurringJob(string name)
    {
        var publisher = new RecurringJobPublisher<TestContext>(_fixture.CreateContext(), TimeProvider.System, new FakeLockProvider());
        await publisher.AddOrUpdateRecurringJob(new UnitRequest(), name, "* * * * *");

        var setupCtx = _fixture.CreateContext();
        var recurring = await setupCtx.Set<RecurringJob>().FirstAsync(r => r.Name == name, Xunit.TestContext.Current.CancellationToken);
        recurring.NextExecution = DateTime.UtcNow.AddMinutes(-5);
        await setupCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return recurring;
    }

    private sealed class RecordingNotificationTransport : IWarpNotificationTransport
    {
        public List<(NotificationKind Kind, string? Queue)> Published { get; } = [];

        public Task PublishAsync(NotificationKind kind, string? queue, CancellationToken ct)
        {
            Published.Add((kind, queue));

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<Notification> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            // Test-only — listening is irrelevant for the publish-side regression.
            await Task.Yield();
            yield break;
        }

        public Task ListenerReady { get; } = Task.CompletedTask;
    }

    [TimedFact]
    public async Task DisableRecurringJob_SetsDisabledAt()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = new RecurringJobPublisher<TestContext>(ctx, TimeProvider.System, new FakeLockProvider());
        await publisher.AddOrUpdateRecurringJob(new UnitRequest(), "disable-test", "* * * * *");

        var readCtx = _fixture.CreateContext();
        var rj = await readCtx.Set<RecurringJob>().FirstAsync(r => r.Name == "disable-test", Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        await svc.DisableRecurringJob(rj.Id);

        // Assert
        var verifyCtx = _fixture.CreateContext();
        var updated = await verifyCtx.Set<RecurringJob>().FirstAsync(r => r.Id == rj.Id, Xunit.TestContext.Current.CancellationToken);
        updated.DisabledAt.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task EnableRecurringJob_ClearsDisabledAt()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        ctx.Set<RecurringJob>().Add(new RecurringJob
        {
            Name = "enable-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow,
            NextExecution = DateTime.UtcNow.AddMinutes(1),
            DisabledAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var readCtx = _fixture.CreateContext();
        var rj = await readCtx.Set<RecurringJob>().FirstAsync(r => r.Name == "enable-test", Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        await svc.EnableRecurringJob(rj.Id);

        // Assert
        var verifyCtx = _fixture.CreateContext();
        var updated = await verifyCtx.Set<RecurringJob>().FirstAsync(r => r.Id == rj.Id, Xunit.TestContext.Current.CancellationToken);
        updated.DisabledAt.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetRecurringJobs_ReturnsDisabledAt()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var disabledTime = DateTime.UtcNow;
        ctx.Set<RecurringJob>().Add(new RecurringJob
        {
            Name = "disabled-list-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow,
            NextExecution = DateTime.UtcNow.AddMinutes(1),
            DisabledAt = disabledTime,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        var result = await svc.GetRecurringJobs(new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert
        var item = result.Items.ShouldHaveSingleItem();
        item.DisabledAt.ShouldNotBeNull();
        item.DisabledAt.Value.ShouldBe(disabledTime, TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task GetRecurringJobById_ReturnsDisabledAt()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var disabledTime = DateTime.UtcNow;
        ctx.Set<RecurringJob>().Add(new RecurringJob
        {
            Name = "disabled-detail-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow,
            NextExecution = DateTime.UtcNow.AddMinutes(1),
            DisabledAt = disabledTime,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var readCtx = _fixture.CreateContext();
        var rj = await readCtx.Set<RecurringJob>().FirstAsync(r => r.Name == "disabled-detail-test", Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        var detail = await svc.GetRecurringJobById(rj.Id);

        // Assert
        detail.ShouldNotBeNull();
        detail.DisabledAt.ShouldNotBeNull();
        detail.DisabledAt.Value.ShouldBe(disabledTime, TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task GetRecurringJobHistory_ReturnsSkippedFlag()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var recurringJob = new RecurringJob
        {
            Name = "skipped-history-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow,
            NextExecution = DateTime.UtcNow.AddMinutes(1),
        };
        ctx.Set<RecurringJob>().Add(recurringJob);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        ctx.Set<RecurringJobLog>().Add(new RecurringJobLog
        {
            RecurringJobId = recurringJob.Id,
            Skipped = true,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        var history = await svc.GetRecurringJobHistory(recurringJob.Id, new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert
        var entry = history.Items.ShouldHaveSingleItem();
        entry.Skipped.ShouldBeTrue();
        entry.JobId.ShouldBeNull();
    }

    [TimedFact]
    public async Task RecurringJobScheduler_CreatesJobWhenDue()
    {
        // Arrange — create a recurring job with NextExecution in the past
        var ctx = _fixture.CreateContext();
        var nextJobId = Guid.NewGuid();
        var pastTime = DateTime.UtcNow.AddMinutes(-5);

        ctx.Set<Job>().Add(new Job
        {
            Id = nextJobId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = pastTime,
            ScheduleTime = pastTime,
            Queue = "default",
        });
        var recurringJob = new RecurringJob
        {
            Name = "scheduler-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            NextExecution = pastTime,
        };
        ctx.Set<RecurringJob>().Add(recurringJob);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        ctx.Set<RecurringJobLog>().Add(new RecurringJobLog
        {
            RecurringJobId = recurringJob.Id,
            JobId = nextJobId,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var jobCountBefore = await _fixture.CreateContext().Set<Job>().CountAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var schedCtx = _fixture.CreateContext();
        var result = await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ScheduleRecurringJobsAsync(CancellationToken.None);

        // Assert
        result.Scheduled.ShouldBeGreaterThanOrEqualTo(1);

        var jobCountAfter = await _fixture.CreateContext().Set<Job>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        jobCountAfter.ShouldBeGreaterThan(jobCountBefore);
    }

    [TimedFact]
    public async Task RecurringJobScheduler_CreatesJobWithTraceId()
    {
        var ctx = _fixture.CreateContext();
        var pastTime = DateTime.UtcNow.AddMinutes(-5);
        var recurringJob = new RecurringJob
        {
            Name = "scheduler-trace-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            NextExecution = pastTime,
        };
        ctx.Set<RecurringJob>().Add(recurringJob);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var schedCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ScheduleRecurringJobsAsync(CancellationToken.None);

        var job = await _fixture.CreateContext().Set<Job>()
            .Where(x => x.CurrentState == State.Enqueued)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

        // A recurring firing bypasses Publisher, so it must root its own trace — otherwise the
        // fired job (and its whole tree) lands in the DB with a null trace_id.
        job.TraceId.ShouldNotBeNull();
        job.TraceId.ShouldBe(job.Id);
    }

    [TimedFact]
    public async Task GetRecurringJobs_WithFailedRun_ReturnsLastRunState()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var rj = AddRecurringJob(ctx, "last-run-state", DateTime.UtcNow.AddMinutes(1));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var jobId = AddJob(ctx, State.Failed);
        AddRun(ctx, rj.Id, jobId);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        var result = await svc.GetRecurringJobs(new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert
        var item = result.Items.ShouldHaveSingleItem();
        item.HasLastRun.ShouldBeTrue();
        item.LastJobId.ShouldBe(jobId);
        item.LastState.ShouldBe(State.Failed);
    }

    [TimedFact]
    public async Task GetRecurringJobs_WithNewerSkippedLog_ReturnsLastRealRunState()
    {
        // Arrange: a real run, then a skipped firing on top of it (the definition was disabled)
        var ctx = _fixture.CreateContext();
        var rj = AddRecurringJob(ctx, "skipped-on-top", DateTime.UtcNow.AddMinutes(1));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var jobId = AddJob(ctx, State.Completed);
        AddRun(ctx, rj.Id, jobId);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        ctx.Set<RecurringJobLog>().Add(new RecurringJobLog
        {
            RecurringJobId = rj.Id,
            Skipped = true,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        var result = await svc.GetRecurringJobs(new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert: a skip is not a run, so the last real firing still reports
        var item = result.Items.ShouldHaveSingleItem();
        item.HasLastRun.ShouldBeTrue();
        item.LastJobId.ShouldBe(jobId);
        item.LastState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task GetRecurringJobs_WithCleanedUpJob_ReportsRunWithoutState()
    {
        // Arrange: a run whose job row is gone (JobId nulled by DeleteBehavior.SetNull)
        var ctx = _fixture.CreateContext();
        var rj = AddRecurringJob(ctx, "cleaned-up-run", DateTime.UtcNow.AddMinutes(1));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        AddRun(ctx, rj.Id, jobId: null);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        var result = await svc.GetRecurringJobs(new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert: it ran, but the outcome is no longer knowable
        var item = result.Items.ShouldHaveSingleItem();
        item.HasLastRun.ShouldBeTrue();
        item.LastJobId.ShouldBeNull();
        item.LastState.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetRecurringJobs_WithoutRuns_ReportsNoLastRun()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        AddRecurringJob(ctx, "never-ran", DateTime.UtcNow.AddMinutes(1));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        var result = await svc.GetRecurringJobs(new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert
        var item = result.Items.ShouldHaveSingleItem();
        item.HasLastRun.ShouldBeFalse();
        item.LastJobId.ShouldBeNull();
        item.LastState.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetRecurringJobs_WithMultipleDefinitions_MapsEachRunToItsOwnDefinition()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var first = AddRecurringJob(ctx, "multi-first", DateTime.UtcNow.AddMinutes(1));
        var second = AddRecurringJob(ctx, "multi-second", DateTime.UtcNow.AddMinutes(2));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var firstJobId = AddJob(ctx, State.Completed);
        AddRun(ctx, first.Id, firstJobId);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var secondJobId = AddJob(ctx, State.Processing);
        AddRun(ctx, second.Id, secondJobId);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new RecurringJobService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), TestTasks.NullSignals);
        var result = await svc.GetRecurringJobs(new BaseListRequest { Page = 0, PageSize = 20 });

        // Assert: ordered by NextExecution
        result.Items.Count.ShouldBe(2);
        result.Items[0].LastJobId.ShouldBe(firstJobId);
        result.Items[0].LastState.ShouldBe(State.Completed);
        result.Items[1].LastJobId.ShouldBe(secondJobId);
        result.Items[1].LastState.ShouldBe(State.Processing);
    }

    private static RecurringJob AddRecurringJob(TestContext ctx, string name, DateTime nextExecution)
    {
        var recurringJob = new RecurringJob
        {
            Name = name,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow,
            NextExecution = nextExecution,
        };

        ctx.Set<RecurringJob>().Add(recurringJob);

        return recurringJob;
    }

    private static Guid AddJob(TestContext ctx, State state)
    {
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = state,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });

        return jobId;
    }

    private static void AddRun(TestContext ctx, int recurringJobId, Guid? jobId)
    {
        ctx.Set<RecurringJobLog>().Add(new RecurringJobLog
        {
            RecurringJobId = recurringJobId,
            JobId = jobId,
            CreatedAt = DateTime.UtcNow,
        });
    }
}
