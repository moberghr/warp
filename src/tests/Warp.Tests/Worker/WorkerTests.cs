using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Worker;

[GenerateDatabaseTests]
public abstract class WorkerTestsBase : IAsyncLifetime
{
    // Counter increments are summed here rather than written as rows. Tests that assert on
    // Counter/Statistic rows flush it with TestTasks.FlushCountersAsync before reading.
    private readonly WarpCounterBuffer _counterBuffer = new();

    private readonly IDatabaseFixture _fixture;
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();

    protected WorkerTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();

        await TestWorkerHarness.SeedServerAndWorkerAsync(_fixture, ServerId, WorkerId, Xunit.TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private (WarpWorkerService<TestContext> Worker, IServiceScopeFactory ScopeFactory) CreateWorker(string[]? queues = null, ServerTaskSignals<TestContext>? signals = null) =>
        TestWorkerHarness.Create(_fixture, _counterBuffer, ServerId, WorkerId, queues, signals);

    [TimedFact]
    public async Task GetAndProcessJob_EmptyQueue_ReturnsFalse()
    {
        // Arrange
        var (worker, _) = CreateWorker();

        // Act
        var result = await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        result.ShouldBeFalse();
    }

    [TimedFact]
    public async Task GetAndProcessJob_CompletesJob()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, _) = CreateWorker();

        // Act
        var result = await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        result.ShouldBeTrue();
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);
        job.HandlerType.ShouldNotBeNull();

        // The worker let go of the row, and a completed job expires.
        job.CurrentWorkerId.ShouldBeNull();
        job.LastKeepAlive.ShouldBeNull();
        job.CancellationMode.ShouldBe(CancellationMode.None);
        job.ExpireAt.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task GetAndProcessJob_FailingJob_MarksFailed()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, _) = CreateWorker();

        // Act
        var result = await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        result.ShouldBeTrue();
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Failed);

        // The worker let go of the row, and a failed job never expires (it waits for an operator).
        job.CurrentWorkerId.ShouldBeNull();
        job.LastKeepAlive.ShouldBeNull();
        job.CancellationMode.ShouldBe(CancellationMode.None);
        job.ExpireAt.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetAndProcessJob_AfterTheJobIsFinalized_SignalsJobFinalized()
    {
        // The Orchestrator waits on this signal to finalize a batch or message as soon as its last
        // child settles. Without it a parent waits out the Orchestrator's own interval.
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var signals = new ServerTaskSignals<TestContext>();
        var finalized = 0;
        using var subscription = signals.Subscribe(ServerTaskSignal.JobFinalized, () => Interlocked.Increment(ref finalized));
        var (worker, _) = CreateWorker(signals: signals);

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        Volatile.Read(ref finalized).ShouldBe(1);
    }

    [TimedFact]
    public async Task GetAndProcessJob_HandlerThrowsItsOwnCancellation_MarksFailedNotCancelled()
    {
        // Nobody cancelled the job: the handler threw OperationCanceledException by itself, the way an
        // inner timeout or a cancelled HTTP call does. That is a failure. Only a cancellation the
        // worker itself requested (a delete while running) ends a job as Deleted.
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowsOwnCancellationRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowsOwnCancellationRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, _) = CreateWorker();

        // Act
        var result = await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        result.ShouldBeTrue();
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);
        job.CurrentWorkerId.ShouldBeNull();
        job.ExpireAt.ShouldBeNull();

        var logs = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Select(x => x.EventType)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        logs.ShouldContain("Failed");
        logs.ShouldNotContain("Cancelled");
    }

    // Rows a worker can claim but cannot run. Each must end Failed with the worker's hold released and
    // a Failed log saying why: a row left Processing would only be found by StaleJobRecovery, which
    // would requeue it to fail the same way again.
    [TimedTheory]
    [InlineData("Not.A.Real.Type, Not.A.Real.Assembly", "{}", null)]
    [InlineData(null, "{}", null)]
    [InlineData("unit", "this is not json", null)]
    [InlineData("unit", "{}", "{ this is not json")]
    public async Task GetAndProcessJob_RowThatCannotRun_IsFailedAndReleased(string? type, string message, string? metadata)
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = string.Equals(type, "unit", StringComparison.Ordinal) ? typeof(UnitRequest).AssemblyQualifiedName : type,
            Message = message,
            Metadata = metadata,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, _) = CreateWorker();

        // Act
        var result = await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        result.ShouldBeTrue();
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);
        job.CurrentWorkerId.ShouldBeNull();
        job.LastKeepAlive.ShouldBeNull();
        job.ExpireAt.ShouldBeNull();

        var logs = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Select(x => x.EventType)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        logs.ShouldContain("Failed");
    }

    [TimedFact]
    public async Task GetAndProcessJob_SkipsScheduledStateJob()
    {
        // Future-dated jobs live in State.Scheduled and are promoted to Enqueued by
        // ScheduledJobActivationTask. The worker only queries State.Enqueued rows, so a
        // Scheduled row (regardless of its ScheduleTime) must be invisible to fetch.
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Scheduled,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow.AddHours(2),
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, _) = CreateWorker();

        // Act
        var result = await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        result.ShouldBeFalse();
    }

    [TimedFact]
    public async Task GetAndProcessJob_SkipsNonSubscribedQueue()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "other",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, _) = CreateWorker(["default"]);

        // Act
        var result = await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        result.ShouldBeFalse();
    }

    [TimedFact]
    public async Task GetAndProcessJob_OnlyPicksJobKind()
    {
        // Arrange
        var ctx = _fixture.CreateContext();

        // Message-kind job (should be skipped by worker)
        ctx.Set<Job>().Add(new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Message,
            CurrentState = State.Enqueued,
            Type = typeof(SingleHandlerMessage).AssemblyQualifiedName,
            Message = "{}",
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });

        // Job-kind job (should be picked up)
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new UnitRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (worker, _) = CreateWorker();

        // Act
        var result = await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        result.ShouldBeTrue();
        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ShouldNotBeNull();
        job.CurrentState.ShouldBe(State.Completed);
    }
}
