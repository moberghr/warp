using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker.Services;

namespace Warp.Tests.Scheduling;

[GenerateDatabaseTests]
public abstract class RecurringJobEdgeCaseTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected RecurringJobEdgeCaseTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task ScheduleRecurringJobs_JobStillPending_SkipsScheduling()
    {
        // Arrange — recurring job with NextJobId pointing to an Enqueued (pending) job
        var ctx = _fixture.CreateContext();
        var nextJobId = Guid.NewGuid();
        var pastTime = DateTime.UtcNow.AddMinutes(-5);

        ctx.Set<Job>().Add(new Job
        {
            Id = nextJobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued, // Still pending
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = pastTime,
            ScheduleTime = pastTime,
            Queue = "default",
        });
        var recurringJob = new RecurringJob
        {
            Name = "skip-test",
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

        // Assert — should skip because the pending job still exists. The dedup `continue` runs before
        // the disabled branch, so this is not a "skipped disabled definition" and neither counter moves.
        result.Scheduled.ShouldBe(0);
        result.Skipped.ShouldBe(0);

        var jobCountAfter = await _fixture.CreateContext().Set<Job>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        jobCountAfter.ShouldBe(jobCountBefore);
    }

    [TimedFact]
    public async Task ScheduleRecurringJobs_MultipleDueJobs_SchedulesAll()
    {
        // Arrange — insert 3 recurring jobs with past NextExecution, each with a completed next job
        var ctx = _fixture.CreateContext();
        var pastTime = DateTime.UtcNow.AddMinutes(-5);

        for (var i = 0; i < 3; i++)
        {
            var nextJobId = Guid.NewGuid();
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
                Name = $"multi-test-{i}",
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
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var schedCtx = _fixture.CreateContext();
        var result = await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ScheduleRecurringJobsAsync(CancellationToken.None);

        // Assert
        result.Scheduled.ShouldBe(3);
    }

    [TimedFact]
    public async Task ScheduleRecurringJobs_UpdatesNextExecution()
    {
        // Arrange
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
            Name = "next-exec-test",
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

        // Act
        var schedCtx = _fixture.CreateContext();
        var beforeScheduler = DateTime.UtcNow;
        await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ScheduleRecurringJobsAsync(CancellationToken.None);

        // Assert — NextExecution is computed from the scheduler's `now` via cron.GetNextOccurrence,
        // which returns a strictly-future occurrence relative to that `now`. Compare against the
        // time captured before invoking the scheduler — comparing against a fresh DateTime.UtcNow
        // flakes when the scheduler runs just before a cron minute boundary (e.g. scheduler at
        // 15:55:59.99x → NextExecution = 15:56:00.000 < fresh UtcNow at 15:56:00.0329).
        var readCtx = _fixture.CreateContext();
        var rj = await readCtx.Set<RecurringJob>().FirstAsync(r => r.Name == "next-exec-test", Xunit.TestContext.Current.CancellationToken);
        rj.NextExecution.ShouldNotBeNull();
        rj.NextExecution.Value.ShouldBeGreaterThan(beforeScheduler);
    }

    [TimedFact]
    public async Task ScheduleRecurringJobs_UpdatesLastExecution()
    {
        // Arrange
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
            Name = "last-exec-test",
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

        // Act
        var schedCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ScheduleRecurringJobsAsync(CancellationToken.None);

        // Assert — LastExecution should be set (was the previous NextExecution)
        var readCtx = _fixture.CreateContext();
        var rj = await readCtx.Set<RecurringJob>().FirstAsync(r => r.Name == "last-exec-test", Xunit.TestContext.Current.CancellationToken);
        rj.LastExecution.ShouldNotBeNull();
        rj.LastExecution.Value.ShouldBe(pastTime, TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task ScheduleRecurringJobs_DisabledJob_CreatesSkippedLogEntry()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var pastTime = DateTime.UtcNow.AddMinutes(-5);
        var recurringJob = new RecurringJob
        {
            Name = "disabled-skip-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            NextExecution = pastTime,
            DisabledAt = DateTime.UtcNow.AddMinutes(-20),
        };
        ctx.Set<RecurringJob>().Add(recurringJob);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var schedCtx = _fixture.CreateContext();
        var result = await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ScheduleRecurringJobsAsync(CancellationToken.None);

        // Assert — a skip is counted as a skip, never as a scheduled job
        result.Scheduled.ShouldBe(0);
        result.Skipped.ShouldBe(1);

        var readCtx = _fixture.CreateContext();
        var log = await readCtx.Set<RecurringJobLog>()
            .Where(l => l.RecurringJobId == recurringJob.Id)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        log.ShouldNotBeNull();
        log.Skipped.ShouldBeTrue();
        log.JobId.ShouldBeNull();
    }

    [TimedFact]
    public async Task ScheduleRecurringJobs_DisabledJob_DoesNotCreateJob()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var pastTime = DateTime.UtcNow.AddMinutes(-5);
        ctx.Set<RecurringJob>().Add(new RecurringJob
        {
            Name = "disabled-no-job-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            NextExecution = pastTime,
            DisabledAt = DateTime.UtcNow.AddMinutes(-20),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var jobCountBefore = await _fixture.CreateContext().Set<Job>().CountAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var schedCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ScheduleRecurringJobsAsync(CancellationToken.None);

        // Assert
        var jobCountAfter = await _fixture.CreateContext().Set<Job>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        jobCountAfter.ShouldBe(jobCountBefore);
    }

    [TimedFact]
    public async Task ScheduleRecurringJobs_DisabledJob_AdvancesNextExecution()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var pastTime = DateTime.UtcNow.AddMinutes(-5);
        ctx.Set<RecurringJob>().Add(new RecurringJob
        {
            Name = "disabled-next-exec-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            NextExecution = pastTime,
            DisabledAt = DateTime.UtcNow.AddMinutes(-20),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var schedCtx = _fixture.CreateContext();
        var beforeScheduler = DateTime.UtcNow;
        await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ScheduleRecurringJobsAsync(CancellationToken.None);

        // Assert — compare against the pre-scheduler timestamp, not a fresh DateTime.UtcNow:
        // cron `* * * * *` produces minute-boundary times, so a scheduler invocation at
        // 15:55:59.99x computes NextExecution = 15:56:00.000, which is < UtcNow at assertion.
        //
        // Advancing it is deliberate even though the definition is inert: it keeps the skip cadence
        // cron-paced. A frozen NextExecution would leave the row permanently due and write one skip
        // log per scheduler tick. The dashboard hides the column while disabled instead.
        var readCtx = _fixture.CreateContext();
        var rj = await readCtx.Set<RecurringJob>().FirstAsync(r => r.Name == "disabled-next-exec-test", Xunit.TestContext.Current.CancellationToken);
        rj.NextExecution.ShouldNotBeNull();
        rj.NextExecution.Value.ShouldBeGreaterThan(beforeScheduler);
    }

    [TimedFact]
    public async Task ExecuteAsync_OneDueAndOneDisabled_ReportsScheduledAndSkippedSeparately()
    {
        // Arrange — one firing definition and one disabled one, both due
        var ctx = _fixture.CreateContext();
        var dueAt = DateTime.UtcNow.AddMinutes(-5);
        ctx.Set<RecurringJob>().Add(new RecurringJob
        {
            Name = "message-enabled-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            NextExecution = dueAt,
        });
        ctx.Set<RecurringJob>().Add(new RecurringJob
        {
            Name = "message-disabled-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            NextExecution = dueAt,
            DisabledAt = DateTime.UtcNow.AddMinutes(-8),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var schedCtx = _fixture.CreateContext();
        var message = await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ExecuteAsync(CancellationToken.None);

        // Assert — the server-task history must not report the skipped definition as scheduled
        message.ShouldBe("Scheduled 1 recurring jobs, skipped 1 disabled");
    }

    [TimedFact]
    public async Task ExecuteAsync_NothingDue_ReportsNothing()
    {
        // Arrange — a definition that is not due yet
        var ctx = _fixture.CreateContext();
        ctx.Set<RecurringJob>().Add(new RecurringJob
        {
            Name = "message-idle-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            NextExecution = DateTime.UtcNow.AddHours(1),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var schedCtx = _fixture.CreateContext();
        var message = await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ExecuteAsync(CancellationToken.None);

        // Assert — an idle tick stays silent rather than logging "Scheduled 0"
        message.ShouldBeNull();
    }

    [TimedFact]
    public async Task ScheduleRecurringJobs_DisabledJob_DoesNotAdvanceLastExecution()
    {
        // Arrange — a definition that ran once, then was disabled
        var ctx = _fixture.CreateContext();
        var lastRealExecution = DateTime.UtcNow.AddMinutes(-30);
        var recurringJob = new RecurringJob
        {
            Name = "disabled-last-exec-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow.AddMinutes(-60),
            LastExecution = lastRealExecution,
            NextExecution = DateTime.UtcNow.AddMinutes(-5),
            DisabledAt = DateTime.UtcNow.AddMinutes(-20),
        };
        ctx.Set<RecurringJob>().Add(recurringJob);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var schedCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ScheduleRecurringJobsAsync(CancellationToken.None);

        // Assert — a skip executed nothing, so LastExecution still names the last real run
        var readCtx = _fixture.CreateContext();
        var rj = await readCtx.Set<RecurringJob>().FirstAsync(r => r.Id == recurringJob.Id, Xunit.TestContext.Current.CancellationToken);
        rj.LastExecution.ShouldNotBeNull();
        rj.LastExecution.Value.ShouldBe(lastRealExecution, TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task ScheduleRecurringJobs_DisabledJobNeverRan_LeavesLastExecutionNull()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var recurringJob = new RecurringJob
        {
            Name = "disabled-never-ran-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            NextExecution = DateTime.UtcNow.AddMinutes(-5),
            DisabledAt = DateTime.UtcNow.AddMinutes(-8),
        };
        ctx.Set<RecurringJob>().Add(recurringJob);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var schedCtx = _fixture.CreateContext();
        await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ScheduleRecurringJobsAsync(CancellationToken.None);

        // Assert — the dashboard must keep reading "Never", not the skipped occurrence
        var readCtx = _fixture.CreateContext();
        var rj = await readCtx.Set<RecurringJob>().FirstAsync(r => r.Id == recurringJob.Id, Xunit.TestContext.Current.CancellationToken);
        rj.LastExecution.ShouldBeNull();
    }

    [TimedFact]
    public async Task ScheduleRecurringJobs_EnabledJob_CreatesJobNormally()
    {
        // Arrange — enabled recurring job (DisabledAt = null) with completed previous job
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
            Name = "enabled-regression-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            NextExecution = pastTime,
            DisabledAt = null,
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

        // Assert — should create a real job
        result.Scheduled.ShouldBe(1);

        var jobCountAfter = await _fixture.CreateContext().Set<Job>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        jobCountAfter.ShouldBe(jobCountBefore + 1);

        var log = await _fixture.CreateContext().Set<RecurringJobLog>()
            .Where(l => l.RecurringJobId == recurringJob.Id)
            .OrderByDescending(l => l.CreatedAt)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);
        log.Skipped.ShouldBeFalse();
        log.JobId.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task ScheduleRecurringJobs_NextExecutionExactlyNow_Schedules()
    {
        // Arrange — NextExecution == now should be scheduled (<= comparison)
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow.AddMinutes(10);

        var recurringJob = new RecurringJob
        {
            Name = "boundary-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = now.AddMinutes(-10),
            NextExecution = now,
        };
        ctx.Set<RecurringJob>().Add(recurringJob);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var tp = new FakeTimeProvider(now);
        var schedCtx = _fixture.CreateContext();
        var result = await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, tp).ScheduleRecurringJobsAsync(CancellationToken.None);

        // Assert — should schedule (NextExecution <= now)
        result.Scheduled.ShouldBe(1);
    }

    [TimedFact]
    public async Task ScheduleRecurringJobs_NextExecutionInFuture_DoesNotSchedule()
    {
        // Arrange — NextExecution in the future should NOT be scheduled
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;

        var recurringJob = new RecurringJob
        {
            Name = "future-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = now.AddMinutes(-10),
            NextExecution = now.AddMinutes(5),
        };
        ctx.Set<RecurringJob>().Add(recurringJob);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var schedCtx = _fixture.CreateContext();
        var result = await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ScheduleRecurringJobsAsync(CancellationToken.None);

        // Assert
        result.Scheduled.ShouldBe(0);
    }

    [TimedFact]
    public async Task ScheduleRecurringJobs_DedupChecksLatestLog_NotOldest()
    {
        // Arrange — oldest log has completed job, latest log has enqueued job → should skip
        var ctx = _fixture.CreateContext();
        var now = DateTime.UtcNow;

        var oldJobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = oldJobId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = now.AddMinutes(-10),
            ScheduleTime = now.AddMinutes(-10),
            Queue = "default",
        });

        var newJobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = newJobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = now.AddMinutes(-1),
            ScheduleTime = now.AddMinutes(-1),
            Queue = "default",
        });

        var recurringJob = new RecurringJob
        {
            Name = "dedup-order-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "* * * * *",
            CreatedAt = now.AddMinutes(-20),
            NextExecution = now.AddMinutes(-1),
        };
        ctx.Set<RecurringJob>().Add(recurringJob);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Old log (completed job) — should NOT be the one checked
        ctx.Set<RecurringJobLog>().Add(new RecurringJobLog
        {
            RecurringJobId = recurringJob.Id,
            JobId = oldJobId,
            CreatedAt = now.AddMinutes(-10),
        });

        // Latest log (enqueued job) — should BE the one checked
        ctx.Set<RecurringJobLog>().Add(new RecurringJobLog
        {
            RecurringJobId = recurringJob.Id,
            JobId = newJobId,
            CreatedAt = now.AddMinutes(-1),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var schedCtx = _fixture.CreateContext();
        var result = await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ScheduleRecurringJobsAsync(CancellationToken.None);

        // Assert — should skip because latest log's job is Enqueued
        result.Scheduled.ShouldBe(0);
    }
}

file class FakeTimeProvider(DateTime utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
}
