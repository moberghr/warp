using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Features.Pause;

[GenerateDatabaseTests]
public abstract class PauseIntegrationTestsBase : IntegrationTestBase
{
    protected PauseIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    private async Task<Guid> GetFirstGroupId(WarpTestServer server)
    {
        var ctx = Fixture.CreateContext();
        var group = await ctx.Set<WorkerGroup>()
            .Where(g => g.ServerId == server.ServerId)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);
        return group.Id;
    }

    [TimedFact]
    public async Task PauseServer_PauseStateHolder_UpdatedByHeartbeat()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var groupId = await GetFirstGroupId(server);
        var svc = server.CreateServerCommandService();

        await svc.PauseServer(server.ServerId);
        await server.WaitForPauseState(groupId, expectedPaused: true);
        server.PauseState.IsPaused(groupId).ShouldBeTrue();

        // Cleanup
        await svc.ResumeServer(server.ServerId);
        await server.WaitForPauseState(groupId, expectedPaused: false);
    }

    [TimedFact]
    public async Task PauseServer_JobsStayEnqueued()
    {
        // Pause propagation runs through PauseStateHolder, which Heartbeat refreshes on its
        // periodic tick. Disable the auto Heartbeat and drive it manually so the test fully
        // owns the timing of the holder flip — no race between auto-heartbeat and the
        // pause/publish sequence below.
        using var loops = new WorkerLoopPauseTracker();
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.HealthCheckInterval = null,
            configureServices: services => services.AddSingleton<PauseStateHolder>(loops));
        var groupId = await GetFirstGroupId(server);
        var svc = server.CreateServerCommandService();

        // Pause via the API (DB row update) then run Heartbeat once so the in-memory holder
        // catches up. After this returns, every worker iteration that begins from this point
        // on will see paused=true and skip the fetch.
        await svc.PauseServer(server.ServerId);
        await server.RunHeartbeatOnceAsync(Xunit.TestContext.Current.CancellationToken);
        server.PauseState.IsPaused(groupId).ShouldBeTrue();

        // A worker iteration that passed its pause check before the heartbeat flipped the holder still
        // completes its claim (rule 6.8), and that claim can see the row published below. A fixed 500 ms
        // wait for such claims to finish was not enough on CI (SQL Server 2025: a claim stamped 600 ms before
        // the job existed came back with it). Wait until every worker loop has since answered "paused"
        // instead; none can claim after that.
        await loops.WaitUntilEveryLoopSawPauseAsync(Xunit.TestContext.Current.CancellationToken);

        // Publish a job — by now all worker iterations are either sleeping in the pause
        // short-circuit or about to enter it; none can claim this row.
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Confirm pause is honored: WaitForCompletion must time out, because the (paused)
        // worker can't claim the only outstanding job. Expressing the negative assertion as a
        // bounded WaitForCompletion replaces a polling-loop-with-Delay; the 2s budget is the
        // window over which we expect no worker iteration to misbehave.
        await Should.ThrowAsync<TimeoutException>(
            async () => await server.WaitForCompletion(TimeSpan.FromSeconds(2)));

        var jobBeforeResume = await server.GetJob(jobId);
        jobBeforeResume.CurrentState.ShouldBe(State.Enqueued);

        // Resume + manual heartbeat to flip the holder back, then let workers process.
        await svc.ResumeServer(server.ServerId);
        await server.RunHeartbeatOnceAsync(Xunit.TestContext.Current.CancellationToken);
        await server.WaitForCompletion();
    }

    [TimedFact]
    public async Task PauseServer_Resume_JobsGetProcessed()
    {
        // Heartbeat driven by hand: pause propagation is deterministic, and with the periodic heartbeat off the
        // server's clock is not corrected to the database's (DatabaseClock), so the claim stamp below and
        // resumedAt are read off the same clock. With the correction on, each beat re-measures the offset
        // with up to half a round trip of error, which moved a claim stamp tens of ms before resumedAt on CI.
        using var loops = new WorkerLoopPauseTracker();
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.HealthCheckInterval = null,
            configureServices: services => services.AddSingleton<PauseStateHolder>(loops));
        var svc = server.CreateServerCommandService();

        // Pause, publish, then resume
        await svc.PauseServer(server.ServerId);
        await server.RunHeartbeatOnceAsync(Xunit.TestContext.Current.CancellationToken);

        // A worker iteration that passed its pause check before the heartbeat flipped the holder still
        // completes its claim (rule 6.8): on CI one did, claiming the job published below while the server was
        // paused. So wait until every worker loop that has checked at all has since answered "paused"; none
        // can claim after that.
        await loops.WaitUntilEveryLoopSawPauseAsync(Xunit.TestContext.Current.CancellationToken);

        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Resume — job should complete
        var resumedAt = DateTime.UtcNow;
        await svc.ResumeServer(server.ServerId);
        await server.RunHeartbeatOnceAsync(Xunit.TestContext.Current.CancellationToken);
        await server.WaitForCompletion();

        var job = await server.GetJob(jobId);
        job.CurrentState.ShouldBe(State.Completed);

        // It was claimed AFTER the resume. Without this the test passes just the same when the
        // pause never held and the job ran straight away.
        var logs = await server.GetJobLogs(jobId);
        var processing = logs.Single(x => string.Equals(x.EventType, "Processing", StringComparison.Ordinal));
        processing.Timestamp.ShouldBeGreaterThanOrEqualTo(resumedAt);
    }

    [TimedFact]
    public async Task PauseWorkerGroup_JobsStayEnqueued()
    {
        // See PauseServer_JobsStayEnqueued for why we drive Heartbeat manually.
        using var loops = new WorkerLoopPauseTracker();
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.HealthCheckInterval = null,
            configureServices: services => services.AddSingleton<PauseStateHolder>(loops));
        var groupId = await GetFirstGroupId(server);
        var svc = server.CreateServerCommandService();

        await svc.PauseWorkerGroup(groupId);
        await server.RunHeartbeatOnceAsync(Xunit.TestContext.Current.CancellationToken);
        server.PauseState.IsPaused(groupId).ShouldBeTrue();

        // Wait out claims already in flight — see PauseServer_JobsStayEnqueued.
        await loops.WaitUntilEveryLoopSawPauseAsync(Xunit.TestContext.Current.CancellationToken);

        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Negative-window assertion — see PauseServer_JobsStayEnqueued for rationale.
        await Should.ThrowAsync<TimeoutException>(
            async () => await server.WaitForCompletion(TimeSpan.FromSeconds(2)));

        var jobBeforeResume = await server.GetJob(jobId);
        jobBeforeResume.CurrentState.ShouldBe(State.Enqueued);

        await svc.ResumeWorkerGroup(groupId);
        await server.RunHeartbeatOnceAsync(Xunit.TestContext.Current.CancellationToken);
        await server.WaitForCompletion();
    }

    [TimedFact]
    public async Task PauseWorkerGroup_Resume_JobsGetProcessed()
    {
        await using var server = await WarpTestServer.StartAsync(Fixture);
        var groupId = await GetFirstGroupId(server);
        var svc = server.CreateServerCommandService();

        // Pause, publish, resume
        await svc.PauseWorkerGroup(groupId);
        await server.WaitForPauseState(groupId, expectedPaused: true);

        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await svc.ResumeWorkerGroup(groupId);
        await server.WaitForCompletion();

        var job = await server.GetJob(jobId);
        job.CurrentState.ShouldBe(State.Completed);
    }

    // Tells the worker loops apart without touching the worker: each loop is one async flow, and an AsyncLocal
    // set inside a synchronous call (IsPaused) stays set in the caller's flow, so a loop's first check gives it
    // an identity that its later checks see again. A loop that last answered "not paused" may be inside a claim.
    private sealed class WorkerLoopPauseTracker : PauseStateHolder, IDisposable
    {
        private static readonly AsyncLocal<object?> Loop = new();
        private readonly ConcurrentDictionary<object, bool> _lastAnswer = new(ReferenceEqualityComparer.Instance);
        private readonly SemaphoreSlim _checked = new(0);

        public override bool IsPaused(Guid? workerGroupId)
        {
            var paused = base.IsPaused(workerGroupId);
            _lastAnswer[Loop.Value ??= new object()] = paused;
            _checked.Release();

            return paused;
        }

        public async Task WaitUntilEveryLoopSawPauseAsync(CancellationToken ct)
        {
            while (_lastAnswer.Values.Any(x => !x))
            {
                await _checked.WaitAsync(ct);
            }
        }

        public void Dispose() => _checked.Dispose();
    }
}
