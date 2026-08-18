using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Handlers;
using Warp.Core.Notifications;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker.Services;

namespace Warp.Tests.Reliability;

[GenerateDatabaseTests]
public abstract class CrashRecoveryTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected CrashRecoveryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task RequeueStaleJobs_MultipleStaleJobs_AllRequeued()
    {
        // Arrange — insert 5 stale Processing jobs
        var ctx = _fixture.CreateContext();
        var jobIds = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var jobId = Guid.NewGuid();
            jobIds.Add(jobId);
            ctx.Set<Job>().Add(new Job
            {
                Id = jobId,
                Kind = JobKind.Job,
                CurrentState = State.Processing,
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = "default",
                LastKeepAlive = DateTime.UtcNow.AddMinutes(-10),
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var result = await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5))
            .RecoverStaleJobsAsync(CancellationToken.None);

        // Assert
        result.Requeued.ShouldBe(5);
        var readCtx = _fixture.CreateContext();
        foreach (var id in jobIds)
        {
            var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == id, Xunit.TestContext.Current.CancellationToken);
            job.CurrentState.ShouldBe(State.Enqueued);
        }
    }

    [TimedFact]
    public async Task RequeueStaleJobs_NonProcessingJobs_NotAffected()
    {
        // Arrange — insert jobs in Completed, Failed, and Enqueued states with old keepalive
        var ctx = _fixture.CreateContext();
        var staleTime = DateTime.UtcNow.AddMinutes(-10);

        var completedId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = completedId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = staleTime,
        });

        var failedId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = failedId,
            Kind = JobKind.Job,
            CurrentState = State.Failed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = staleTime,
        });

        var enqueuedId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = enqueuedId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = staleTime,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var result = await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5))
            .RecoverStaleJobsAsync(CancellationToken.None);

        // Assert
        result.Total.ShouldBe(0);

        var readCtx = _fixture.CreateContext();
        (await readCtx.Set<Job>().FirstAsync(j => j.Id == completedId, Xunit.TestContext.Current.CancellationToken)).CurrentState.ShouldBe(State.Completed);
        (await readCtx.Set<Job>().FirstAsync(j => j.Id == failedId, Xunit.TestContext.Current.CancellationToken)).CurrentState.ShouldBe(State.Failed);
        (await readCtx.Set<Job>().FirstAsync(j => j.Id == enqueuedId, Xunit.TestContext.Current.CancellationToken)).CurrentState.ShouldBe(State.Enqueued);
    }

    [TimedFact]
    public async Task RequeueStaleJobs_StaleJob_RetriedTimesNotIncremented()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = DateTime.UtcNow.AddMinutes(-10),
            Metadata = """{"RetriedTimes":2}""",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5))
            .RecoverStaleJobsAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Enqueued);

        // Stale recovery must not count as a retry — the retry counter (in metadata) is untouched.
        var metadata = MetadataSerializer.Deserialize(job.Metadata)!;
        Convert.ToInt32(metadata["RetriedTimes"]).ShouldBe(2);
    }

    [TimedFact]
    public async Task RequeueStaleJobs_ConcurrentCalls_OnlyOnceRequeued()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = DateTime.UtcNow.AddMinutes(-10),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act — run 5 concurrent requeue attempts
        var tasks = Enumerable.Range(0, 5)
            .Select(_ =>
            {
                var ctx = _fixture.CreateContext();
                return TestTasks.CreateStaleJobRecovery(ctx, TimeProvider.System, TimeSpan.FromMinutes(5))
                    .RecoverStaleJobsAsync(CancellationToken.None);
            })
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Assert — exactly 1 should have requeued the job
        results.Sum(x => x.Requeued).ShouldBe(1);

        var logs = await _fixture.CreateContext().Set<JobLog>()
            .Where(x => x.JobId == jobId && x.EventType == "Requeued")
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        logs.Count.ShouldBe(1);
    }

    [TimedFact]
    public async Task CleanUpServers_DeadServerWithProcessingJob_JobStateUnchanged()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var serverId = Guid.NewGuid();
        var workerId = Guid.NewGuid();

        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = DateTime.UtcNow.AddHours(-2),
            LastHeartbeatTime = DateTime.UtcNow.AddMinutes(-10),
            ServiceCount = 1,
        });
        ctx.Set<Warp.Core.Data.Entities.Worker>().Add(new Warp.Core.Data.Entities.Worker
        {
            Id = workerId,
            ServerId = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow.AddMinutes(-10),
        });

        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            CurrentWorkerId = workerId,
            LastKeepAlive = DateTime.UtcNow.AddMinutes(-10),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act — cleanup only removes server/workers, not jobs
        await TestTasks
            .CreateServerCleanup(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5))
            .CleanUpServersAsync(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Processing); // Unchanged — StaleJobRecovery handles this
    }

    [TimedFact]
    public async Task CleanUpServers_CombinedRecovery_JobsRequeuedAndServerCleaned()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var serverId = Guid.NewGuid();
        var workerId = Guid.NewGuid();

        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = DateTime.UtcNow.AddHours(-2),
            LastHeartbeatTime = DateTime.UtcNow.AddMinutes(-10),
            ServiceCount = 1,
        });
        ctx.Set<Warp.Core.Data.Entities.Worker>().Add(new Warp.Core.Data.Entities.Worker
        {
            Id = workerId,
            ServerId = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow.AddMinutes(-10),
        });

        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            CurrentWorkerId = workerId,
            LastKeepAlive = DateTime.UtcNow.AddMinutes(-10),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act — run both cleanup + recovery (as health manager would)
        var recovery = await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5))
            .RecoverStaleJobsAsync(CancellationToken.None);
        var removed = await TestTasks
            .CreateServerCleanup(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5))
            .CleanUpServersAsync(CancellationToken.None);

        // Assert
        recovery.Requeued.ShouldBe(1);
        removed.ShouldBe(1);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Enqueued);

        var servers = await readCtx.Set<Server>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        servers.ShouldBe(0);
    }

    [TimedFact]
    public async Task RequeueStaleJobs_KeepAliveAtExactCutoff_NotRequeued()
    {
        // Arrange — job with LastKeepAlive exactly at cutoff should NOT be requeued (strict < comparison)
        var ctx = _fixture.CreateContext();
        var timeout = TimeSpan.FromMinutes(5);
        var now = DateTime.UtcNow.AddMinutes(10);
        var exactCutoff = now - timeout;

        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = exactCutoff,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var tp = new FakeTimeProvider(now);
        var result = await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), tp, timeout)
            .RecoverStaleJobsAsync(CancellationToken.None);

        // Assert — should NOT be requeued (at boundary, not past it)
        result.Total.ShouldBe(0);
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Processing);
    }

    [TimedFact]
    public async Task CleanUpServers_HeartbeatAtExactTimeout_NotCleaned()
    {
        // Arrange — server with heartbeat exactly at timeout boundary should NOT be cleaned.
        // Round `now` to microsecond precision so LastHeartbeatTime survives PostgreSQL round-trip
        // (timestamp has 6-digit precision; raw .NET ticks have 7 digits). Without this, the saved
        // value would be truncated and `now - savedHeartbeat` would exceed timeout by sub-microsecond
        // ticks, falsely tripping the `>` boundary check.
        var ctx = _fixture.CreateContext();
        var timeout = TimeSpan.FromMinutes(5);
        var rawNow = DateTime.UtcNow.AddMinutes(10);
        var now = new DateTime(rawNow.Ticks - (rawNow.Ticks % 10), DateTimeKind.Utc);

        var serverId = Guid.NewGuid();
        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = now.AddHours(-1),
            LastHeartbeatTime = now - timeout,
            ServiceCount = 1,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var tp = new FakeTimeProvider(now);
        await TestTasks
            .CreateServerCleanup(_fixture.CreateContext(), tp, timeout)
            .CleanUpServersAsync(CancellationToken.None);

        // Assert — server should still exist
        var readCtx = _fixture.CreateContext();
        var server = await readCtx.Set<Server>().FindAsync([serverId], Xunit.TestContext.Current.CancellationToken);
        server.ShouldNotBeNull();
    }

    /// <summary>
    /// CRITICAL #1: Stale recovery must respect CancellationMode.
    /// If a job has CancellationMode=Graceful (user called DeleteJob), stale recovery
    /// should set it to Deleted, NOT requeue it.
    /// </summary>
    [TimedFact]
    public async Task StaleRecovery_WithCancellationModeGraceful_SetsDeletedNotEnqueued()
    {
        // Arrange: a processing job with CancellationMode=Graceful and stale keep-alive
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CancellationMode = CancellationMode.Graceful,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = DateTime.UtcNow.AddMinutes(-10), // stale
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act: run stale recovery
        var recoveryCtx = _fixture.CreateContext();
        await TestTasks
            .CreateStaleJobRecovery(recoveryCtx, TimeProvider.System, TimeSpan.FromMinutes(5))
            .RecoverStaleJobsAsync(CancellationToken.None);

        // Assert: job should be Deleted (not Enqueued) because user intended to cancel it
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FindAsync([jobId], Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Deleted, "Stale job with CancellationMode=Graceful should be Deleted, not requeued");
        job.CancellationMode.ShouldBe(CancellationMode.None);
    }

    [TimedFact]
    public async Task RecoverStaleJobs_RequeuedJob_WakesWorkersOnItsOwnCommit()
    {
        // Regression: a recovered job landed in Enqueued with no wake at all. NotificationDispatch's
        // CapturePending only sees ADDED Job rows and a requeue is Modified, so recovery was the one
        // enqueue site that announced nothing — the job then waited out a worker's backoff poll, up to
        // MaxPollingInterval (which UseDatabasePush raises to 5 minutes) after the crash it recovers from.
        await SeedStaleProcessingJobAsync();

        var transport = new RecordingNotificationTransport();
        var signals = new ServerTaskSignals<TestContext>();
        var woken = 0;
        using var subscription = signals.Subscribe(ServerTaskSignal.JobEnqueued, () => woken++);

        await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5), signals: signals, transport: transport)
            .RecoverStaleJobsAsync(CancellationToken.None);

        // No outer transaction, so the sweep owns and commits its own — the wake is durable and fires here.
        woken.ShouldBe(1);
        transport.Published.Count.ShouldBe(1);
        transport.Published[0].Kind.ShouldBe(NotificationKind.JobEnqueued);
        transport.Published[0].Queue.ShouldBe("default");
    }

    [TimedFact]
    public async Task RecoverStaleJobs_RequeuedJob_DoesNotWakeBeforeCommit()
    {
        // Under the server-task host the sweep runs inside the lock transaction, so the requeue is not
        // durable when it returns. Waking a worker here sends it querying for a row it cannot see (§8.25).
        await SeedStaleProcessingJobAsync();

        var transport = new RecordingNotificationTransport();
        var signals = new ServerTaskSignals<TestContext>();
        var woken = 0;
        using var subscription = signals.Subscribe(ServerTaskSignal.JobEnqueued, () => woken++);

        var ctx = _fixture.CreateContext();
        await using var outerTx = await ctx.Database.BeginTransactionAsync(Xunit.TestContext.Current.CancellationToken);

        await TestTasks
            .CreateStaleJobRecovery(ctx, TimeProvider.System, TimeSpan.FromMinutes(5), signals: signals, transport: transport)
            .RecoverStaleJobsAsync(CancellationToken.None);

        woken.ShouldBe(0);
        transport.Published.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task OnCommitted_AfterStaleJobRequeued_FiresJobEnqueued()
    {
        await SeedStaleProcessingJobAsync();

        var transport = new RecordingNotificationTransport();
        var signals = new ServerTaskSignals<TestContext>();
        var woken = 0;
        using var subscription = signals.Subscribe(ServerTaskSignal.JobEnqueued, () => woken++);

        var ctx = _fixture.CreateContext();
        await using var outerTx = await ctx.Database.BeginTransactionAsync(Xunit.TestContext.Current.CancellationToken);
        var recovery = TestTasks.CreateStaleJobRecovery(ctx, TimeProvider.System, TimeSpan.FromMinutes(5), signals: signals, transport: transport);
        await recovery.RecoverStaleJobsAsync(CancellationToken.None);
        await outerTx.CommitAsync(Xunit.TestContext.Current.CancellationToken);

        // Act: the host calls this once the lock transaction has committed.
        await recovery.OnCommittedAsync(CancellationToken.None);

        woken.ShouldBe(1);
        transport.Published.Count.ShouldBe(1);
        transport.Published[0].Kind.ShouldBe(NotificationKind.JobEnqueued);
        transport.Published[0].Queue.ShouldBe("default");
    }

    [TimedFact]
    public async Task RecoverStuckWebhookDeliveries_StagedJob_WakesWorkersOnTheWebhooksQueue()
    {
        // The staged executor job replaced an IPublisher.Enqueue, which announced itself via
        // Publisher.SaveChangesAsync. Staging directly bypasses that, so the sweep has to announce the row
        // itself or a recovered delivery sits on warp:webhooks until a worker's backoff poll finds it.
        await SeedStuckDeliveryAsync();

        var transport = new RecordingNotificationTransport();
        var signals = new ServerTaskSignals<TestContext>();
        var woken = 0;
        using var subscription = signals.Subscribe(ServerTaskSignal.JobEnqueued, () => woken++);

        var recovered = await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5), signals: signals, transport: transport)
            .RecoverStuckWebhookDeliveriesAsync(CancellationToken.None);

        // No outer transaction, so the sweep owns and commits its own — the wake is durable and fires here.
        recovered.ShouldBe(1);
        woken.ShouldBe(1);
        transport.Published.Count.ShouldBe(1);
        transport.Published[0].Kind.ShouldBe(NotificationKind.JobEnqueued);
        transport.Published[0].Queue.ShouldBe("warp:webhooks");
    }

    [TimedFact]
    public async Task Execute_StaleJobAndStuckDeliveryInOneSweep_WakesBothQueues()
    {
        // Both sweeps buffer into the same set, so the webhook sweep must UNION its CapturePending result
        // rather than assign it — assigning drops the job sweep's requeue wake, and only under the task
        // host's transaction (where neither sweep drains for itself) does that loss actually show.
        await SeedStaleProcessingJobAsync();
        await SeedStuckDeliveryAsync();

        var transport = new RecordingNotificationTransport();
        var signals = new ServerTaskSignals<TestContext>();
        var woken = 0;
        using var subscription = signals.Subscribe(ServerTaskSignal.JobEnqueued, () => woken++);

        var ctx = _fixture.CreateContext();
        await using var outerTx = await ctx.Database.BeginTransactionAsync(Xunit.TestContext.Current.CancellationToken);
        var recovery = TestTasks.CreateStaleJobRecovery(ctx, TimeProvider.System, TimeSpan.FromMinutes(5), signals: signals, transport: transport);
        await recovery.ExecuteAsync(CancellationToken.None);
        await outerTx.CommitAsync(Xunit.TestContext.Current.CancellationToken);

        await recovery.OnCommittedAsync(CancellationToken.None);

        woken.ShouldBe(2);
        transport.Published.Count.ShouldBe(2);
        transport.Published.ShouldContain(n => n.Kind == NotificationKind.JobEnqueued && n.Queue == "default");
        transport.Published.ShouldContain(n => n.Kind == NotificationKind.JobEnqueued && n.Queue == "warp:webhooks");
    }

    [TimedFact]
    public async Task RecoverStuckWebhookDeliveries_StagedJob_RootsItsOwnTraceAndCarriesTheApplication()
    {
        // Provenance the replaced IPublisher path used to supply: it stamped Application from config and
        // fell back to rooting the trace at the job id when no caller trace was ambient — which is always
        // the case for a recovery sweep. (That the server options carry the Core-level ApplicationName at
        // all in the two-builder shape is guaranteed by the registration merge — see
        // WarpConfigurationMergeTests.)
        await SeedStuckDeliveryAsync();

        await TestTasks
            .CreateStaleJobRecovery(_fixture.CreateContext(), TimeProvider.System, TimeSpan.FromMinutes(5), applicationName: "recovery-app")
            .RecoverStuckWebhookDeliveriesAsync(CancellationToken.None);

        var job = await _fixture.CreateContext().Set<Job>()
            .Where(x => x.Queue == "warp:webhooks")
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

        job.TraceId.ShouldBe(job.Id);
        job.Application.ShouldBe("recovery-app");
    }

    private async Task SeedStuckDeliveryAsync()
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [],
            Status = WebhookDeliveryStatus.Pending,
            AttemptCount = 1,
            NextAttemptAt = DateTime.UtcNow.AddHours(-1),
            CreatedAt = DateTime.UtcNow.AddHours(-2),
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SeedStaleProcessingJobAsync()
    {
        var jobId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
            LastKeepAlive = DateTime.UtcNow.AddMinutes(-10),
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return jobId;
    }

    private sealed class RecordingNotificationTransport : IWarpNotificationTransport
    {
        public List<(NotificationKind Kind, string? Queue)> Published { get; } = [];

        public Task ListenerReady { get; } = Task.CompletedTask;

        public Task PublishAsync(NotificationKind kind, string? queue, CancellationToken ct)
        {
            Published.Add((kind, queue));

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<Notification> ListenAsync([EnumeratorCancellation] CancellationToken ct)
        {
            // Test-only — listening is irrelevant for the publish-side regression.
            await Task.Yield();

            yield break;
        }
    }
}

file class FakeTimeProvider(DateTime utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
}
