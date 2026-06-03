using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
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
        var count = await Warp.Tests.Helpers.TestTasks.CreateRecurringJobScheduler(schedCtx, TimeProvider.System).ScheduleRecurringJobsAsync(CancellationToken.None);

        // Assert
        count.ShouldBeGreaterThanOrEqualTo(1);

        var jobCountAfter = await _fixture.CreateContext().Set<Job>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        jobCountAfter.ShouldBeGreaterThan(jobCountBefore);
    }
}
