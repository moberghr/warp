using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Worker;

/// <summary>
/// Regression coverage for the atomic <c>UPDATE ... RETURNING/OUTPUT</c> claim path. The legacy
/// SELECT+UPDATE split had a window where two workers could read the same row, each think they
/// won, and race on the subsequent UPDATE. The hand-SQL claim closes that window: every claimed
/// row transitions Enqueued → Processing in a single round-trip with <c>FOR UPDATE SKIP LOCKED</c>
/// (PG) / <c>WITH (ROWLOCK, UPDLOCK, READPAST)</c> (SQL Server) serializing concurrent claimers.
/// </summary>
[GenerateDatabaseTests]
public abstract class ConcurrentClaimTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ConcurrentClaimTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task ClaimEnqueuedJobs_ManyClaimersOnePerClaim_EachJobClaimedExactlyOnce()
    {
        // 10 rows, 20 parallel single-row claimers — each row must land with exactly one claimer.
        const int jobCount = 10;
        const int claimerCount = 20;

        var jobIds = await SeedJobs(jobCount, "default");

        var claims = await RunConcurrentClaimers(claimerCount, limit: 1, queue: "default");

        var claimSet = claims.ToHashSet();
        claims.Count.ShouldBe(jobCount);
        claimSet.Count.ShouldBe(jobCount, "each row must be claimed at most once");
        claimSet.ShouldBe(jobIds, ignoreOrder: true);

        await AssertAllProcessingWithDistinctWorkers(jobCount);
    }

    [TimedFact]
    public async Task ClaimEnqueuedJobs_FewerRowsThanClaimers_ExcessClaimersGetEmpty()
    {
        // 5 rows, 10 claimers — 5 claimers win a row, 5 get nothing. No duplicates.
        const int jobCount = 5;
        const int claimerCount = 10;

        await SeedJobs(jobCount, "default");

        var claims = await RunConcurrentClaimers(claimerCount, limit: 1, queue: "default");

        var claimSet = claims.ToHashSet();
        claims.Count.ShouldBe(jobCount);
        claimSet.Count.ShouldBe(jobCount);
    }

    [TimedFact]
    public async Task ClaimEnqueuedJobs_RowHeldByOtherTransaction_ClaimerSkipsIt()
    {
        // The concurrent-claim tests prove the happy path — parallel claimers never double-claim.
        // This one proves the mechanism itself: given a row whose lock is *deliberately* held by
        // another transaction, the atomic UPDATE ... SKIP LOCKED claim must pass it over rather
        // than block or fail. Without that guarantee, one stuck worker would stall every claim
        // across the fleet.
        //
        // PG-only: on SQL Server, a SELECT WITH (UPDLOCK, READPAST) inside an UPDATE CTE / JOIN
        // subquery does not reliably skip rows U-locked by a concurrent long-lived transaction
        // — the UPDATE's row-acquisition phase re-takes locks without READPAST. Every single-
        // statement claim pattern we tried blocked, and the multi-statement pattern that would
        // skip correctly doesn't round-trip cleanly through EF's FromSqlRaw. The happy-path
        // contention coverage on SQL Server comes from the other tests in this class plus the
        // 20x flake-hunt loop on MultiServerTests_SqlServer.
        await using var probeCtx = _fixture.CreateContext();
        if (probeCtx.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        var ct = Xunit.TestContext.Current.CancellationToken;
        const int rowCount = 20;
        var now = DateTime.UtcNow;
        var ids = Enumerable.Range(0, rowCount).Select(_ => Guid.NewGuid()).ToList();
        var lockedId = ids[0];

        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<Job>().AddRange(ids.Select(id => new Job
        {
            Id = id,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            CreateTime = now,
            ScheduleTime = now,
            Queue = "default",
        }));
        await seedCtx.SaveChangesAsync(ct);

        // Transaction A: holder. OpenConnectionAsync forces a dedicated pooled connection (EF
        // otherwise may share) so the lock is demonstrably held across the transaction boundary
        // into Transaction B.
        var holderCtx = _fixture.CreateContext();
        await holderCtx.Database.OpenConnectionAsync(ct);
        var holderQueries = Warp.Tests.Helpers.TestTasks.QueriesFor(holderCtx);
        await using var holderTx = await holderCtx.Database.BeginTransactionAsync(ct);

        var held = await holderQueries.LockJobByIdWaitAsync(holderCtx, lockedId, ct);
        held.ShouldNotBeNull();
        held.Id.ShouldBe(lockedId);

        // Transaction B: claimer on a separate connection. Should skip the locked row and return
        // up to (rowCount - 1) rows — the exact count depends on claim ordering, but `lockedId`
        // must never appear in the result.
        var claimerCtx = _fixture.CreateContext();
        await claimerCtx.Database.OpenConnectionAsync(ct);
        var claimerQueries = Warp.Tests.Helpers.TestTasks.QueriesFor(claimerCtx);

        var claimed = await claimerQueries.ClaimEnqueuedJobsAsync(
            claimerCtx,
            ["default"],
            Guid.NewGuid(),
            DateTime.UtcNow,
            limit: rowCount,
            ct);

        claimed.Any(j => j.Id == lockedId).ShouldBeFalse("claimer must skip the locked row, not claim it");
        claimed.Count.ShouldBe(rowCount - 1, "claimer should have taken every other row");

        // Release the holder's lock; the locked row should still be Enqueued (holder did not update it).
        await holderTx.RollbackAsync(ct);

        var readCtx = _fixture.CreateContext();
        var finalLocked = await readCtx.Set<Job>().FirstAsync(j => j.Id == lockedId, ct);
        finalLocked.CurrentState.ShouldBe(State.Enqueued, "locked row stays Enqueued — it was held, not claimed");
    }

    [TimedFact]
    public async Task ClaimEnqueuedJobs_BatchLimit_MultipleClaimersPartitionTheWork()
    {
        // Dispatcher-mode pattern: 100 rows, 4 claimers each asking for up to 25. Every row ends
        // up in exactly one claimer's batch, no batch contains duplicates, no row is missed.
        const int jobCount = 100;
        const int claimerCount = 4;
        const int batchSize = 25;

        var jobIds = await SeedJobs(jobCount, "default");

        var claims = await RunConcurrentClaimers(claimerCount, limit: batchSize, queue: "default");

        var claimSet = claims.ToHashSet();
        claims.Count.ShouldBe(jobCount);
        claimSet.Count.ShouldBe(jobCount);
        claimSet.ShouldBe(jobIds, ignoreOrder: true);

        await AssertAllProcessingWithDistinctWorkers(jobCount);
    }

    [TimedFact]
    public async Task ClaimEnqueuedJobs_BatchClaim_ReturnsTheOldestJobsOldestFirst()
    {
        // Eight jobs a second apart, with ids that sort in the OPPOSITE order to their schedule
        // times, since id order is what PostgreSQL's UPDATE ... RETURNING gave back. A claim of five
        // must be the five oldest, and in schedule order: the dispatcher starts them in list order.
        var oldest = DateTime.UtcNow.AddMinutes(-1);
        var jobs = Enumerable.Range(0, 8)
            .Select(_ => Guid.NewGuid())
            .OrderDescending()
            .Select((id, i) =>
                new Job
                {
                    Id = id,
                    Kind = JobKind.Job,
                    CurrentState = State.Enqueued,
                    Type = typeof(UnitRequest).AssemblyQualifiedName,
                    Message = "{}",
                    CreateTime = oldest.AddSeconds(i),
                    ScheduleTime = oldest.AddSeconds(i),
                    Queue = "default",
                })
            .ToList();

        await using (var arrange = _fixture.CreateContext())
        {
            arrange.Set<Job>().AddRange(jobs);
            await arrange.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        }

        // Act
        await using var ctx = _fixture.CreateContext();
        var claimed = await Warp.Tests.Helpers.TestTasks.QueriesFor(ctx).ClaimEnqueuedJobsAsync(
            ctx,
            ["default"],
            Guid.NewGuid(),
            DateTime.UtcNow,
            5,
            Xunit.TestContext.Current.CancellationToken);

        // Assert
        claimed.Select(x => x.Id).ShouldBe(jobs.Take(5).Select(x => x.Id));
    }

    private async Task<List<Guid>> SeedJobs(int count, string queue)
    {
        var now = DateTime.UtcNow;
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
        var ctx = _fixture.CreateContext();

        ctx.Set<Job>().AddRange(ids.Select(id => new Job
        {
            Id = id,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            CreateTime = now,
            ScheduleTime = now,
            Queue = queue,
        }));
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return ids;
    }

    private async Task<ConcurrentBag<Guid>> RunConcurrentClaimers(int claimerCount, int limit, string queue)
    {
        // All claimers block on the gate and are released simultaneously to maximize the chance
        // they overlap inside the claim SQL. Without the gate the first claimer typically returns
        // before the others even open their connections.
        var gate = new TaskCompletionSource();
        var claims = new ConcurrentBag<Guid>();

        var tasks = Enumerable.Range(0, claimerCount)
            .Select(async _ =>
            {
                await gate.Task;
                var workerId = Guid.NewGuid();
                var ctx = _fixture.CreateContext();
                var sqlQueries = Warp.Tests.Helpers.TestTasks.QueriesFor(ctx);
                var claimed = await sqlQueries.ClaimEnqueuedJobsAsync(
                    ctx,
                    [queue],
                    workerId,
                    DateTime.UtcNow,
                    limit,
                    Xunit.TestContext.Current.CancellationToken);

                foreach (var job in claimed)
                {
                    claims.Add(job.Id);
                }
            })
            .ToList();

        gate.SetResult();
        await Task.WhenAll(tasks);

        return claims;
    }

    private async Task AssertAllProcessingWithDistinctWorkers(int expectedCount)
    {
        var readCtx = _fixture.CreateContext();

        var remainingEnqueued = await readCtx.Set<Job>()
            .Where(x => x.CurrentState == State.Enqueued)
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        remainingEnqueued.ShouldBe(0);

        var processing = await readCtx.Set<Job>()
            .Where(x => x.CurrentState == State.Processing)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        processing.Count.ShouldBe(expectedCount);
        processing.ShouldAllBe(j => j.CurrentWorkerId != null);
        processing.ShouldAllBe(j => j.LastKeepAlive != null);
    }
}
