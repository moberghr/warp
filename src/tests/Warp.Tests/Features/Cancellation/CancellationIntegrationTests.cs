using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features.Cancellation;

[GenerateDatabaseTests]
public abstract class CancellationIntegrationTestsBase : IntegrationTestBase
{
    protected CancellationIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    // One run of the graceful-cancel path, asserted end to end: what DeleteJob records, what the
    // worker's cancel arm writes to the row, and who wrote each log. CancellableRequest runs for 30s,
    // so reaching Deleted inside this test's 10s budget is also the proof that the handler was
    // cancelled rather than left to finish.
    [TimedFact]
    public async Task GivenProcessingJob_WhenDeleted_ThenHandlerIsCancelledAndRowIsFinalizedAsDeleted()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new CancellableRequest());
        await publisher.SaveChangesAsync(ct);

        await server.WaitForJobState(jobId, State.Processing);

        var cmd = server.CreateCommandService();
        await cmd.DeleteJob(jobId);

        // The Cancelled log, not the state: DeleteJob itself does not finalize the row.
        await server.WaitForJobLog(jobId, "Cancelled", timeout: TimeSpan.FromSeconds(5));

        var job = await server.GetJob(jobId);
        job.CurrentState.ShouldBe(State.Deleted);
        job.CancellationMode.ShouldBe(CancellationMode.None);
        job.CurrentWorkerId.ShouldBeNull();
        job.LastKeepAlive.ShouldBeNull();
        job.ExpireAt.ShouldNotBeNull();

        var logs = await server.GetJobLogs(jobId);

        // The request to cancel comes from the command service, so it has no worker.
        var requested = logs.Single(x => string.Equals(x.EventType, "CancellationRequested", StringComparison.Ordinal));
        requested.WorkerId.ShouldBeNull();

        // The worker's own logs carry its id, and they agree on which worker it was.
        var processing = logs.Single(x => string.Equals(x.EventType, "Processing", StringComparison.Ordinal));
        processing.WorkerId.ShouldNotBeNull();
        var cancelled = logs.Single(x => string.Equals(x.EventType, "Cancelled", StringComparison.Ordinal));
        cancelled.WorkerId.ShouldBe(processing.WorkerId);

        logs.ShouldNotContain(x => string.Equals(x.EventType, "Completed", StringComparison.Ordinal));
        logs.ShouldNotContain(x => string.Equals(x.EventType, "Failed", StringComparison.Ordinal));
    }

    [TimedFact]
    public async Task GivenProcessingJobWithHandlerPolicy_WhenCancelled_ThenStampedPolicySurvivesOnTheRow()
    {
        // The policy resolved for this attempt (§8.8) is stamped into the in-memory JobContext and only
        // reaches the row through the finalizing write. Success and failure persist it; a graceful cancel
        // must too, or a cancelled-then-requeued job re-resolves — the stamp-once invariant.
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new CancellableStampedRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Processing);

        var cmd = server.CreateCommandService();
        await cmd.DeleteJob(jobId);

        await server.WaitForJobLog(jobId, "Cancelled", timeout: TimeSpan.FromSeconds(5));

        var job = await server.GetJob(jobId);
        job.CurrentState.ShouldBe(State.Deleted);
        job.Metadata.ShouldNotBeNull();
        var meta = MetadataSerializer.Deserialize(job.Metadata);
        Convert.ToInt32(meta["MaxRetries"]).ShouldBe(2);

        // ...but a cancelled attempt is not a spent retry.
        meta.ShouldNotContainKey("RetriedTimes");
    }

    [TimedFact]
    public async Task GivenDispatcherMode_WhenStampedJobIsCancelled_ThenStampedPolicySurvivesOnTheRow()
    {
        // Both worker paths must move in lockstep (§8.33) — the dispatcher keeps its own cancel arm.
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            cfg =>
            {
                cfg.UseDispatcher = true;
                cfg.WorkerCount = 2;
                cfg.CompletionBatchSize = 10;
                cfg.CompletionFlushInterval = TimeSpan.FromMilliseconds(50);
            });
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new CancellableStampedRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Processing);

        var cmd = server.CreateCommandService();
        await cmd.DeleteJob(jobId);

        await server.WaitForJobLog(jobId, "Cancelled", timeout: TimeSpan.FromSeconds(10));

        var job = await server.GetJob(jobId);
        job.CurrentState.ShouldBe(State.Deleted);
        job.Metadata.ShouldNotBeNull();
        var meta = MetadataSerializer.Deserialize(job.Metadata);
        Convert.ToInt32(meta["MaxRetries"]).ShouldBe(2);

        // ...but a cancelled attempt is not a spent retry.
        meta.ShouldNotContainKey("RetriedTimes");
    }
}
