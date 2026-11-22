using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Helper;
using Warp.Core.Retry;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Orchestration;

[GenerateDatabaseTests]
public abstract class BatchIntegrationTestsBase : IntegrationTestBase
{
    protected BatchIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenBatchOfFive_WhenAllComplete_ThenBatchFinalizes()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var batchPublisher = server.CreateBatchPublisher();
        var jobs = Enumerable.Range(0, 5).Select(_ => new UnitRequest()).ToList();
        var batchId = await batchPublisher.StartNew(jobs);
        await batchPublisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();

        // Batch job should be completed
        var batch = await ctx.Set<Job>().FirstAsync(j => j.Id == batchId, Xunit.TestContext.Current.CancellationToken);
        batch.CurrentState.ShouldBe(State.Completed);
        batch.Kind.ShouldBe(JobKind.Batch);
        batch.JobCount.ShouldBe(5);

        // All child jobs should be completed
        var childJobs = await ctx.Set<Job>()
            .Where(j => j.ParentJobId == batchId && j.Kind == JobKind.Job)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        childJobs.Count.ShouldBe(5);
        childJobs.ShouldAllBe(j => j.CurrentState == State.Completed);
    }

    [TimedFact]
    public async Task GivenBatchOnBulkQueue_WhenWorkerListensOnBulk_ThenBatchAndDefaultQueueContinuationComplete()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture, opt => opt.Queues = ["default", "bulk"]);
        var batchPublisher = server.CreateBatchPublisher();

        var batchId = await batchPublisher.StartNew(Enumerable.Range(0, 3).Select(_ => new UnitRequest()).ToList(), queue: "bulk");
        var continuationId = await batchPublisher.ContinueBatchWith(Enumerable.Range(0, 2).Select(_ => new UnitRequest()).ToList(), batchId);
        await batchPublisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();
        var batch = await ctx.Set<Job>().FirstAsync(j => j.Id == batchId, Xunit.TestContext.Current.CancellationToken);
        var continuation = await ctx.Set<Job>().FirstAsync(j => j.Id == continuationId, Xunit.TestContext.Current.CancellationToken);
        var bulkChildren = await ctx.Set<Job>()
            .Where(j => j.ParentJobId == batchId && j.Kind == JobKind.Job)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        batch.CurrentState.ShouldBe(State.Completed);
        bulkChildren.ShouldAllBe(j => j.Queue == "bulk" && j.CurrentState == State.Completed);
        continuation.CurrentState.ShouldBe(State.Completed);
        continuation.Queue.ShouldBe("default");
    }

    [TimedFact]
    public async Task GivenBatchWithContinuation_WhenFirstBatchCompletes_ThenContinuationActivatesAndCompletes()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var batchPublisher = server.CreateBatchPublisher();

        var batchJobs = Enumerable.Range(0, 3).Select(_ => new UnitRequest()).ToList();
        var batchId = await batchPublisher.StartNew(batchJobs);

        var continuationJobs = Enumerable.Range(0, 2).Select(_ => new UnitRequest()).ToList();
        var continuationBatchId = await batchPublisher.ContinueBatchWith(continuationJobs, batchId);

        await batchPublisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();

        // First batch completed
        var batch = await ctx.Set<Job>().FirstAsync(j => j.Id == batchId, Xunit.TestContext.Current.CancellationToken);
        batch.CurrentState.ShouldBe(State.Completed);

        // Continuation batch completed
        var continuation = await ctx.Set<Job>().FirstAsync(j => j.Id == continuationBatchId, Xunit.TestContext.Current.CancellationToken);
        continuation.CurrentState.ShouldBe(State.Completed);
        continuation.ParentJobId.ShouldBe(batchId);

        // All continuation child jobs completed
        var continuationChildren = await ctx.Set<Job>()
            .Where(j => j.ParentJobId == continuationBatchId && j.Kind == JobKind.Job)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        continuationChildren.Count.ShouldBe(2);
        continuationChildren.ShouldAllBe(j => j.CurrentState == State.Completed);
    }

    [TimedFact]
    public async Task GivenThreeChainedBatches_WhenProcessed_ThenAllComplete()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var batchPublisher = server.CreateBatchPublisher();

        // Batch 1
        var batch1Jobs = Enumerable.Range(0, 2).Select(_ => new UnitRequest()).ToList();
        var batch1Id = await batchPublisher.StartNew(batch1Jobs);

        // Batch 2 (continuation of batch 1)
        var batch2Jobs = Enumerable.Range(0, 2).Select(_ => new UnitRequest()).ToList();
        var batch2Id = await batchPublisher.ContinueBatchWith(batch2Jobs, batch1Id);

        // Batch 3 (continuation of batch 2)
        var batch3Jobs = Enumerable.Range(0, 2).Select(_ => new UnitRequest()).ToList();
        var batch3Id = await batchPublisher.ContinueBatchWith(batch3Jobs, batch2Id);

        await batchPublisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();

        var batch1 = await ctx.Set<Job>().FirstAsync(j => j.Id == batch1Id, Xunit.TestContext.Current.CancellationToken);
        var batch2 = await ctx.Set<Job>().FirstAsync(j => j.Id == batch2Id, Xunit.TestContext.Current.CancellationToken);
        var batch3 = await ctx.Set<Job>().FirstAsync(j => j.Id == batch3Id, Xunit.TestContext.Current.CancellationToken);

        batch1.CurrentState.ShouldBe(State.Completed);
        batch2.CurrentState.ShouldBe(State.Completed);
        batch3.CurrentState.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task GivenBatchWithOnAnyFinishedState_WhenSomeJobsFail_ThenContinuationStillFires()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var batchPublisher = server.CreateBatchPublisher();

        // Batch with mix of succeeding and failing jobs, using OnAnyFinishedState
        var batchJobs = new List<ThrowExceptionRequest>
        {
            new(),
            new(),
        };
        var batchId = await batchPublisher.StartNew(batchJobs, options: ContinuationOptions.OnAnyFinishedState);

        // Continuation should fire even though batch jobs fail
        var continuationJobs = new List<UnitRequest> { new() };
        var continuationBatchId = await batchPublisher.ContinueBatchWith(continuationJobs, batchId);

        await batchPublisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();

        // Batch with OnAnyFinishedState completes even when children fail
        var batch = await ctx.Set<Job>().FirstAsync(j => j.Id == batchId, Xunit.TestContext.Current.CancellationToken);
        batch.CurrentState.ShouldBe(State.Completed);

        // Continuation should have activated and completed because OnAnyFinishedState
        var continuation = await ctx.Set<Job>().FirstAsync(j => j.Id == continuationBatchId, Xunit.TestContext.Current.CancellationToken);
        continuation.CurrentState.ShouldBe(State.Completed);

        var continuationChildren = await ctx.Set<Job>()
            .Where(j => j.ParentJobId == continuationBatchId && j.Kind == JobKind.Job)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        continuationChildren.ShouldAllBe(j => j.CurrentState == State.Completed);
    }

    [TimedFact]
    public async Task GivenBatchWithOnlyOnSucceeded_WhenJobFails_ThenContinuationStaysAwaiting()
    {
        // Disable the auto-orchestrator so the "did/didn't activate continuation" check is
        // driven by explicit ticks rather than a wall-clock Task.Delay.
        await using var server = await WarpTestServer.StartAsync(Fixture, cfg => cfg.OrchestrationInterval = null);
        var batchPublisher = server.CreateBatchPublisher();

        // Bypass retry on the failing batch members — the test contract is "OnlyOnSucceeded
        // doesn't activate continuation when batch fails", which doesn't depend on retry.
        // Without WithRetry(0), AddRetry(MaxRetries=3, Delays=[1]) drags the failure out
        // ~4-6s and races the WaitForJobState wait.
        var noRetry = new JobParameters().WithRetry(0).Metadata;
        var batchJobs = new List<ThrowExceptionRequest>
        {
            new(),
            new(),
        };
        var batchId = await batchPublisher.StartNew(batchJobs, options: ContinuationOptions.OnlyOnSucceeded, metadata: noRetry);

        var continuationJobs = new List<UnitRequest> { new() };
        var continuationBatchId = await batchPublisher.ContinueBatchWith(continuationJobs, batchId);

        await batchPublisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Wait for the batch *members* (Kind=Job) to reach Failed via worker processing —
        // this is what workers do unconditionally. The batch itself (Kind=Batch) transitions
        // to Failed only when the Orchestrator notices all members are terminal, so we drive
        // that explicitly below.
        var memberIds = await Fixture.CreateContext().Set<Job>()
            .Where(j => j.ParentJobId == batchId && j.Kind == JobKind.Job)
            .Select(j => j.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        memberIds.Count.ShouldBe(2);
        foreach (var memberId in memberIds)
        {
            await server.WaitForJobState(memberId, State.Failed);
        }

        // First orchestrator tick finalizes the batch (members all terminal → batch Failed).
        await server.RunOrchestratorOnceAsync(Xunit.TestContext.Current.CancellationToken);

        // Subsequent ticks would activate the continuation if the OnlyOnSucceeded gate were
        // broken. Run a few more to also catch a "fires only after N ticks" regression.
        await server.RunOrchestratorOnceAsync(Xunit.TestContext.Current.CancellationToken);
        await server.RunOrchestratorOnceAsync(Xunit.TestContext.Current.CancellationToken);

        var ctx = Fixture.CreateContext();

        // Batch should be failed
        var batch = await ctx.Set<Job>().FirstAsync(j => j.Id == batchId, Xunit.TestContext.Current.CancellationToken);
        batch.CurrentState.ShouldBe(State.Failed);

        // Continuation stays Awaiting — condition not met, but batch could be requeued
        var continuation = await ctx.Set<Job>().FirstAsync(j => j.Id == continuationBatchId, Xunit.TestContext.Current.CancellationToken);
        continuation.CurrentState.ShouldBe(State.Awaiting);

        // Continuation children should also stay Awaiting
        var continuationChildren = await ctx.Set<Job>()
            .Where(j => j.ParentJobId == continuationBatchId && j.Kind == JobKind.Job)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        continuationChildren.ShouldAllBe(j => j.CurrentState == State.Awaiting);
    }

    [TimedFact]
    public async Task GivenBatchWithRetryJobs_WhenRetriesExhausted_ThenBatchReflectsOutcome()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var batchPublisher = server.CreateBatchPublisher();

        // Batch with OnlyOnSucceeded (default) and all-failing children should fail
        var failingJobs = new List<ThrowExceptionRequest> { new(), new() };
        var batchId = await batchPublisher.StartNew(failingJobs);
        await batchPublisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(batchId, State.Failed, timeout: TimeSpan.FromSeconds(8));

        var ctx = Fixture.CreateContext();

        var batch = await ctx.Set<Job>().FirstAsync(j => j.Id == batchId, Xunit.TestContext.Current.CancellationToken);
        batch.CurrentState.ShouldBe(State.Failed);

        var childJobs = await ctx.Set<Job>()
            .Where(j => j.ParentJobId == batchId && j.Kind == JobKind.Job)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        childJobs.ShouldAllBe(j => j.CurrentState == State.Failed);
    }
}
