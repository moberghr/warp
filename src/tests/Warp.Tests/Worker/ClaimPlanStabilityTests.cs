using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Worker;

/// <summary>
/// The claim must take at most its limit whatever plan PostgreSQL chooses for it.
/// <para>
/// Found as jobs stuck in Processing with no worker running them. The claim was
/// <c>UPDATE job t ... FROM (SELECT id ... LIMIT n FOR UPDATE SKIP LOCKED) c WHERE t.id = c.id</c>,
/// and when the planner believed the table empty it put <c>t</c> on the OUTER side of a nested loop
/// and re-ran the limited subquery once per outer row. Each re-run skips the row the same statement
/// has just updated and returns the next one in schedule order, so a single <c>LIMIT 1</c> claimed
/// every row whose id also sorted later. The worker runs only <c>claimed[0]</c>; the rest were
/// orphaned until StaleJobRecovery requeued them.
/// </para>
/// <para>
/// The statistics are the exact state captured in CI at every one of 650 multi-row claims:
/// <c>reltuples = 0</c> with pages still allocated and no column statistics — what autovacuum leaves
/// after vacuuming a table whose rows were all deleted, before the next analyze. A queue that drains
/// to empty and then takes a burst reaches it in production too. Injecting it (PostgreSQL 18's
/// <c>pg_restore_relation_stats</c>) makes the plan deterministic instead of a race with autovacuum.
/// Ids ascend with schedule time, which is the case where the faulty plan takes every row.
/// </para>
/// </summary>
[GenerateDatabaseTests]
public abstract class ClaimPlanStabilityTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ClaimPlanStabilityTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task ClaimEnqueuedJobs_PlannerBelievesTableEmpty_ClaimsNoMoreThanTheLimit()
    {
        if (await IsSqlServerAsync())
        {
            return;
        }

        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedBelievedEmptyAsync(JobKind.Job, ct);

        var claimerCtx = _fixture.CreateContext();
        var queries = Warp.Tests.Helpers.TestTasks.QueriesFor(claimerCtx);

        var claimed = await queries.ClaimEnqueuedJobsAsync(claimerCtx, ["default"], Guid.NewGuid(), DateTime.UtcNow, limit: 1, ct);

        claimed.Count.ShouldBe(1);
        var processing = await _fixture.CreateContext().Set<Job>()
            .Where(x => x.CurrentState == State.Processing)
            .CountAsync(ct);
        processing.ShouldBe(1, "every Processing row the claim did not return is a job no worker will run");
    }

    [TimedFact]
    public async Task ClaimEnqueuedMessages_PlannerBelievesTableEmpty_ClaimsNoMoreThanTheLimit()
    {
        // The message router's batch claim had the same UPDATE ... FROM (limited subquery) shape.
        if (await IsSqlServerAsync())
        {
            return;
        }

        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedBelievedEmptyAsync(JobKind.Message, ct);

        var claimerCtx = _fixture.CreateContext();
        var queries = Warp.Tests.Helpers.TestTasks.QueriesFor(claimerCtx);

        var claimed = await queries.ClaimEnqueuedMessagesAsync(claimerCtx, limit: 1, ct);

        claimed.Count.ShouldBe(1);
        var processing = await _fixture.CreateContext().Set<Job>()
            .Where(x => x.CurrentState == State.Processing)
            .CountAsync(ct);
        processing.ShouldBe(1, "every Processing row the claim did not return is a message nothing will route");
    }

    // PostgreSQL-only: the fault is a PostgreSQL plan shape, and the statistics are injected with a
    // PostgreSQL function. SQL Server's claims are different statements.
    private async Task<bool> IsSqlServerAsync()
    {
        await using var probeCtx = _fixture.CreateContext();

        return probeCtx.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task SeedBelievedEmptyAsync(JobKind kind, CancellationToken ct)
    {
        const int rowCount = 20;
        var now = DateTime.UtcNow;

        var seedCtx = _fixture.CreateContext();
        seedCtx.Set<Job>().AddRange(Enumerable.Range(1, rowCount).Select(i => new Job
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{i:D12}"),
            Kind = kind,
            CurrentState = State.Enqueued,
            Type = typeof(UnitRequest).AssemblyQualifiedName,
            Message = "{}",
            CreateTime = now,
            ScheduleTime = now.AddMilliseconds(i),
            Queue = "default",
        }));
        await seedCtx.SaveChangesAsync(ct);

        await InjectEmptyTableStatisticsAsync(seedCtx, ct);
    }

    private static async Task InjectEmptyTableStatisticsAsync(DbContext context, CancellationToken ct)
    {
        var entity = context.Model.FindEntityType(typeof(Job))!;
        var schema = entity.GetSchema() ?? "public";
        var table = entity.GetTableName()!;

        // No column statistics, as after an analyze that found no rows.
        await context.Database.ExecuteSqlRawAsync(
            @"SELECT pg_clear_attribute_stats({0}, {1}, a.attname, false)
              FROM pg_attribute a
              WHERE a.attrelid = to_regclass(format('%I.%I', {0}, {1})) AND a.attnum > 0 AND NOT a.attisdropped",
            [schema, table],
            ct);

        await context.Database.ExecuteSqlRawAsync(
            "SELECT pg_restore_relation_stats('schemaname', {0}::text, 'relname', {1}::text, "
            + "'relpages', 114, 'reltuples', 0::real, 'relallvisible', 45)",
            [schema, table],
            ct);
    }
}
