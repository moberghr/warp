using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Observability;

/// <summary>
/// A completed job must hand its staged counter increments to the shared buffer.
/// <para>
/// The worker stages increments on itself during a job and moves them across in
/// <c>CommitStagedCounters</c> after each commit. Every finalization arm — success, failure and
/// graceful cancel — has to make that call, and a new arm that forgets it loses every counter the job
/// emitted with nothing to notice it by: the job still completes, the rows are simply never written.
/// §8.33 records the dispatcher path being missed once for exactly this class of omission, and this
/// is the single-worker half of that lockstep.
/// </para>
/// <para>
/// <b>What this does NOT prove.</b> It does not show that a ROLLED-BACK attempt contributes nothing.
/// The pinned handler sits before finalization, so no outcome counter has been emitted yet and the
/// pre-release assertion holds whether emission stages or writes straight through — verified by
/// mutation: inverting the commit ordering, and bypassing staging entirely, both leave this test
/// green. Distinguishing them needs an observation between SaveChanges and COMMIT, which has no seam
/// and would need fault injection on the worker hot path (§0.2/§6.1). The rollback guarantee rests on
/// the call sites being ordered after the commit, which is enforced by reading the code, not here.
/// </para>
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class StagedCounterCommitTestsBase : IntegrationTestBase
{
    protected StagedCounterCommitTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact(30_000)]
    public async Task FinalizationCounters_OfACompletedJob_AreHandedToTheBuffer()
    {
        var barrier = new BarrierSignal();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            config => config.WorkerCount = 1,
            services => services.AddSingleton(barrier));

        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new BarrierRequest());
        await publisher.SaveChangesAsync();

        // Pinned inside the handler: the claim has committed, the finalization has not run at all.
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();

        // Baseline, not the assertion that carries this test: nothing has finalized yet, so draining
        // here only clears claim-stage increments so the post-commit drain cannot read them as its own.
        server.CounterBuffer.Drain();

        barrier.CanFinish.Release();
        await server.WaitForJobState(jobId, State.Completed);

        var afterCommit = server.CounterBuffer.Drain().Select(x => x.Key).ToList();
        afterCommit.ShouldContain(
            "stats:succeeded",
            "a committed job must hand its staged increments to the buffer");
    }
}
