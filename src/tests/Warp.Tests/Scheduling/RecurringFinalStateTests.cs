using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Models;
using Warp.Core.Notifications;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Scheduling;

/// <summary>
/// ExpirationCleanup preserves a recurring firing's outcome on its RecurringJobLog before deleting
/// the Job row it points at (§8.9). Without the stamp, a low-frequency definition reads as "cleaned
/// up" for every run it ever made once JobExpirationTimeout (1 day) passes — and a Deleted outcome
/// (a skip-mode refusal, a graceful cancel) was indistinguishable from a success.
/// </summary>
[GenerateDatabaseTests]
public abstract class RecurringFinalStateTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected RecurringFinalStateTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task RunCleanup_ExpiredRecurringJob_StampsOutcomeOnTheAuditRow()
    {
        var ctx = _fixture.CreateContext();
        var (_, logId) = await InsertFiring(ctx, State.Completed, expired: true);

        await TestTasks.CreateExpirationCleanup(_fixture.CreateContext(), TimeProvider.System)
            .RunCleanupAsync(CancellationToken.None);

        // The job row is gone and JobId nulled by SetNull, but the outcome survives on the log.
        var log = await _fixture.CreateContext().Set<RecurringJobLog>()
            .FirstAsync(x => x.Id == logId, Xunit.TestContext.Current.CancellationToken);

        log.JobId.ShouldBeNull();
        log.FinalState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task RunCleanup_DeletedRecurringJob_PreservesTheDeletedOutcome()
    {
        // The case the old "Cleaned up" label hid entirely: a refused or cancelled run read as a success.
        var ctx = _fixture.CreateContext();
        var (_, logId) = await InsertFiring(ctx, State.Deleted, expired: true);

        await TestTasks.CreateExpirationCleanup(_fixture.CreateContext(), TimeProvider.System)
            .RunCleanupAsync(CancellationToken.None);

        var log = await _fixture.CreateContext().Set<RecurringJobLog>()
            .FirstAsync(x => x.Id == logId, Xunit.TestContext.Current.CancellationToken);

        log.FinalState.ShouldBe(State.Deleted);
    }

    [TimedFact]
    public async Task RunCleanup_LiveJob_LeavesFinalStateNull()
    {
        // Nothing was deleted, so the live Job row stays the source of truth for the outcome.
        var ctx = _fixture.CreateContext();
        var (jobId, logId) = await InsertFiring(ctx, State.Completed, expired: false);

        await TestTasks.CreateExpirationCleanup(_fixture.CreateContext(), TimeProvider.System)
            .RunCleanupAsync(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var log = await readCtx.Set<RecurringJobLog>()
            .FirstAsync(x => x.Id == logId, Xunit.TestContext.Current.CancellationToken);

        log.JobId.ShouldBe(jobId);
        log.FinalState.ShouldBeNull();
    }

    [TimedFact]
    public async Task RunCountBasedCleanup_ExpiredRecurringJob_StampsOutcomeToo()
    {
        // The second delete path — MaxExpirableJobCount — must not lose what the age sweep preserves.
        var ctx = _fixture.CreateContext();
        var (_, logId) = await InsertFiring(ctx, State.Completed, expired: true);

        await TestTasks.CreateExpirationCleanup(_fixture.CreateContext(), TimeProvider.System)
            .RunCountBasedCleanupAsync(0, 1000, CancellationToken.None);

        var log = await _fixture.CreateContext().Set<RecurringJobLog>()
            .FirstAsync(x => x.Id == logId, Xunit.TestContext.Current.CancellationToken);

        log.JobId.ShouldBeNull();
        log.FinalState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task GetRecurringJobs_AfterCleanup_ReportsTheStampedOutcomeAsCleanedUp()
    {
        var ctx = _fixture.CreateContext();
        await InsertFiring(ctx, State.Completed, expired: true);

        await TestTasks.CreateExpirationCleanup(_fixture.CreateContext(), TimeProvider.System)
            .RunCleanupAsync(CancellationToken.None);

        var svc = NewService();
        var page = await svc.GetRecurringJobs(new BaseListRequest());
        var item = page.Items.ShouldHaveSingleItem();

        // Outcome known, nothing to link to — the dashboard renders "Completed (cleaned up)".
        item.HasLastRun.ShouldBeTrue();
        item.LastState.ShouldBe(State.Completed);
        item.LastRunCleanedUp.ShouldBeTrue();
        item.LastJobId.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetRecurringJobs_LiveJob_ReportsTheLiveStateAndIsNotCleanedUp()
    {
        var ctx = _fixture.CreateContext();
        var (jobId, _) = await InsertFiring(ctx, State.Failed, expired: false);

        var svc = NewService();
        var page = await svc.GetRecurringJobs(new BaseListRequest());
        var item = page.Items.ShouldHaveSingleItem();

        item.LastState.ShouldBe(State.Failed);
        item.LastRunCleanedUp.ShouldBeFalse();
        item.LastJobId.ShouldBe(jobId);
    }

    [TimedFact]
    public async Task GetRecurringJobHistory_AfterCleanup_KeepsTheOutcomeWithoutAJobRow()
    {
        var ctx = _fixture.CreateContext();
        await InsertFiring(ctx, State.Completed, expired: true);

        await TestTasks.CreateExpirationCleanup(_fixture.CreateContext(), TimeProvider.System)
            .RunCleanupAsync(CancellationToken.None);

        var svc = NewService();
        var page = await svc.GetRecurringJobHistory("final-state-test", new BaseListRequest());
        var entry = page.Items.ShouldHaveSingleItem();

        entry.JobExists.ShouldBeFalse();
        entry.CurrentState.ShouldBe(State.Completed);
        entry.Skipped.ShouldBeFalse();
    }

    [TimedFact]
    public async Task RunCleanup_SkippedFiring_LeavesFinalStateNull()
    {
        // A skip never ran anything, so there is no outcome to preserve.
        var ctx = _fixture.CreateContext();
        var definition = NewDefinition();
        ctx.Set<RecurringJob>().Add(definition);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var log = new RecurringJobLog
        {
            RecurringJobId = definition.Id,
            JobId = null,
            Skipped = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        ctx.Set<RecurringJobLog>().Add(log);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await TestTasks.CreateExpirationCleanup(_fixture.CreateContext(), TimeProvider.System)
            .RunCleanupAsync(CancellationToken.None);

        var readCtx = _fixture.CreateContext();
        var read = await readCtx.Set<RecurringJobLog>()
            .FirstAsync(x => x.Id == log.Id, Xunit.TestContext.Current.CancellationToken);

        read.FinalState.ShouldBeNull();
        read.Skipped.ShouldBeTrue();
    }

    private static RecurringJob NewDefinition()
    {
        return new RecurringJob
        {
            Name = "final-state-test",
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            Cron = "0 0 1 * *",
            CreatedAt = DateTime.UtcNow.AddDays(-90),
            LastExecution = DateTime.UtcNow.AddDays(-30),
            NextExecution = DateTime.UtcNow.AddDays(1),
        };
    }

    // One firing: a definition, the job it created, and the audit row linking them. `expired` decides
    // whether the cleanup sweep is allowed to take the job.
    private static async Task<(Guid JobId, int LogId)> InsertFiring(TestContext ctx, State state, bool expired)
    {
        var definition = NewDefinition();
        ctx.Set<RecurringJob>().Add(definition);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            Type = definition.Type,
            CurrentState = state,
            CreateTime = DateTime.UtcNow.AddDays(-30),
            ScheduleTime = DateTime.UtcNow.AddDays(-30),
            Queue = "default",
            ExpireAt = expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddDays(1),
        };
        ctx.Set<Job>().Add(job);

        var log = new RecurringJobLog
        {
            RecurringJobId = definition.Id,
            JobId = job.Id,
            Skipped = false,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
        };
        ctx.Set<RecurringJobLog>().Add(log);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return (job.Id, log.Id);
    }

    private RecurringJobService<TestContext> NewService()
    {
        return new RecurringJobService<TestContext>(
            _fixture.CreateContext(),
            TimeProvider.System,
            new NullNotificationTransport(),
            TestTasks.NullSignals);
    }
}
