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

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();

        // SQL Server: Respawn DELETEs the earlier tests' rows (20,000 in one of them), and their emptied and
        // ghost pages stay allocated until the server's background ghost cleanup gets to them. A scan of the
        // job table then read them too, and the optimizer, costing the scan by those pages, switched to a
        // key lookup per row: 241 and 1,578 pages after the other tests in this class, under 200 alone
        // (#364). A rebuild gives every test the table a fresh database has.
        await using var context = _fixture.CreateContext();
        if (context.Database.ProviderName?.Contains("SqlServer", StringComparison.Ordinal) == true)
        {
            // No parameters; the identifiers come from the model.
            var entity = context.Model.FindEntityType(typeof(Job))!;
            var rebuild = "ALTER INDEX ALL ON [" + entity.GetSchema() + "].[" + entity.GetTableName() + "] REBUILD";
            await context.Database.ExecuteSqlRawAsync(rebuild, Xunit.TestContext.Current.CancellationToken);
        }
    }

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

    // The check for the parents a finished job named. Its cost must not depend on how many other parents
    // are in flight: the sweep reads all 1,000 of these to find none ready (10,924 pages on PostgreSQL),
    // and it used to run on every job completion.
    [TimedFact]
    public async Task HintedReadiness_ThousandMessagesInFlight_ReadsOnlyTheNamedParents()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        var messages = Enumerable.Range(0, 1000).Select(_ => Row(JobKind.Message, State.Processing, null, now)).ToList();
        await using (var seed = _fixture.CreateContext())
        {
            seed.Set<Job>().AddRange(messages);
            seed.Set<Job>().AddRange(messages.Skip(1).Select(x => Row(JobKind.Job, State.Processing, x.Id, now)));
            seed.Set<Job>().Add(Row(JobKind.Job, State.Processing, messages[0].Id, now));
            await seed.SaveChangesAsync(ct);

            // The first message's only child finishes: the row leaves Processing, as in a running system.
            await seed.Set<Job>()
                .Where(x => x.ParentJobId == messages[0].Id)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.CurrentState, State.Completed), ct);
        }

        Guid[] named = [messages[0].Id, messages[1].Id];
        await using var context = _fixture.CreateContext();
        var childStates = Orchestrator<TestContext>.ChildStatesOfQuery(context, named);
        var ready = Orchestrator<TestContext>.ParentsWithSettledChildren(await childStates.ToListAsync(ct));

        ready.ShouldBe([messages[0].Id], "one named parent is ready, the other still has a running child");
        var (childPages, childDetail) = await MeasurePagesReadAsync(context, childStates, ct);
        childPages.ShouldBeLessThan(MaxPages, childDetail);
        var (parentPages, parentDetail) = await MeasurePagesReadAsync(context, Orchestrator<TestContext>.ParentsByIdQuery(context, ready), ct);
        parentPages.ShouldBeLessThan(MaxPages, parentDetail);
    }

    // The same check in the state the message-routing benchmark reaches: the table was analyzed while it
    // was nearly empty, then a burst of messages ran, so thousands of finished jobs are in the table and the
    // statistics know of none. As one statement (parents by id, kind and state, NOT EXISTS a pending child
    // with kind = Job, EXISTS a child with kind = Job in a settled state) PostgreSQL then found the settled
    // child through (kind, state, create_time), every finished job in the table once per named parent:
    // 28,608 pages for 26 named parents on the benchmark, on almost every job completion. The statements
    // below name the parent ids and nothing else, so only the parent index and the primary key can serve them.
    [TimedFact]
    public async Task HintedReadiness_BurstOfFinishedJobsTheStatisticsNeverSaw_ReadsOnlyTheNamedParents()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        await using var context = _fixture.CreateContext();
        var postgres = context.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
        var entity = context.Model.FindEntityType(typeof(Job))!;
        var table = "\"" + entity.GetSchema() + "\".\"" + entity.GetTableName() + "\"";

        // None of these takes parameters; the identifiers come from the model.
        var keepStale = "ALTER TABLE " + table + " SET (autovacuum_enabled = false)";
        var analyze = "ANALYZE " + table;
        var restore = "ALTER TABLE " + table + " RESET (autovacuum_enabled)";

        try
        {
            if (postgres)
            {
                // Statistics of a nearly empty table, kept that way: autovacuum would analyze it in its own time.
                context.Set<Job>().AddRange(Enumerable.Range(0, 20).Select(_ => Row(JobKind.Job, State.Enqueued, null, now)));
                await context.SaveChangesAsync(ct);
                await context.Database.ExecuteSqlRawAsync(keepStale, ct);
                await context.Database.ExecuteSqlRawAsync(analyze, ct);
                context.ChangeTracker.Clear();
            }

            // 1,000 messages of three handlers each. The first 990 have run; the last ten are in flight with
            // two children done and one running, except five of them whose third child finishes below.
            var messages = Enumerable.Range(0, 1000).Select(_ => Row(JobKind.Message, State.Processing, null, now)).ToList();
            var inFlight = messages.Skip(990).ToList();
            await using (var seed = _fixture.CreateContext())
            {
                seed.Set<Job>().AddRange(messages);
                seed.Set<Job>().AddRange(messages.SelectMany(x => Enumerable.Range(0, 3).Select(_ => Row(JobKind.Job, State.Processing, x.Id, now))));
                await seed.SaveChangesAsync(ct);

                // Every transition is an update, as on a running system: it leaves the old index entries behind.
                var done = messages.Take(990).Select(x => x.Id).ToList();
                await seed.Set<Job>()
                    .Where(x => x.ParentJobId != null && done.Contains(x.ParentJobId.Value))
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.CurrentState, State.Completed), ct);
                await seed.Set<Job>()
                    .Where(x => done.Contains(x.Id))
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.CurrentState, State.Completed), ct);

                var running = await seed.Set<Job>()
                    .Where(x => x.ParentJobId != null && inFlight.Select(y => y.Id).Contains(x.ParentJobId.Value))
                    .Select(x => new { x.Id, ParentId = x.ParentJobId!.Value })
                    .ToListAsync(ct);
                var readyParents = inFlight.Take(5).Select(x => x.Id).ToHashSet();
                var finished = running
                    .GroupBy(x => x.ParentId)
                    .SelectMany(x => readyParents.Contains(x.Key) ? x : x.Take(2))
                    .Select(x => x.Id)
                    .ToList();
                await seed.Set<Job>()
                    .Where(x => finished.Contains(x.Id))
                    .ExecuteUpdateAsync(x => x.SetProperty(y => y.CurrentState, State.Completed), ct);
            }

            var named = inFlight.Select(x => x.Id).ToArray();
            var childStates = Orchestrator<TestContext>.ChildStatesOfQuery(context, named);
            var ready = Orchestrator<TestContext>.ParentsWithSettledChildren(await childStates.ToListAsync(ct));

            ready.Order().ShouldBe(inFlight.Take(5).Select(x => x.Id).Order(), "five named parents have every child done, five still have one running");
            var (childPages, childDetail) = await MeasurePagesReadAsync(context, childStates, ct);
            childPages.ShouldBeLessThan(MaxPages, childDetail);
            var (parentPages, parentDetail) = await MeasurePagesReadAsync(context, Orchestrator<TestContext>.ParentsByIdQuery(context, ready), ct);
            parentPages.ShouldBeLessThan(MaxPages, parentDetail);
        }
        finally
        {
            if (postgres)
            {
                await context.Database.ExecuteSqlRawAsync(restore, CancellationToken.None);
            }
        }
    }

    // "Which of these jobs is awaited?", asked while thousands of rows are awaiting: the cost follows the
    // two jobs asked about, not the awaiting rows.
    [TimedFact]
    public async Task AwaitedParentIdsAmongQuery_ThousandsOfAwaitingRows_ReadsOnlyTheNamedJobs()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        Job Row(State state, Guid? parent) =>
            new()
            {
                Id = Guid.NewGuid(),
                Kind = JobKind.Job,
                CurrentState = state,
                ParentJobId = parent,
                CreateTime = now,
                ScheduleTime = now,
                Queue = "default",
            };

        var parents = Enumerable.Range(0, RowsThatLeftProcessing).Select(_ => Row(State.Processing, null)).ToList();
        var notAwaited = Row(State.Completed, null);
        await using (var seed = _fixture.CreateContext())
        {
            seed.Set<Job>().AddRange(parents);
            seed.Set<Job>().Add(notAwaited);
            seed.Set<Job>().AddRange(parents.Select(x => Row(State.Awaiting, x.Id)));
            await seed.SaveChangesAsync(ct);
        }

        Guid[] named = [parents[0].Id, notAwaited.Id];
        await using var context = _fixture.CreateContext();
        var query = Orchestrator<TestContext>.AwaitedParentIdsAmongQuery(context, named);

        (await query.ToListAsync(ct)).ShouldBe([parents[0].Id]);
        var (pages, detail) = await MeasurePagesReadAsync(context, query, ct);
        pages.ShouldBeLessThan(MaxPages, detail);
    }

    // A message-heavy table: every routed child keeps its ParentJobId after it completes, and completed rows
    // stay for a day. The awaiting-rows read runs on every Orchestrator pass and must not follow them.
    [TimedFact(30_000)]
    public async Task AwaitedParentIdsQuery_ManyCompletedChildrenOfMessages_DoesNotReadThem()
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
            for (var i = 0; i < 200; i++)
            {
                var message = Row(JobKind.Message, State.Completed, null);
                seed.Set<Job>().Add(message);
                seed.Set<Job>().AddRange(Enumerable.Range(0, 100).Select(_ => Row(JobKind.Job, State.Completed, message.Id)));
            }

            var parent = Row(JobKind.Job, State.Processing, null);
            seed.Set<Job>().Add(parent);
            seed.Set<Job>().AddRange(Enumerable.Range(0, 5).Select(_ => Row(JobKind.Job, State.Awaiting, parent.Id)));
            await seed.SaveChangesAsync(ct);
        }

        await using var context = _fixture.CreateContext();
        var (pages, detail) = await MeasurePagesReadAsync(context, Orchestrator<TestContext>.AwaitedParentIdsQuery(context), ct);

        pages.ShouldBeLessThan(MaxPages, detail);
    }

    private static Job Row(JobKind kind, State state, Guid? parent, DateTime now) =>
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
