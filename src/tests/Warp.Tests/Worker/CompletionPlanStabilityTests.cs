using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Worker;

// Rot guard for the batched completion's log insert on SQL Server (rule 6.13). Found on CI: written as
// WHERE l.j IN (SELECT id FROM @done), the optimizer was free to put @done on the outer side of the join
// and OPENJSON(@logs) on the inner side, and one cached plan did so without a spool: the whole log document
// was parsed again for every completed job, ~85,000 pages per 60-job flush, and dispatcher mode fell from
// ~540 to ~120 jobs/s. Which plan was cached depended on the estimates at compile time, so it cannot be
// reproduced on demand; what can be checked is the order the statement now pins.
[GenerateDatabaseTests]
public abstract class CompletionPlanStabilityTestsBase : IAsyncLifetime
{
    private const string ShowplanNamespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private readonly IDatabaseFixture _fixture;

    protected CompletionPlanStabilityTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task CompleteJobs_LogInsert_ParsesTheLogDocumentOnTheOuterSideOfTheJoin()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var context = _fixture.CreateContext();
        if (context.Database.ProviderName?.Contains("SqlServer", StringComparison.Ordinal) != true)
        {
            Assert.Skip("SQL Server plan shape; PostgreSQL's completion is a different statement.");
        }

        // Arrange
        var jobs = Enumerable.Range(0, 3).Select(_ => NewJob()).ToList();
        await using (var insert = _fixture.CreateContext())
        {
            insert.Set<Job>().AddRange(jobs);
            await insert.SaveChangesAsync(ct);
        }

        var owners = jobs.ConvertAll(x => JobRunOwner.Of(x, Guid.NewGuid()));
        foreach (var job in jobs)
        {
            job.CurrentState = State.Completed;
            job.CurrentWorkerId = null;
            job.LastKeepAlive = null;
        }

        // Act: the real completion, its command captured (and rolled back) to replay it under STATISTICS XML.
        // Only this context's connection is captured (the listener is process-wide and other classes run
        // completions in parallel), and the capture ends before the replay.
        await context.Database.OpenConnectionAsync(ct);
        var capture = new CompletionCommandCapture(context.Database.GetDbConnection());
        using (capture)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            await TestTasks.QueriesFor(context).CompleteJobsAsync(context, jobs, owners, [.. jobs.SelectMany(x => Logs(x.Id))], ct);
            await transaction.RollbackAsync(ct);
        }

        var plans = await capture.ActualPlansAsync(_fixture.CreateContext(), ct);

        // Assert
        var insertPlan = plans
            .SelectMany(x => x.Descendants(XName.Get("StmtSimple", ShowplanNamespace)))
            .Single(x => string.Equals((string?)x.Attribute("StatementType"), "INSERT", StringComparison.Ordinal));
        var join = insertPlan
            .Descendants(XName.Get("RelOp", ShowplanNamespace))
            .First(x => string.Equals((string?)x.Attribute("PhysicalOp"), "Nested Loops", StringComparison.Ordinal));
        var outer = join
            .Descendants(XName.Get("RelOp", ShowplanNamespace))
            .First();
        outer.DescendantsAndSelf(XName.Get("RelOp", ShowplanNamespace))
            .ShouldContain(x => string.Equals((string?)x.Attribute("PhysicalOp"), "Table-valued function", StringComparison.Ordinal), insertPlan.ToString());
    }

    private static Job NewJob() => new()
    {
        Id = Guid.NewGuid(),
        Kind = JobKind.Job,
        CurrentState = State.Processing,
        Type = "test",
        Message = "{}",
        CreateTime = DateTime.UtcNow,
        ScheduleTime = DateTime.UtcNow,
        Queue = "default",
    };

    private static IEnumerable<JobLog> Logs(Guid jobId) =>
    [
        new JobLog { JobId = jobId, EventType = "Processing", Timestamp = DateTime.UtcNow, Level = "Information", Message = "processing" },
        new JobLog { JobId = jobId, EventType = "Completed", Timestamp = DateTime.UtcNow, Level = "Information", Message = "completed" },
    ];

    // Captures the completion batch SqlClient sends (text and parameter values), to replay it under
    // STATISTICS XML in a transaction that is rolled back, without copying the provider's SQL here.
    private sealed class CompletionCommandCapture : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly System.Data.Common.DbConnection _connection;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _all;
        private string? _sql;
        private List<(string Name, object? Value)> _parameters = [];

        public CompletionCommandCapture(System.Data.Common.DbConnection connection)
        {
            _connection = connection;
            _all = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public async Task<List<XDocument>> ActualPlansAsync(TestContext context, CancellationToken ct)
        {
            _sql.ShouldNotBeNull("the completion command was not captured");
            await context.Database.OpenConnectionAsync(ct);
            var connection = (SqlConnection)context.Database.GetDbConnection();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SET STATISTICS XML ON; " + _sql;
            foreach (var (name, value) in _parameters)
            {
                command.Parameters.Add(new SqlParameter(name, value ?? DBNull.Value));
            }

            var plans = new List<XDocument>();
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                do
                {
                    var isPlan = reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.Ordinal);
                    while (await reader.ReadAsync(ct))
                    {
                        if (isPlan)
                        {
                            plans.Add(XDocument.Parse(reader.GetString(0)));
                        }
                    }
                }
                while (await reader.NextResultAsync(ct));
            }

            await transaction.RollbackAsync(ct);

            return plans;
        }

        public void OnNext(DiagnosticListener value)
        {
            if (string.Equals(value.Name, "SqlClientDiagnosticListener", StringComparison.Ordinal))
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(value.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (!value.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal)
                || value.Value?.GetType().GetProperty("Command")?.GetValue(value.Value) is not SqlCommand command
                || !ReferenceEquals(command.Connection, _connection)
                || !command.CommandText.Contains("OPENJSON(@logs)", StringComparison.Ordinal))
            {
                return;
            }

            _sql = command.CommandText;
            _parameters = [.. command.Parameters.Cast<SqlParameter>().Select(x => (x.ParameterName, x.Value))];
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
}
