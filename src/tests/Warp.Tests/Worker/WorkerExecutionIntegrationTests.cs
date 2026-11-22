using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Worker;

// What a worker does around a handler that is still running: renewing its hold on the row, and what
// becomes of a job whose handler is told to stop and does not. Each case runs in single-worker and in
// dispatcher mode, which keep separate copies of this code.
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class WorkerExecutionIntegrationTestsBase : IntegrationTestBase
{
    protected WorkerExecutionIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenLongRunningHandler_WhileItRuns_ThenKeepAliveIsRenewed(bool useDispatcher)
    {
        // StaleJobRecovery requeues a Processing row whose LastKeepAlive stops moving, so a handler
        // that outlives the stale threshold depends on the job monitor renewing it.
        var barrier = new BarrierSignal();
        await using var server = await StartAsync(useDispatcher, barrier);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new BarrierRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        try
        {
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the handler never started");

            var atStart = (await server.GetJob(jobId)).LastKeepAlive;
            atStart.ShouldNotBeNull();

            await WarpTestServer.WaitUntil(
                async () => (await server.GetJob(jobId)).LastKeepAlive > atStart,
                TimeSpan.FromSeconds(5),
                Xunit.TestContext.Current.CancellationToken);
        }
        finally
        {
            barrier.CanFinish.Release();
        }

        await server.WaitForJobState(jobId, State.Completed, TimeSpan.FromSeconds(5));
    }

    [TimedTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenHandlerThatIgnoresCancellation_WhenItFinishesItsWork_ThenJobIsCompleted(bool useDispatcher)
    {
        // Rule 8.5: the work happened, so the job is Completed, and the cancellation request does not
        // outlive it on the row.
        var barrier = new BarrierSignal();
        await using var server = await StartAsync(useDispatcher, barrier);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new IgnoresCancellationRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        try
        {
            (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))
                .ShouldBeTrue("the handler never started");

            await server.CreateCommandService().DeleteJob(jobId);
            (await server.GetJob(jobId)).CancellationMode.ShouldBe(CancellationMode.Graceful);
        }
        finally
        {
            barrier.CanFinish.Release();
        }

        await server.WaitForJobLog(jobId, "Completed", timeout: TimeSpan.FromSeconds(5));

        var job = await server.GetJob(jobId);
        job.CurrentState.ShouldBe(State.Completed);
        job.CancellationMode.ShouldBe(CancellationMode.None);
        job.CurrentWorkerId.ShouldBeNull();

        var logs = await server.GetJobLogs(jobId);
        logs.ShouldNotContain(x => string.Equals(x.EventType, "Cancelled", StringComparison.Ordinal));
    }

    private Task<WarpTestServer> StartAsync(bool useDispatcher, BarrierSignal barrier) =>
        WarpTestServer.StartAsync(
            Fixture,
            cfg =>
            {
                cfg.UseDispatcher = useDispatcher;
                cfg.WorkerCount = 2;
                cfg.CompletionBatchSize = 10;
                cfg.CompletionFlushInterval = TimeSpan.FromMilliseconds(50);
            },
            services => services.AddSingleton(barrier));
}
