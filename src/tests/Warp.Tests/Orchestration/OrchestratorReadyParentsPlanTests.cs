using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Worker.Services;

namespace Warp.Tests.Orchestration;

// Rot guard for rule 6.11. The Orchestrator asks "which batch or message parents are ready to
// finalize?" on almost every job completion. Every job passes through Processing, and the first shape
// of this query made PostgreSQL walk an index entry for every row that had recently LEFT Processing
// (10,004 pages to return nothing). The behaviour tests cannot see that: the rows returned were always
// right. This test measures the pages the real query reads, with thousands of rows that have just left
// Processing and no batch or message in the table, so it must read next to nothing.
//
// The second test does the same for the queries that decide which AWAITING rows can move.
[GenerateDatabaseTests]
public abstract partial class OrchestratorReadyParentsPlanTestsBase : IAsyncLifetime
{
    private const int RowsThatLeftProcessing = 3_000;

    // The good plan reads a handful of pages; the bad one read more than one per row. Far enough from
    // both that neither a page split nor a planner version makes it flap.
    private const int MaxPages = 200;

    private readonly IDatabaseFixture _fixture;

    protected OrchestratorReadyParentsPlanTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task ReadyParentsQuery_ManyRowsJustLeftProcessing_ReadsAHandfulOfPages()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        await using (var seed = _fixture.CreateContext())
        {
            seed.Set<Job>().AddRange(Enumerable.Range(0, RowsThatLeftProcessing).Select(_ =>
                new Job
                {
                    Id = Guid.NewGuid(),
                    Kind = JobKind.Job,
                    CurrentState = State.Processing,
                    CreateTime = DateTime.UtcNow,
                    ScheduleTime = DateTime.UtcNow,
                    Queue = "default",
                }));
            await seed.SaveChangesAsync(ct);

            // Leaves a not-yet-vacuumed entry in every index that contains CurrentState.
            await seed.Set<Job>()
                .Where(x => x.CurrentState == State.Processing)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.CurrentState, State.Completed), ct);
        }

        await using var context = _fixture.CreateContext();
        var query = Orchestrator<TestContext>.ReadyParentsQuery(context, batchSize: 1000);

        var (pages, detail) = await MeasurePagesReadAsync(context, query, ct);

        pages.ShouldBeLessThan(MaxPages, detail);
    }

    // The other half of the Orchestrator's pass: "which awaiting rows can move?". A batch workload half
    // way through, five running batches each with a continuation batch of 100 awaiting jobs, in a table
    // that also holds thousands of completed jobs. As one EXISTS query per question this read 5,063
    // pages on PostgreSQL with only 250 completed rows, because each of the 505 awaiting rows had every
    // Completed row read for it; the three steps it is now read 13, 15 and 46.
    [TimedFact]
    public async Task AwaitingRowQueries_BatchWorkloadAmongManyCompletedJobs_EachReadAHandfulOfPages()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        Job Row(JobKind kind, State state, Guid? parent) =>
            new()
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                CurrentState = state,
                ParentJobId = parent,
                CreateTime = now,
                ScheduleTime = now,
                Queue = "default",
            };

        await using (var seed = _fixture.CreateContext())
        {
            for (var i = 0; i < 5; i++)
            {
                var parent = Row(JobKind.Batch, State.Processing, null);
                var continuation = Row(JobKind.Batch, State.Awaiting, parent.Id);
                seed.Set<Job>().Add(parent);
                seed.Set<Job>().Add(continuation);
                seed.Set<Job>().AddRange(Enumerable.Range(0, 100).Select(x => Row(JobKind.Job, x < 50 ? State.Completed : State.Enqueued, parent.Id)));
                seed.Set<Job>().AddRange(Enumerable.Range(0, 100).Select(_ => Row(JobKind.Job, State.Awaiting, continuation.Id)));
            }

            // What the old shape's cost multiplied by.
            seed.Set<Job>().AddRange(Enumerable.Range(0, RowsThatLeftProcessing).Select(_ => Row(JobKind.Job, State.Completed, null)));
            await seed.SaveChangesAsync(ct);
        }

        List<Guid> parentIds;
        await using (var context = _fixture.CreateContext())
        {
            parentIds = await Orchestrator<TestContext>.AwaitedParentIdsQuery(context).ToListAsync(ct);

            // 500 jobs wait on 5 continuation batches, which wait on 5 batches: ten parents, not 505.
            parentIds.Count.ShouldBe(10);

            var (pages, detail) = await MeasurePagesReadAsync(context, Orchestrator<TestContext>.AwaitedParentIdsQuery(context), ct);
            pages.ShouldBeLessThan(MaxPages, detail);
        }

        await using (var context = _fixture.CreateContext())
        {
            var (pages, detail) = await MeasurePagesReadAsync(context, Orchestrator<TestContext>.AwaitedParentStatesQuery(context, parentIds), ct);
            pages.ShouldBeLessThan(MaxPages, detail);
        }

        await using (var context = _fixture.CreateContext())
        {
            var (pages, detail) = await MeasurePagesReadAsync(context, Orchestrator<TestContext>.AwaitingChildrenOfQuery(context, parentIds, 1000), ct);
            pages.ShouldBeLessThan(MaxPages, detail);
        }
    }

    private static async Task<(long Pages, string Detail)> MeasurePagesReadAsync(TestContext context, IQueryable query, CancellationToken ct)
    {
        await context.Database.OpenConnectionAsync(ct);
        await using var command = query.CreateDbCommand();

        if (context.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
        {
            // Shared blocks on the root node include every child node's.
            command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + command.CommandText;
            var json = (string)(await command.ExecuteScalarAsync(ct))!;
            using var document = JsonDocument.Parse(json);
            var plan = document.RootElement[0].GetProperty("Plan");

            return (plan.GetProperty("Shared Hit Blocks").GetInt64() + plan.GetProperty("Shared Read Blocks").GetInt64(), json);
        }

        // SQL Server reports logical reads per table as informational messages once the reader is done.
        var connection = (SqlConnection)context.Database.GetDbConnection();
        long logicalReads = 0;
        var messages = new System.Text.StringBuilder();
        void OnMessage(object sender, SqlInfoMessageEventArgs e)
        {
            messages.AppendLine(e.Message);
            foreach (Match match in LogicalReads().Matches(e.Message))
            {
                logicalReads += long.Parse(match.Groups["reads"].Value, CultureInfo.InvariantCulture);
            }
        }

        connection.InfoMessage += OnMessage;
        try
        {
            // In the same batch as the query: the setting then applies to the statement whichever way
            // the provider sends it (parameterised commands go through sp_executesql).
            command.CommandText = "SET STATISTICS IO ON; " + command.CommandText;
            await using var reader = await command.ExecuteReaderAsync(ct);
            do
            {
                while (await reader.ReadAsync(ct))
                {
                    // Drain: the statistics message follows the result set.
                }
            }
            while (await reader.NextResultAsync(ct));
        }
        finally
        {
            connection.InfoMessage -= OnMessage;
        }

        // No message at all means the measurement is broken, not that the query read nothing.
        messages.Length.ShouldBeGreaterThan(0, "SQL Server sent no STATISTICS IO message");

        return (logicalReads, messages.ToString());
    }

    [GeneratedRegex(@"logical reads (?<reads>\d+)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LogicalReads();
}
