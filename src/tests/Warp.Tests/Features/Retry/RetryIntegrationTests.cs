using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.Retry;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features.Retry;

[GenerateDatabaseTests]
public abstract class RetryIntegrationTestsBase : IntegrationTestBase
{
    protected RetryIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    private static int GetRetriedTimes(Job job)
    {
        if (job.Metadata == null)
        {
            return 0;
        }

        var meta = MetadataSerializer.Deserialize(job.Metadata);
        if (meta.TryGetValue("RetriedTimes", out var value))
        {
            return Convert.ToInt32(value);
        }

        return 0;
    }

    [TimedFact]
    public async Task GivenFailingJobWithThreeRetries_WhenProcessed_ThenRetriesThreeTimesThenFails()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new ThrowExceptionRequest(), new JobParameters().Configure<IRetryMetadata>(m => m.MaxRetries = 3));
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Failed, timeout: TimeSpan.FromSeconds(8));

        var ctx = Fixture.CreateContext();

        var job = await ctx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);
        GetRetriedTimes(job).ShouldBe(3);

        // Should have 1 initial attempt + 3 retries = 4 "Processing" log entries
        var processingLogs = await ctx.Set<JobLog>()
            .CountAsync(l => l.JobId == jobId && l.EventType == "Processing", Xunit.TestContext.Current.CancellationToken);
        processingLogs.ShouldBe(4);

        // Each retry now emits a Failed + Scheduled pair instead of a single Requeued,
        // so 3 retries × (1 Failed + 1 Scheduled) + 1 terminal Failed = 4 Failed total and
        // 3 Scheduled total. The Failed counts include the per-retry exception rows AND the
        // final terminal-failure row.
        var failedLogs = await ctx.Set<JobLog>()
            .CountAsync(l => l.JobId == jobId && l.EventType == "Failed", Xunit.TestContext.Current.CancellationToken);
        failedLogs.ShouldBe(4);

        var scheduledLogs = await ctx.Set<JobLog>()
            .CountAsync(l => l.JobId == jobId && l.EventType == "Scheduled", Xunit.TestContext.Current.CancellationToken);
        scheduledLogs.ShouldBe(3);
    }

    [TimedFact]
    public async Task GivenFailingJobWithZeroRetries_WhenProcessed_ThenFailsImmediately()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new ThrowExceptionRequest(), new JobParameters
        {
            Metadata = new Dictionary<string, object> { ["MaxRetries"] = 0 },
        });
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Failed, timeout: TimeSpan.FromSeconds(8));

        var ctx = Fixture.CreateContext();

        var job = await ctx.Set<Job>().FirstAsync(j => j.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.CurrentState.ShouldBe(State.Failed);
        GetRetriedTimes(job).ShouldBe(0);

        // Should have exactly 1 "Processing" log entry (the single attempt)
        var processingLogs = await ctx.Set<JobLog>()
            .CountAsync(l => l.JobId == jobId && l.EventType == "Processing", Xunit.TestContext.Current.CancellationToken);
        processingLogs.ShouldBe(1);

        // No retries → no Scheduled-for-retry log, just one terminal Failed.
        var scheduledLogs = await ctx.Set<JobLog>()
            .CountAsync(l => l.JobId == jobId && l.EventType == "Scheduled", Xunit.TestContext.Current.CancellationToken);
        scheduledLogs.ShouldBe(0);

        var failedLogs = await ctx.Set<JobLog>()
            .CountAsync(l => l.JobId == jobId && l.EventType == "Failed", Xunit.TestContext.Current.CancellationToken);
        failedLogs.ShouldBe(1);
    }

    [TimedFact]
    public async Task GivenFailingJobWithRetries_WhenProcessed_ThenScheduleTimeUpdatedOnRetry()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new ThrowExceptionRequest(), new JobParameters
        {
            Metadata = new Dictionary<string, object> { ["MaxRetries"] = 1 },
        }.Configure<IRetryMetadata>(m => m.MaxRetries = 1));
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Failed, timeout: TimeSpan.FromSeconds(8));

        var ctx = Fixture.CreateContext();

        // 1 retry → 1 Scheduled log (the retry was scheduled); the matching Failed lives
        // alongside it from the split-log emission.
        var scheduledLogs = await ctx.Set<JobLog>()
            .CountAsync(x => x.JobId == jobId && x.EventType == "Scheduled", Xunit.TestContext.Current.CancellationToken);
        scheduledLogs.ShouldBe(1);

        var job = await ctx.Set<Job>()
            .Where(x => x.Id == jobId)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);
        GetRetriedTimes(job).ShouldBe(1);
    }
}
