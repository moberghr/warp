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

namespace Warp.Tests.Scheduling;

[GenerateDatabaseTests]
public abstract class RetentionTestsBase : IAsyncLifetime
{
    // Counter increments are summed here rather than written as rows. Tests that assert on
    // Counter/Statistic rows flush it with TestTasks.FlushCountersAsync before reading.
    private readonly WarpCounterBuffer _counterBuffer = new();

    private readonly IDatabaseFixture _fixture;
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();

    protected RetentionTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();

        await TestWorkerHarness.SeedServerAndWorkerAsync(_fixture, ServerId, WorkerId, Xunit.TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private WarpWorkerService<TestContext> CreateWorker() =>
        TestWorkerHarness.Create(_fixture, _counterBuffer, ServerId, WorkerId).Worker;

    [TimedFact]
    public async Task DeleteJob_IncrementsDeletedStat()
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

        var statBefore = await _fixture.CreateContext().Set<Statistic>()
            .Where(x => x.Key == "stats:deleted")
            .Select(x => x.Value)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.DeleteJob(jobId);

        await Warp.Tests.Helpers.TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(CancellationToken.None);

        // Assert
        var statAfter = await _fixture.CreateContext().Set<Statistic>()
            .Where(x => x.Key == "stats:deleted")
            .Select(x => x.Value)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        statAfter.ShouldBe(statBefore + 1);
    }

    [TimedFact]
    public async Task RequeueJob_LeavesSucceededStat()
    {
        // Arrange — enqueue and process a job so stats:succeeded is incremented
        var ctx = _fixture.CreateContext();
        var publisher = new Publisher<TestContext>(ctx, Options.Create(new WarpConfiguration()), TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);
        var jobId = await publisher.Enqueue(new UnitRequest());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var worker = CreateWorker();
        await worker.GetAndProcessJob(CancellationToken.None);
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());

        await Warp.Tests.Helpers.TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(CancellationToken.None);

        var succeededBefore = await _fixture.CreateContext().Set<Statistic>()
            .Where(x => x.Key == "stats:succeeded")
            .Select(x => x.Value)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = Warp.Tests.Helpers.TestTasks.CreateJobCommandService(_fixture.CreateContext());
        await svc.RequeueJob(jobId);

        await Warp.Tests.Helpers.TestTasks.CreateCounterAggregator(_fixture.CreateContext()).AggregateCountersAsync(CancellationToken.None);

        // Assert
        var succeededAfter = await _fixture.CreateContext().Set<Statistic>()
            .Where(x => x.Key == "stats:succeeded")
            .Select(x => x.Value)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        // Requeueing a completed job does not retract the success it already recorded.
        succeededAfter.ShouldBe(succeededBefore);
    }

    [TimedFact]
    public async Task ExpirationCleanup_DeletesExpiredJobAndLogs()
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
            ExpireAt = DateTime.UtcNow.AddHours(-1),
        });
        ctx.Set<JobLog>().Add(new JobLog
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            EventType = "Created",
            Timestamp = DateTime.UtcNow,
            Level = "Information",
            Message = "Test log entry",
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var cleanCtx = _fixture.CreateContext();
        var cleaned = await Warp.Tests.Helpers.TestTasks.CreateExpirationCleanup(cleanCtx, TimeProvider.System).RunCleanupAsync(CancellationToken.None);

        // Assert
        cleaned.ShouldBeGreaterThanOrEqualTo(1);

        var readCtx = _fixture.CreateContext();
        var job = await readCtx.Set<Job>().FirstOrDefaultAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.ShouldBeNull();

        var logs = await readCtx.Set<JobLog>().Where(l => l.JobId == jobId).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        logs.ShouldBeEmpty();
    }
}
