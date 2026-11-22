using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Observability;

[GenerateDatabaseTests]
public abstract class RealTimeLogIntegrationTestsBase : IntegrationTestBase
{
    protected RealTimeLogIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    // Polls the JobLog table until at least <paramref name="expectedCount"/> "Log"-event rows
    // exist for the given job, or throws TimeoutException. Replaces fixed Task.Delay(300) waits:
    // under DB contention the monitor flush (LogFlushInterval = 100ms) can take longer than 300ms,
    // and a fixed sleep either flakes (too short) or wastes time (too long). Polling lets the
    // happy path return in tens of ms and surfaces a clear failure if logs never appear.
    private async Task<List<JobLog>> WaitForHandlerLogs(Guid jobId, int expectedCount, TimeSpan? timeout = null)
    {
        List<JobLog> logs = [];
        await WarpTestServer.WaitUntil(
            $"Job {jobId} accumulating {expectedCount} handler-log rows",
            async () =>
            {
                logs = await Fixture.CreateContext().Set<JobLog>()
                    .Where(x => x.JobId == jobId && x.EventType == "Log")
                    .OrderBy(x => x.Timestamp)
                    .ToListAsync(Xunit.TestContext.Current.CancellationToken);

                return logs.Count >= expectedCount;
            },
            timeout ?? TimeSpan.FromSeconds(5),
            Xunit.TestContext.Current.CancellationToken);

        return logs;
    }

    [TimedFact]
    public async Task GivenProcessingJob_WhenHandlerLogs_ThenLogsAppearInDbBeforeCompletion()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new ProgressLogRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Processing);

        // Verify logs are in DB while job is still processing
        var handlerLogs = await WaitForHandlerLogs(jobId, expectedCount: 2);
        handlerLogs.ShouldContain(l => l.Message.Contains("Step 1 started"));
        handlerLogs.ShouldContain(l => l.Message.Contains("Step 1 warning"));

        // Job should still be processing (logs appeared DURING execution, not after)
        var job = await server.GetJob(jobId);
        job.CurrentState.ShouldBe(State.Processing);

        // Cancel to release the handler
        var cmd = server.CreateCommandService();
        await cmd.DeleteJob(jobId);
        await server.WaitForJobLog(jobId, "Cancelled", timeout: TimeSpan.FromSeconds(5));
    }

    [TimedFact]
    public async Task GivenProcessingJob_WhenHandlerLogs_ThenLogLevelsArePreserved()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new ProgressLogRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Processing);

        var handlerLogs = await WaitForHandlerLogs(jobId, expectedCount: 2);
        handlerLogs.ShouldContain(l => l.Level == "Information" && l.Message.Contains("Step 1 started"));
        handlerLogs.ShouldContain(l => l.Level == "Warning" && l.Message.Contains("Step 1 warning"));

        var cmd = server.CreateCommandService();
        await cmd.DeleteJob(jobId);
        await server.WaitForJobLog(jobId, "Cancelled", timeout: TimeSpan.FromSeconds(5));
    }

    [TimedFact]
    public async Task GivenProcessingJob_WhenCancelledBeforeFlush_ThenHandlerLogsArePreserved()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new ProgressLogRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Processing);

        // Cancel immediately — don't wait for any flush.
        // The handler has already logged "Step 1 started" and "Step 1 warning"
        // but the monitor hasn't flushed them yet.
        // On the next monitor tick, Drain() picks up the logs AND detects cancellation.
        // The bug: monitor returns without saving the drained logs.
        var cmd = server.CreateCommandService();
        await cmd.DeleteJob(jobId);

        await server.WaitForJobLog(jobId, "Cancelled", timeout: TimeSpan.FromSeconds(5));

        // Verify handler logs survived the cancellation
        var ctx = Fixture.CreateContext();
        var handlerLogs = await ctx.Set<JobLog>()
            .Where(x => x.JobId == jobId && x.EventType == "Log")
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        handlerLogs.Count.ShouldBeGreaterThanOrEqualTo(2);
        handlerLogs.ShouldContain(l => l.Message.Contains("Step 1 started"));
        handlerLogs.ShouldContain(l => l.Message.Contains("Step 1 warning"));
    }
}
