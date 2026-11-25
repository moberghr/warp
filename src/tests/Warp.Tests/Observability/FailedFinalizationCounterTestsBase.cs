using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Observability;

/// <summary>
/// A job whose success-path write failed must be counted once, as failed — never as both.
/// <para>
/// The worker stages a finalization's counter increments on itself and hands them to the shared buffer
/// only after the transaction commits. When that commit throws, control lands in the failure arm, which
/// finalizes the SAME job a second time and commits successfully — so anything still staged from the
/// abandoned attempt rides across with the failure's own increments and <c>stats:succeeded</c> and
/// <c>stats:failed</c> both move for one job. That breaks the reconciliation §8.33 requires and lets the
/// attributed reason breakdown exceed its own state total, silently: the job row is correct, the metrics
/// are not, and nothing in the run looks wrong.
/// </para>
/// <para>
/// Reaching it needs fault injection, which is why the sibling
/// <see cref="StagedCounterCommitTestsBase"/> could not cover it: both arms write the same rows through
/// the same context, so there is no asymmetry in the data to exploit. The fault goes in through
/// <see cref="IWarpServerContextConfigurator"/> — the seam the providers use to configure the server
/// context — so the worker hot path keeps no test hook (§0.2/§6.1).
/// </para>
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class FailedFinalizationCounterTestsBase : IntegrationTestBase
{
    protected FailedFinalizationCounterTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task StagedCounters_OfAnAttemptWhoseFinalizationFailed_DoNotRideTheFailureArmsCommit()
    {
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            config =>
            {
                config.WorkerCount = 1;

                // The fault is injected into single-worker finalization (EF SaveChanges); dispatcher completions are one SQL statement.
                config.UseDispatcher = false;

                // The background flusher would otherwise drain the buffer before the assertion reads it.
                config.CounterBufferFlushInterval = TimeSpan.FromMinutes(10);
            },
            FailTheFirstCompletion);

        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync();

        // The handler succeeds; the write that records it does not, so the worker re-finalizes as Failed.
        await server.WaitForJobState(jobId, State.Failed);

        // The arm that committed hands its increments to the buffer just AFTER its commit, so the row
        // can read Failed a moment before they arrive. Draining is destructive, so collect until the
        // failure arm's own counter shows up (WaitUntil throws if it never does).
        var keys = new List<string>();
        await WarpTestServer.WaitUntil(
            () =>
            {
                keys.AddRange(server.CounterBuffer.Drain().Select(x => x.Key));

                return Task.FromResult(keys.Contains("stats:failed", StringComparer.Ordinal));
            },
            TimeSpan.FromSeconds(5),
            Xunit.TestContext.Current.CancellationToken);

        keys.ShouldNotContain(
            "stats:succeeded",
            "the attempt that rolled back must contribute nothing — one job cannot be both");
    }

    // The counters above are only half of it: the job row and its logs must also be those of a clean
    // Failed job. The success arm had already set ExpireAt (a Completed job expires) and added its
    // "Completed" log to the context before the write failed, and the failure arm then finalized the same
    // tracked job. A Failed job left with ExpireAt set is deleted by ExpirationCleanup a day later, which
    // rule 8.2 says never happens to a failed job.
    [TimedFact]
    public async Task CompletionSaveFails_ReFinalizedAsFailed_IsACleanFailedRow()
    {
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            config =>
            {
                config.WorkerCount = 1;

                // The fault is injected into single-worker finalization (EF SaveChanges); dispatcher completions are one SQL statement.
                config.UseDispatcher = false;
            },
            FailTheFirstCompletion);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync();

        await server.WaitForJobState(jobId, State.Failed);

        await AssertCleanFailedRow(jobId);
    }

    // The other way the success arm's write can fail: the save goes through and the COMMIT throws. EF has
    // by then marked the job's changes as saved, so the failure arm sees nothing to write for the worker
    // fields it believes are already cleared, while the database rolled them back.
    [TimedFact]
    public async Task CompletionCommitFails_ReFinalizedAsFailed_IsACleanFailedRow()
    {
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            config =>
            {
                config.WorkerCount = 1;

                // The fault is injected into single-worker finalization (EF SaveChanges); dispatcher completions are one SQL statement.
                config.UseDispatcher = false;
            },
            FailTheFirstCompletionCommit);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync();

        await server.WaitForJobState(jobId, State.Failed);

        await AssertCleanFailedRow(jobId);
    }

    // The diagnostic rows of the job ride the same finalizing write: the deferred "Processing" row and the
    // handler's log lines. When the save went through and the COMMIT threw, EF considered them saved while
    // the database rolled them back, and the re-finalization did not write them again: the Failed job
    // had no "Processing" row and none of its handler's lines.
    [TimedFact]
    public async Task CompletionCommitFails_ReFinalizedAsFailed_KeepsTheProcessingRowAndTheHandlersLogLines()
    {
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            config =>
            {
                config.WorkerCount = 1;

                // The fault is injected into single-worker finalization (EF SaveChanges); dispatcher completions are one SQL statement.
                config.UseDispatcher = false;
            },
            FailTheFirstCompletionCommit);
        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new LoggingRequest());
        await publisher.SaveChangesAsync();

        await server.WaitForJobState(jobId, State.Failed);

        var logs = await server.GetJobLogs(jobId);
        logs.Count(x => string.Equals(x.EventType, "Processing", StringComparison.Ordinal)).ShouldBe(1);
        logs.Count(x => x.Message?.EndsWith("Processing logging request", StringComparison.Ordinal) == true).ShouldBe(1);
        logs.Count(x => x.Message?.EndsWith("This is a warning", StringComparison.Ordinal) == true).ShouldBe(1);
    }

    private async Task AssertCleanFailedRow(Guid jobId)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var ctx = Fixture.CreateContext();
        var job = await ctx.Set<Job>().AsNoTracking().SingleAsync(x => x.Id == jobId, ct);
        var terminalLogs = await ctx.Set<Warp.Core.Data.Entities.JobLog>()
            .AsNoTracking()
            .Where(x => x.JobId == jobId)
            .Where(x => x.EventType == "Completed" || x.EventType == "Failed")
            .Select(x => x.EventType)
            .ToListAsync(ct);

        job.CurrentState.ShouldBe(State.Failed);
        job.ExpireAt.ShouldBeNull("a failed job never expires (rule 8.2)");
        job.CurrentWorkerId.ShouldBeNull();
        job.LastKeepAlive.ShouldBeNull();
        terminalLogs.ShouldBe(["Failed"], "the abandoned attempt's Completed log must not be written");
    }

    private static void FailTheFirstCompletionCommit(IServiceCollection services) =>
        services.AddServerContextInterceptor(new FailFirstCompletionCommitInterceptor());

    /// <summary>
    /// Replaces the provider's server-context configurator with one that delegates to it and adds the
    /// interceptor. Registered last so <c>GetService</c> returns it; the provider's own instance is taken
    /// from the descriptor so none of its connection wiring is reimplemented here.
    /// </summary>
    private static void FailTheFirstCompletion(IServiceCollection services) =>
        services.AddServerContextInterceptor(new FailFirstCompletionInterceptor());

    /// <summary>
    /// Throws once, on the write that records a job as Completed. Everything after it — including the
    /// failure arm's own write of the same job — goes through, which is the whole point: the failure arm
    /// has to reach its commit for the abandoned attempt's increments to be observable.
    /// </summary>
    private sealed class FailFirstCompletionInterceptor : SaveChangesInterceptor
    {
        private int _fired;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var completing = eventData.Context?.ChangeTracker
                .Entries<Job>()
                .Any(x => x.State == EntityState.Modified && x.Entity.CurrentState == State.Completed) == true;

            if (completing && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                throw new InvalidOperationException("Injected finalization failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Throws once, on the COMMIT of the transaction that records a job as Completed: the save before it
    /// succeeded, so EF considers the job's changes written.
    /// </summary>
    private sealed class FailFirstCompletionCommitInterceptor : DbTransactionInterceptor
    {
        private int _fired;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            var completed = eventData.Context?.ChangeTracker
                .Entries<Job>()
                .Any(x => x.Entity.CurrentState == State.Completed) == true;

            if (completed && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                throw new InvalidOperationException("Injected: the completion commit failed.");
            }

            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }
}
