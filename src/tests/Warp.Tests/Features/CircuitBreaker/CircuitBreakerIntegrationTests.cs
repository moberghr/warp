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

namespace Warp.Tests.Features.CircuitBreaker;

[GenerateDatabaseTests]
public abstract class CircuitBreakerIntegrationTestsBase : IntegrationTestBase
{
    protected CircuitBreakerIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenFailingHandler_WhenThresholdHit_ThenCircuitOpensAndSubsequentJobsRescheduled()
    {
        const string groupKey = nameof(ThrowExceptionRequest);

        await using var server = await WarpTestServer.StartAsync(Fixture);

        // Seed the state to threshold - 1, then drive one more failure to open the circuit.
        // This avoids waiting through the global Retry (MaxRetries=3) on every failure.
        var seedCtx = Fixture.CreateContext();
        seedCtx.Set<CircuitBreakerState>().Add(new CircuitBreakerState
        {
            GroupKey = groupKey,
            FailureCount = 999,
            LastFailureAt = DateTime.UtcNow,
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var failingPublisher = server.CreatePublisher();
        var failingJobId = await failingPublisher.Enqueue(
            new ThrowExceptionRequest(),
            new JobParameters().Configure<IRetryMetadata>(m => m.MaxRetries = 0));
        await failingPublisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(failingJobId, State.Failed, timeout: TimeSpan.FromSeconds(8));

        // Verify circuit is open
        var stateCtx = Fixture.CreateContext();
        var state = await stateCtx.Set<CircuitBreakerState>()
            .Where(x => x.GroupKey == groupKey)
            .FirstOrDefaultAsync(CancellationToken.None);
        state.ShouldNotBeNull();
        state.FailureCount.ShouldBeGreaterThanOrEqualTo(1000);
        state.OpenUntil.ShouldNotBeNull();

        // Publish a subsequent job — circuit is open so it should be rescheduled
        var nextPublisher = server.CreatePublisher();
        var nextJobId = await nextPublisher.Enqueue(new ThrowExceptionRequest());
        await nextPublisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Circuit breaker reschedules to a future time → state becomes Scheduled.
        await server.WaitForJobLog(nextJobId, "Scheduled", timeout: TimeSpan.FromSeconds(15));

        // Worker writes Scheduled when the reschedule time is in the future; the activation
        // task only flips it back to Enqueued once OpenUntil has elapsed (several minutes here).
        var readCtx = Fixture.CreateContext();
        var nextJob = await readCtx.Set<Job>()
            .Where(x => x.Id == nextJobId)
            .FirstOrDefaultAsync(CancellationToken.None);
        nextJob.ShouldNotBeNull();
        nextJob.CurrentState.ShouldBe(State.Scheduled);
        nextJob.ScheduleTime.ShouldBeGreaterThanOrEqualTo(state.OpenUntil!.Value);

        var log = await readCtx.Set<JobLog>()
            .Where(x => x.JobId == nextJobId)
            .Where(x => x.Message.Contains("circuit breaker"))
            .FirstOrDefaultAsync(CancellationToken.None);
        log.ShouldNotBeNull();
        log.Message.ShouldContain(groupKey);

        // Cancel rescheduled job so it doesn't linger
        var cmd = server.CreateCommandService();
        await cmd.DeleteJob(nextJobId);
    }

    [TimedFact]
    public async Task GivenSuccessAfterFailures_WhenJobSucceeds_ThenCounterResets()
    {
        const string groupKey = nameof(UnitRequest);

        await using var server = await WarpTestServer.StartAsync(Fixture);

        var seedCtx = Fixture.CreateContext();
        seedCtx.Set<CircuitBreakerState>().Add(new CircuitBreakerState
        {
            GroupKey = groupKey,
            FailureCount = 2,
            LastFailureAt = DateTime.UtcNow,
        });
        await seedCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await server.WaitForJobState(jobId, State.Completed, timeout: TimeSpan.FromSeconds(8));

        var readCtx = Fixture.CreateContext();
        var state = await readCtx.Set<CircuitBreakerState>()
            .Where(x => x.GroupKey == groupKey)
            .FirstOrDefaultAsync(CancellationToken.None);
        state.ShouldNotBeNull();
        state.FailureCount.ShouldBe(0);
        state.OpenUntil.ShouldBeNull();
    }
}
