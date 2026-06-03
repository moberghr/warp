using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.Retry;
using Warp.Core.Timeout;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features.Timeout;

[GenerateDatabaseTests]
public abstract class TimeoutIntegrationTestsBase : IntegrationTestBase
{
    protected TimeoutIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    private static JobParameters DeleteTimeout(double seconds) =>
        new JobParameters()
            .WithTimeout(TimeSpan.FromSeconds(seconds))
            .Configure<IRetryMetadata>(m => m.MaxRetries = 0);

    private static Task<WarpTestServer> StartWithFakeTime(IDatabaseFixture fixture, FakeTimeProvider time) =>
        WarpTestServer.StartAsync(
            fixture,
            configure: cfg =>
            {
                // Driving FakeTimeProvider forward affects every TimeProvider-aware comparison
                // in the worker, including LastKeepAlive freshness. Disable stale-job recovery
                // so a fake-time jump past InvisibilityTimeout doesn't trigger concurrent
                // re-enqueue of an already-processing job (manifests as extra Processing logs).
                cfg.StaleJobRecoveryInterval = null;
                cfg.InvisibilityTimeout = TimeSpan.FromDays(365);
            },
            configureServices: services => services.AddSingleton<TimeProvider>(time));

    /// <summary>
    /// Drive fake time forward in small chunks while polling for a terminal state. Every chunk
    /// unblocks the worker's <c>Task.Delay(_, _timeProvider, _)</c> poll-loop sleep AND fires
    /// any pending timeout <c>CancellationTokenSource</c> created with the fake provider, so
    /// retries advance in lockstep with fake-time advances. The only real wall-clock cost is
    /// the per-iteration yield that gives the worker continuation a chance to run.
    /// </summary>
    private static async Task<Job> WaitForJobStateWithFakeTime(
        WarpTestServer server,
        FakeTimeProvider time,
        Guid jobId,
        State expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            var job = await server.GetJob(jobId);
            if (job.CurrentState == expected)
            {
                return job;
            }

            await Task.Yield();
        }

