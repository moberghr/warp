using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
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
            Assert.Skip("PostgreSQL planner behaviour; SQL Server's claim is a different statement.");
        }

        var ct = Xunit.TestContext.Current.CancellationToken;
        await SeedBelievedEmptyAsync(JobKind.Job, ct);

        var claimerCtx = _fixture.CreateContext();
        var queries = Warp.Tests.Helpers.TestTasks.QueriesFor(claimerCtx);

        var claimed = await queries.ClaimEnqueuedJobsAsync(claimerCtx, ["default"], Guid.NewGuid(), DateTime.UtcNow, limit: 1, watermark: null, ct);

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
            Assert.Skip("PostgreSQL planner behaviour; SQL Server's claim is a different statement.");
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

    [TimedFact(30_000)]
    public async Task ClaimEnqueuedJobs_BurstIntoAQueueTheStatisticsSayIsEmpty_UsesTheClaimIndexWithoutSorting()
    {
        // Found on CI: after 50k jobs were published into a queue that the last analyze saw empty
        // (reltuples 200, relpages 17, no Enqueued rows), the planner expected the claim to match one row
        // and chose the dashboard's (kind, current_state, create_time) index: it read all 48,020 Enqueued
        // rows, filtered them by queue and sorted them to return 20 (22.8 ms against 0.72 ms), and every
        // claim did so until an analyze, ~10 s in. A burst into a quiet queue reaches this in production.
        if (await IsSqlServerAsync())
        {
            Assert.Skip("PostgreSQL planner behaviour; SQL Server's claim is a different statement.");
        }

        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var seed = _fixture.CreateContext();
        var entity = seed.Model.FindEntityType(typeof(Job))!;
        var schema = entity.GetSchema() ?? "public";
        var table = entity.GetTableName()!;
        var claimIndex = entity.GetIndexes()
            .Single(x => x.Properties.Select(y => y.Name).SequenceEqual([nameof(Job.Kind), nameof(Job.CurrentState), nameof(Job.Queue), nameof(Job.ScheduleTime)]))
            .GetDatabaseName()!;

        var now = DateTime.UtcNow;
        seed.Set<Job>().AddRange(Enumerable.Range(0, 200).Select(i => NewJob(State.Completed, now.AddSeconds(-1000 + i))));
        await seed.SaveChangesAsync(ct);

        // ANALYZE takes no parameters; both identifiers come from the model.
        var analyze = "ANALYZE " + Quote(schema) + "." + Quote(table);
        await seed.Database.ExecuteSqlRawAsync(analyze, ct);

        // The burst: one Enqueued job through EF, copied 49,999 times in one statement (fresh ids, ascending
        // schedule times). 50,000 tracked entities took most of the test's budget.
        var template = NewJob(State.Enqueued, now);
        await using (var burst = _fixture.CreateContext())
        {
            burst.Set<Job>().Add(template);
            await burst.SaveChangesAsync(ct);
        }

        var store = StoreObjectIdentifier.Table(table, schema);
        string ColumnOf(string property) => entity.FindProperty(property)!.GetColumnName(store)!;
        var timeColumns = new[] { ColumnOf(nameof(Job.ScheduleTime)), ColumnOf(nameof(Job.CreateTime)) };
        var copied = entity.GetProperties()
            .Select(x => x.GetColumnName(store)!)
            .Select(x =>
                new
                {
                    Name = Quote(x),
                    Value = CopiedValue(x, ColumnOf(nameof(Job.Id)), timeColumns),
                })
            .ToList();
        var copySql = $"INSERT INTO {Quote(schema)}.{Quote(table)} ({string.Join(", ", copied.Select(x => x.Name))}) "
            + $"SELECT {string.Join(", ", copied.Select(x => x.Value))} FROM {Quote(schema)}.{Quote(table)} t, generate_series(1, 49999) g "
            + $"WHERE t.{Quote(ColumnOf(nameof(Job.Id)))} = {{0}}";
        await seed.Database.ExecuteSqlRawAsync(copySql, [template.Id], ct);

        // The statistics CI captured: what the last analyze saw, not the burst.
        await seed.Database.ExecuteSqlRawAsync(
            "SELECT pg_restore_relation_stats('schemaname', {0}::text, 'relname', {1}::text, 'relpages', 17, 'reltuples', 200::real, 'relallvisible', 17)",
            [schema, table],
            ct);

        // Act: the real claim, whose SQL and parameters are captured to EXPLAIN it.
        await using var claimer = _fixture.CreateContext();
        using var capture = new ClaimCommandCapture(claimer);
        await Warp.Tests.Helpers.TestTasks.QueriesFor(claimer)
            .ClaimEnqueuedJobsAsync(claimer, ["default"], Guid.NewGuid(), DateTime.UtcNow, limit: 20, watermark: null, ct);
        var plan = await capture.ExplainAsync(claimer, ct);

        // Assert
        plan.ShouldContain(claimIndex, Case.Sensitive, "the claim did not use the claim index");
        plan.ShouldNotContain(SortNode, Case.Sensitive, "the claim sorted the queue instead of reading it in order");
    }

    [TimedFact]
    public async Task SetLocalInAMultiStatementCommand_LastsForThatCommandOnly()
    {
        // The claim guard (rule 6.21) relies on this: a transaction-local setting (set_config(..., true))
        // sent in the same command as the claim takes effect for it (the command's statements run as one
        // implicit transaction) and is gone for the connection's next command, so it never leaks into
        // pooled connections.
        if (await IsSqlServerAsync())
        {
            Assert.Skip("PostgreSQL planner behaviour; SQL Server's claim is a different statement.");
        }

        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var context = _fixture.CreateContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(ct);

        await using var within = connection.CreateCommand();
        within.CommandText = "DO $$BEGIN PERFORM set_config('enable_sort', 'off', true); END$$; SHOW enable_sort;";
        var inside = (string?)await within.ExecuteScalarAsync(ct);

        await using var after = connection.CreateCommand();
        after.CommandText = "SHOW enable_sort;";
        var afterwards = (string?)await after.ExecuteScalarAsync(ct);

        inside.ShouldBe("off");
        afterwards.ShouldBe("on");
    }

    [TimedFact]
    public async Task ClaimEnqueuedJobs_EmptyQueue_RaisesNoServerNotice()
    {
        // The claim guard sets a value for the claim's command only. Outside a transaction block PostgreSQL
        // answers SET LOCAL with a WARNING; sent on every claim, empty polls included, that would put a line
        // in the server log per claim. The claim must raise no notice at all.
        if (await IsSqlServerAsync())
        {
            Assert.Skip("PostgreSQL planner behaviour; SQL Server's claim is a different statement.");
        }

        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var claimer = _fixture.CreateContext();
        await claimer.Database.OpenConnectionAsync(ct);
        var connection = (Npgsql.NpgsqlConnection)claimer.Database.GetDbConnection();
        var notices = new List<string>();
        connection.Notice += (_, e) => notices.Add($"{e.Notice.Severity}: {e.Notice.MessageText}");

        // Act
        await Warp.Tests.Helpers.TestTasks.QueriesFor(claimer)
            .ClaimEnqueuedJobsAsync(claimer, ["default"], Guid.NewGuid(), DateTime.UtcNow, limit: 20, watermark: null, ct);

        // Assert
        notices.ShouldBeEmpty();
    }

    private const string SortNode = "\"Node Type\": \"Sort\"";

    private static string Quote(string identifier) => "\"" + identifier + "\"";

    // A copy of the template row: a fresh id, times moved on by the copy's number in milliseconds.
    private static string CopiedValue(string column, string idColumn, string[] timeColumns)
    {
        if (string.Equals(column, idColumn, StringComparison.Ordinal))
        {
            return "gen_random_uuid()";
        }

        return timeColumns.Contains(column, StringComparer.Ordinal)
            ? $"t.{Quote(column)} + g * interval '1 millisecond'"
            : $"t.{Quote(column)}";
    }

    private static Job NewJob(State state, DateTime scheduleTime) => new()
    {
        Id = Guid.NewGuid(),
        Kind = JobKind.Job,
        CurrentState = state,
        Type = typeof(UnitRequest).AssemblyQualifiedName,
        Message = "{}",
        CreateTime = scheduleTime,
        ScheduleTime = scheduleTime,
        Queue = "default",
    };

    // Captures the claim statement a context executes (text and parameters), to EXPLAIN it afterwards
    // without copying the provider's SQL into the test.
    private sealed class ClaimCommandCapture : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly DbContext _context;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _all;
        private string? _sql;
        private List<(string Name, object? Value)> _parameters = [];

        public ClaimCommandCapture(DbContext context)
        {
            _context = context;
            _all = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public async Task<string> ExplainAsync(DbContext context, CancellationToken ct)
        {
            _sql.ShouldNotBeNull("the claim statement was not captured");
            var connection = context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(ct);
            }

            // Replays what the claim sent, in one transaction: any setting statements before the claim
            // run as sent (they last for the claim's command), and the claim statement is EXPLAINed.
            // Split at the end of a line: the guard is a DO block with semicolons of its own.
            var statements = _sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            string? plan = null;
            foreach (var statement in statements)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                if (!statement.Contains("SKIP LOCKED", StringComparison.Ordinal))
                {
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync(ct);

                    continue;
                }

                command.CommandText = "EXPLAIN (FORMAT JSON) " + statement;
                foreach (var (name, value) in _parameters)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }

                plan = (string)(await command.ExecuteScalarAsync(ct))!;
            }

            await transaction.RollbackAsync(ct);

            return plan.ShouldNotBeNull("no claim statement in the captured command");
        }

        public void OnNext(DiagnosticListener value)
        {
            if (string.Equals(value.Name, DbLoggerCategory.Name, StringComparison.Ordinal))
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(value.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (string.Equals(value.Key, RelationalEventId.CommandExecuting.Name, StringComparison.Ordinal)
                && value.Value is CommandEventData data
                && ReferenceEquals(data.Context, _context)
                && data.Command.CommandText.Contains("SKIP LOCKED", StringComparison.Ordinal))
            {
                _sql = data.Command.CommandText;
                _parameters = [.. data.Command.Parameters.Cast<System.Data.Common.DbParameter>().Select(x => (x.ParameterName, x.Value))];
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            _all.Dispose();
            lock (_subscriptions)
            {
                foreach (var subscription in _subscriptions)
                {
                    subscription.Dispose();
                }
            }
        }
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
