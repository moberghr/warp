using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.ErrorGrouping;

/// <summary>
/// End-to-end guard for §8.29 in the DEFAULT single-worker mode (<c>UseDispatcher = false</c>). Here
/// <c>WarpWorkerService.FinalizeJobState</c> — not the dispatcher's batched completion copy
/// (<see cref="ErrorGroupDispatcherIntegrationTestsBase"/>) — is the code that appends the
/// <see cref="ErrorOccurrence"/> inbox row on failure. This boots a real single-worker server, fails a job,
/// drives the <see cref="ErrorGroupAggregator{TContext}"/> once, and asserts the issue was created with the
/// real (unwrapped) exception type AND the inbox drained — proving the single-worker path feeds the inbox.
/// </summary>
[GenerateDatabaseTests]
public abstract class ErrorGroupSingleWorkerIntegrationTestsBase : IntegrationTestBase
{
    protected ErrorGroupSingleWorkerIntegrationTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    private static void ConfigureSingleWorker(WarpServerBuilder<TestContext> config)
    {
        config.UseDispatcher = false;
        config.WorkerCount = 2;

        // Keep error grouping ENABLED (non-null ⇒ the worker feeds the inbox on failure — the path under test),
        // but push the aggregator's auto-loop far out so it can't race the explicit RunServerTaskOnceAsync drive
        // below. The loop still fires once at startup against an empty inbox; the next auto-run is 5 min away.
        config.ErrorGroupingInterval = TimeSpan.FromMinutes(5);
    }

    [TimedFact(timeout: 30_000)]
    public async Task GivenSingleWorkerMode_WhenJobFails_ThenErrorGroupIsCreatedAndInboxDrained()
    {
        // Arrange — single-worker-mode server; enqueue a handler that throws deterministically.
        await using var server = await WarpTestServer.StartAsync(Fixture, ConfigureSingleWorker);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new ThrowExceptionRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // The default test-server retry is MaxRetries=1 (1s delay), so the job fails, retries once, then lands
        // terminal Failed. Every caught attempt exception feeds the inbox in FinalizeJobState, so once the
        // terminal state is committed the ErrorOccurrence rows are committed too.
        await server.WaitForJobState(jobId, State.Failed, TimeSpan.FromSeconds(15));

        // Act — drive the ErrorGroupAggregator once (its auto-loop is pushed out to 5 min so this is the only
        // drain that matters). This folds the worker-written inbox rows into a durable issue.
        await server.RunServerTaskOnceAsync<ErrorGroupAggregator<TestContext>>(Xunit.TestContext.Current.CancellationToken);

        // Assert — a Job-source issue exists for the thrown exception, and the inbox was fully drained.
        var ctx = Fixture.CreateContext();
        var group = (await ctx.Set<ErrorGroup>()
                .AsNoTracking()
                .Where(x => x.Source == ErrorSource.Job)
                .ToListAsync(Xunit.TestContext.Current.CancellationToken))
            .ShouldHaveSingleItem();
        group.Count.ShouldBeGreaterThanOrEqualTo(1);
        group.ExceptionType.ShouldBe("System.NotImplementedException");   // real, unwrapped type

        var inboxRemaining = await ctx.Set<ErrorOccurrence>()
            .AsNoTracking()
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        inboxRemaining.ShouldBe(0);
    }
}
