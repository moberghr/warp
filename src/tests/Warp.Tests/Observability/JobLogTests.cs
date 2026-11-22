using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Observability;

[GenerateDatabaseTests]
public abstract class JobLogTestsBase : IAsyncLifetime
{
    // Counter increments are summed here rather than written as rows. Tests that assert on
    // Counter/Statistic rows flush it with TestTasks.FlushCountersAsync before reading.
    private readonly WarpCounterBuffer _counterBuffer = new();

    private readonly IDatabaseFixture _fixture;
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();

    protected JobLogTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();

        await TestWorkerHarness.SeedServerAndWorkerAsync(_fixture, ServerId, WorkerId, Xunit.TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private WarpWorkerService<TestContext> CreateWorker() =>
        TestWorkerHarness.Create(_fixture, _counterBuffer, ServerId, WorkerId).Worker;

    [TimedFact]
    public async Task GetAndProcessJob_CreatedJob_HasCreatedLog()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = new Publisher<TestContext>(ctx, Options.Create(new WarpConfiguration()), TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);
        var jobId = await publisher.Enqueue(new UnitRequest());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var logs = await readCtx.Set<JobLog>()
            .Where(l => l.JobId == jobId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        logs.ShouldContain(l => l.EventType == "Created");
    }

    [TimedFact]
    public async Task GetAndProcessJob_CompletedJob_HasFullLifecycleLogs()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var publisher = new Publisher<TestContext>(ctx, Options.Create(new WarpConfiguration()), TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);
        var jobId = await publisher.Enqueue(new UnitRequest());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var logs = await readCtx.Set<JobLog>()
            .Where(l => l.JobId == jobId)
            .OrderBy(l => l.Timestamp)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        logs.ShouldContain(l => l.EventType == "Created");
        logs.ShouldContain(l => l.EventType == "Processing");
        logs.ShouldContain(l => l.EventType == "Completed");

        var created = logs.First(l => string.Equals(l.EventType, "Created", StringComparison.Ordinal));
        var processing = logs.First(l => string.Equals(l.EventType, "Processing", StringComparison.Ordinal));
        var completed = logs.First(l => string.Equals(l.EventType, "Completed", StringComparison.Ordinal));
        processing.Timestamp.ShouldBeGreaterThanOrEqualTo(created.Timestamp);
        completed.Timestamp.ShouldBeGreaterThanOrEqualTo(processing.Timestamp);
    }

    [TimedFact]
    public async Task GetAndProcessJob_FailedJob_HasFailedLogWithErrorLevel()
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

        var worker = CreateWorker();

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        var readCtx = _fixture.CreateContext();
        var logs = await readCtx.Set<JobLog>()
            .Where(l => l.JobId == jobId)
            .OrderBy(l => l.Timestamp)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        logs.ShouldContain(l => l.EventType == "Failed" && l.Level == "Error");
        logs.ShouldNotContain(l => l.EventType == "Completed");
    }

    [TimedFact]
    public async Task RequeueJob_CreatesRequeuedLog()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var jobId = Guid.NewGuid();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = "default",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(jobId);

        // Assert
        var readCtx = _fixture.CreateContext();
        var logs = await readCtx.Set<JobLog>()
            .Where(l => l.JobId == jobId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        logs.ShouldContain(l => l.EventType == "Requeued");
    }
}