        throw new TimeoutException($"Job {jobId} did not reach {expected} within 5s wall-clock.");
    }

    [TimedFact]
    public async Task DeleteMode_JobExceedsTimeout_EndsDeletedWithTimeoutLog()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await StartWithFakeTime(Fixture, time);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new CancellableRequest(), DeleteTimeout(60));
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await WaitForJobStateWithFakeTime(server, time, jobId, State.Deleted);

        var ctx = Fixture.CreateContext();
        var job = await ctx.Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Deleted);

        var logs = await ctx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        logs.ShouldContain(x => x.Message != null && x.Message.Contains("Timed out after 60s", StringComparison.Ordinal));
    }

    [TimedFact]
    public async Task DeleteMode_PlusAddRetry_TimedOutNotRetried()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await StartWithFakeTime(Fixture, time);
        var publisher = server.CreatePublisher();
        var parameters = new JobParameters()
            .WithTimeout(TimeSpan.FromSeconds(60))
            .Configure<IRetryMetadata>(m => m.MaxRetries = 3);

        var jobId = await publisher.Enqueue(new CancellableRequest(), parameters);
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await WaitForJobStateWithFakeTime(server, time, jobId, State.Deleted);

        var ctx = Fixture.CreateContext();
        var processingLogs = await ctx.Set<JobLog>()
            .CountAsync(x => x.JobId == jobId && x.EventType == "Processing", Xunit.TestContext.Current.CancellationToken);
        processingLogs.ShouldBe(1);

        // No retry → no per-retry Scheduled/Enqueued log was emitted by the worker.
        var requeuedLogs = await ctx.Set<JobLog>()
            .CountAsync(
                x => x.JobId == jobId && (x.EventType == "Enqueued" || x.EventType == "Scheduled"),
                Xunit.TestContext.Current.CancellationToken);
        requeuedLogs.ShouldBe(0);
    }

    [TimedFact]
    public async Task FailMode_NoRetry_EndsFailedWithTimeoutException()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await StartWithFakeTime(Fixture, time);
        var publisher = server.CreatePublisher();
        var parameters = new JobParameters()
            .WithTimeout(TimeSpan.FromSeconds(60), TimeoutMode.Fail)
            .Configure<IRetryMetadata>(m => m.MaxRetries = 0);

        var jobId = await publisher.Enqueue(new CancellableRequest(), parameters);
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await WaitForJobStateWithFakeTime(server, time, jobId, State.Failed);

        var ctx = Fixture.CreateContext();
        var failedLog = await ctx.Set<JobLog>()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Failed")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        failedLog.ShouldNotBeNull();
        failedLog.Message.ShouldNotBeNull();
        failedLog.Message.ShouldContain("timed out after 60s", Case.Insensitive);
    }

    [TimedFact]
    public async Task FailMode_PlusAddRetry_PerAttempt_IsRetriedThenFails()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await StartWithFakeTime(Fixture, time);
        var publisher = server.CreatePublisher();
        var parameters = new JobParameters()
            .WithTimeout(TimeSpan.FromSeconds(60), TimeoutMode.Fail)
            .Configure<IRetryMetadata>(m =>
            {
                m.MaxRetries = 2;
                m.RetryDelays = [0];
            });

        var jobId = await publisher.Enqueue(new CancellableRequest(), parameters);
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await WaitForJobStateWithFakeTime(server, time, jobId, State.Failed);

        var ctx = Fixture.CreateContext();
        var processingLogs = await ctx.Set<JobLog>()
            .CountAsync(x => x.JobId == jobId && x.EventType == "Processing", Xunit.TestContext.Current.CancellationToken);
        processingLogs.ShouldBe(3);

        // 2 retries → 2 per-retry Scheduled/Enqueued logs (RetryDelays = [0] → immediate, Enqueued).
        var requeuedLogs = await ctx.Set<JobLog>()
            .CountAsync(
                x => x.JobId == jobId && (x.EventType == "Enqueued" || x.EventType == "Scheduled"),
                Xunit.TestContext.Current.CancellationToken);
        requeuedLogs.ShouldBe(2);
    }

    [TimedFact]
    public async Task FailMode_PlusAddRetry_TotalScope_BoundsTotalWallClock()
    {
        // Total-scope discriminator: the deadline anchored at CreateTime caps the whole chain.
        // With Total broken (degenerating to PerAttempt), each retry would re-arm a fresh
        // 60s budget, and even with FakeTimeProvider the test would observe 3 attempts each
        // requiring its own time advance — the per-attempt `Processing` log timestamps would
        // span the advanced fake time. Total-scope correctness means: the SAME deadline is
        // reused on every attempt, and once fake time has passed it, every subsequent attempt
        // fires immediately.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await StartWithFakeTime(Fixture, time);
        var publisher = server.CreatePublisher();
        var parameters = new JobParameters()
            .WithTimeout(TimeSpan.FromSeconds(60), TimeoutMode.Fail, TimeoutScope.Total)
            .Configure<IRetryMetadata>(m =>
            {
                m.MaxRetries = 2;
                m.RetryDelays = [0];
            });

        var jobId = await publisher.Enqueue(new CancellableRequest(), parameters);
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await WaitForJobStateWithFakeTime(server, time, jobId, State.Failed);

        var ctx = Fixture.CreateContext();
        var job = await ctx.Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);

        var processingLogs = await ctx.Set<JobLog>()
            .CountAsync(x => x.JobId == jobId && x.EventType == "Processing", Xunit.TestContext.Current.CancellationToken);
        processingLogs.ShouldBe(3);

        var failedLog = await ctx.Set<JobLog>()
            .Where(x => x.JobId == jobId && x.EventType == "Failed")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);
        failedLog.ShouldNotBeNull();
        failedLog.Message.ShouldContain("deadline exceeded", Case.Insensitive);
    }
}
