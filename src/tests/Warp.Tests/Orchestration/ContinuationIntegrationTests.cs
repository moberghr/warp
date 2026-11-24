using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Helper;
using Warp.Core.Retry;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Orchestration;

[GenerateDatabaseTests]
public abstract class ContinuationIntegrationTestsBase : IntegrationTestBase
{
    protected ContinuationIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenParentJob_WhenCompletes_ThenChildActivatesAndCompletes()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var parentId = await publisher.Enqueue(new UnitRequest());
        var childId = await publisher.Enqueue(new UnitRequest(), parentId);
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();

        var parent = await ctx.Set<Job>().FirstAsync(j => j.Id == parentId, Xunit.TestContext.Current.CancellationToken);
        parent.CurrentState.ShouldBe(State.Completed);

        var child = await ctx.Set<Job>().FirstAsync(j => j.Id == childId, Xunit.TestContext.Current.CancellationToken);
        child.CurrentState.ShouldBe(State.Completed);
        child.ParentJobId.ShouldBe(parentId);
    }

    [TimedFact]
    public async Task GivenParentJobThatFails_WhenDefaultOnlyOnSucceeded_ThenChildStaysAwaiting()
    {
        // Disable the auto-orchestrator so we control exactly when orchestration runs. With the
        // 100ms auto-tick the test would have to Task.Delay long enough to "be sure" the
        // continuation didn't activate; instead we run the orchestrator a deterministic number
        // of times via RunOrchestratorOnceAsync and assert state after each tick.
        await using var server = await WarpTestServer.StartAsync(Fixture, cfg => cfg.OrchestrationInterval = null);
        var publisher = server.CreatePublisher();

        // WarpTestServer registers AddRetry(MaxRetries=3, Delays=[1]). Without disabling retry
        // for this parent, ThrowExceptionRequest goes through 3 retry rounds (~4-6s of
        // back-and-forth Scheduled/Processing) before reaching Failed — racing the test's
        // wait. The test's contract is "parent fails → child stays Awaiting"; bypassing retry
        // on the parent is the right semantic match.
        var parentId = await publisher.Enqueue(new ThrowExceptionRequest(), new JobParameters().WithRetry(0));
        var childId = await publisher.Enqueue(new UnitRequest(), parentId);
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Wait for parent to fail
        await server.WaitForJobState(parentId, State.Failed);

        // Drive orchestration deterministically. A buggy implementation that incorrectly
        // activates the child despite OnlyOnSucceeded would do so on the first orchestration
        // pass. Run a few times to also catch a "fires only after N ticks" regression.
        await server.RunOrchestratorOnceAsync(Xunit.TestContext.Current.CancellationToken);
        await server.RunOrchestratorOnceAsync(Xunit.TestContext.Current.CancellationToken);
        await server.RunOrchestratorOnceAsync(Xunit.TestContext.Current.CancellationToken);

        var ctx = Fixture.CreateContext();

        var parent = await ctx.Set<Job>().FirstAsync(j => j.Id == parentId, Xunit.TestContext.Current.CancellationToken);
        parent.CurrentState.ShouldBe(State.Failed);

        // Child stays Awaiting — condition not met, but parent could be requeued
        var child = await ctx.Set<Job>().FirstAsync(j => j.Id == childId, Xunit.TestContext.Current.CancellationToken);
        child.CurrentState.ShouldBe(State.Awaiting);
    }

    [TimedFact]
    public async Task GivenParentJobThatFails_WithOnAnyFinishedState_ThenChildStillActivates()
    {
        // Use batch publisher to create a batch with OnAnyFinishedState continuation,
        // since individual job continuations don't have ContinuationOptions on the child.
        // Batch is the mechanism for continuation options.
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var batchPublisher = server.CreateBatchPublisher();

        // Disable retry on the failing batch job — without this, AddRetry's MaxRetries=3 +
        // 1s delay forces ~4-6s of retries before the batch member reaches Failed, racing
        // the 10s budget. The test's contract is "OnAnyFinishedState activates continuation
        // when batch fails", which doesn't depend on retry behavior.
        var noRetryMetadata = new JobParameters().WithRetry(0).Metadata;
        var batchJobs = new List<ThrowExceptionRequest> { new() };
        var batchId = await batchPublisher.StartNew(batchJobs, options: ContinuationOptions.OnAnyFinishedState, metadata: noRetryMetadata);

        var continuationJobs = new List<UnitRequest> { new() };
        var continuationBatchId = await batchPublisher.ContinueBatchWith(continuationJobs, batchId);

        await batchPublisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();

        // Batch with OnAnyFinishedState completes even when children fail
        var batch = await ctx.Set<Job>().FirstAsync(j => j.Id == batchId, Xunit.TestContext.Current.CancellationToken);
        batch.CurrentState.ShouldBe(State.Completed);

        // Continuation batch activated and completed because OnAnyFinishedState
        var continuation = await ctx.Set<Job>().FirstAsync(j => j.Id == continuationBatchId, Xunit.TestContext.Current.CancellationToken);
        continuation.CurrentState.ShouldBe(State.Completed);

        var continuationChildren = await ctx.Set<Job>()
            .Where(j => j.ParentJobId == continuationBatchId && j.Kind == JobKind.Job)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        continuationChildren.ShouldAllBe(j => j.CurrentState == State.Completed);
    }

    [TimedFact]
    public async Task GivenThreeLevelContinuationChain_WhenProcessed_ThenAllComplete()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var publisher = server.CreatePublisher();
        var grandparentId = await publisher.Enqueue(new UnitRequest());
        var parentId = await publisher.Enqueue(new UnitRequest(), grandparentId);
        var childId = await publisher.Enqueue(new UnitRequest(), parentId);
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForCompletion();

        var ctx = Fixture.CreateContext();

        var grandparent = await ctx.Set<Job>().FirstAsync(j => j.Id == grandparentId, Xunit.TestContext.Current.CancellationToken);
        grandparent.CurrentState.ShouldBe(State.Completed);

        var parent = await ctx.Set<Job>().FirstAsync(j => j.Id == parentId, Xunit.TestContext.Current.CancellationToken);
        parent.CurrentState.ShouldBe(State.Completed);

        var child = await ctx.Set<Job>().FirstAsync(j => j.Id == childId, Xunit.TestContext.Current.CancellationToken);
        child.CurrentState.ShouldBe(State.Completed);
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenTheProductionSignalInterval_WhenAContinuationChainRuns_ThenEachHopIsWokenWithoutTheSweep(bool useDispatcher)
    {
        // Arrange — the test server normally runs a woken task at once; here the orchestrator holds for the
        // default interval after each pass (rule 6.18), and its own sweep is minutes away, so only a wake
        // kept through the hold can activate each link of the chain within the budget.
        await using var server = await WarpTestServer.StartAsync(Fixture, config =>
        {
            config.UseDispatcher = useDispatcher;
            config.ServerTaskSignalInterval = new WarpServerConfiguration().ServerTaskSignalInterval;
            config.OrchestrationInterval = TimeSpan.FromMinutes(5);
        });
        var publisher = server.CreatePublisher();
        var grandparentId = await publisher.Enqueue(new UnitRequest());
        var parentId = await publisher.Enqueue(new UnitRequest(), grandparentId);
        var childId = await publisher.Enqueue(new UnitRequest(), parentId);
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await server.WaitForCompletion(TimeSpan.FromSeconds(8));

        // Assert
        await using var ctx = Fixture.CreateContext();
        var states = await ctx.Set<Job>()
            .Where(x => x.Id == grandparentId || x.Id == parentId || x.Id == childId)
            .Select(x => x.CurrentState)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        states.ShouldBe([State.Completed, State.Completed, State.Completed]);
    }
}
