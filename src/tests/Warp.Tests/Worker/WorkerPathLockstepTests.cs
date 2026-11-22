using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.Observability;
using Warp.Core.Timeout;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Worker;

// The single worker (WarpWorkerService) and the dispatcher worker (WarpDispatcherWorker + CompletionBatch)
// keep separate copies of job finalization, and the two have drifted before: the dispatcher path missed
// the error-grouping inbox, and the single worker left CancellationMode on the row. The dispatcher copy
// has a second, quiet way to drift: CompletionBatch.MarkFinalizedColumns lists the columns it writes, so
// a field assigned at finalization and not added to that list is never persisted, with no error.
//
// Each case runs one job to rest in single-worker mode and again in dispatcher mode, then compares EVERY
// mapped column of the row, read from the EF model so a column added later is compared without touching
// this test. Values that differ between any two runs (ids, instants) are reduced to their shape.
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract partial class WorkerPathLockstepTestsBase : IntegrationTestBase
{
    protected WorkerPathLockstepTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    public enum Scenario
    {
        Completed = 1,
        FailedAfterRetry = 2,
        Cancelled = 3,
        ParkedByOutcome = 4,
        CompletedWithOtelMetricsSink = 5,
        DeadlineMet = 6,
        DeadlineMissed = 7,
        RequeuedOnceByOutcome = 8,
        DeletedByOutcome = 9,
    }

    // Two full hosts run one after the other, and the failing case waits out a retry delay in each.
    [TimedTheory(30_000)]
    [InlineData(Scenario.Completed)]
    [InlineData(Scenario.FailedAfterRetry)]
    [InlineData(Scenario.Cancelled)]
    [InlineData(Scenario.ParkedByOutcome)]
    [InlineData(Scenario.CompletedWithOtelMetricsSink)]
    [InlineData(Scenario.DeadlineMet)]
    [InlineData(Scenario.DeadlineMissed)]
    [InlineData(Scenario.RequeuedOnceByOutcome)]
    [InlineData(Scenario.DeletedByOutcome)]
    public async Task FinalizedRow_IsTheSameInSingleWorkerAndDispatcherMode(Scenario scenario)
    {
        var single = await RunToRestAsync(scenario, useDispatcher: false);
        var dispatcher = await RunToRestAsync(scenario, useDispatcher: true);

        var differences = single.Keys
            .Union(dispatcher.Keys, StringComparer.Ordinal)
            .Where(x => !string.Equals(single.GetValueOrDefault(x), dispatcher.GetValueOrDefault(x), StringComparison.Ordinal))
            .Select(x => $"{x}: single worker = {single.GetValueOrDefault(x)}, dispatcher = {dispatcher.GetValueOrDefault(x)}")
            .ToList();

        differences.ShouldBeEmpty();

        // Two rows that agree because neither path wrote anything would pass the comparison, so the
        // cases that exist for a counter also check that it is there.
        var counted = single.Keys.Where(x => x.StartsWith("counter ", StringComparison.Ordinal)).ToList();
        switch (scenario)
        {
            case Scenario.CompletedWithOtelMetricsSink:
                counted.ShouldNotContain(x => x.StartsWith("counter jobstat", StringComparison.Ordinal));
                counted.ShouldNotContain(x => x.StartsWith("counter qwait", StringComparison.Ordinal));
                break;

            case Scenario.DeadlineMet:
                counted.ShouldContain(x => x.StartsWith("counter deadline:", StringComparison.Ordinal) && x.EndsWith(":count", StringComparison.Ordinal));
                counted.ShouldNotContain(x => x.StartsWith("counter deadline:", StringComparison.Ordinal) && x.EndsWith(":miss", StringComparison.Ordinal));
                break;

            case Scenario.DeadlineMissed:
                counted.ShouldContain(x => x.StartsWith("counter deadline:", StringComparison.Ordinal) && x.EndsWith(":miss", StringComparison.Ordinal));
                break;

            case Scenario.RequeuedOnceByOutcome:
                single["counter stats:requeued"].ShouldBe("1");
                single["counter stats:requeued-concurrency"].ShouldBe("1");
                single["counter stats:succeeded"].ShouldBe("1");
                break;

            case Scenario.DeletedByOutcome:
                single["counter stats:deleted"].ShouldBe("1");
                single["counter stats:deleted-ratelimit"].ShouldBe("1");
                break;

            default:
                counted.ShouldContain(x => x.StartsWith("counter stats:", StringComparison.Ordinal));
                break;
        }
    }

    private async Task<Dictionary<string, string>> RunToRestAsync(Scenario scenario, bool useDispatcher)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Fixture.ResetAsync();
        Guid jobId;

        // The host is stopped before anything is read: a graceful stop flushes the in-memory counter
        // buffer, so the counters below are complete rather than whatever a timer had written so far.
        await using (var server = await WarpTestServer.StartAsync(
            Fixture,
            cfg =>
            {
                cfg.UseDispatcher = useDispatcher;
                cfg.WorkerCount = 2;
                cfg.CompletionBatchSize = 10;
                cfg.CompletionFlushInterval = TimeSpan.FromMilliseconds(50);
                cfg.AddTimeout();
                if (scenario == Scenario.CompletedWithOtelMetricsSink)
                {
                    cfg.JobMetricsSink = RecordingSink.Otel;
                }
            }))
        {
            jobId = await RunScenarioAsync(server, scenario, ct);
        }

        await using var ctx = Fixture.CreateContext();
        var job = await ctx.Set<Job>()
            .AsNoTracking()
            .Where(x => x.Id == jobId)
            .SingleAsync(ct);

        var future = DateTime.UtcNow.AddHours(1);
        var row = ctx.Entry(job).Properties.ToDictionary(
            x => x.Metadata.Name,
            x => Shape(x.Metadata.Name, x.CurrentValue, future),
            StringComparer.Ordinal);

        // The logs each path wrote for the job, as a sorted list of event types.
        var events = await ctx.Set<JobLog>()
            .AsNoTracking()
            .Where(x => x.JobId == jobId)
            .Select(x => x.EventType)
            .ToListAsync(ct);
        row["(job log events)"] = string.Join(", ", events.Order(StringComparer.Ordinal));

        // What each path counted. Pending Counter rows and folded Statistic rows are one total.
        var counters = await ctx.Set<Counter>()
            .AsNoTracking()
            .Select(x => new { x.Key, Value = (long)x.Value })
            .ToListAsync(ct);
        var statistics = await ctx.Set<Statistic>()
            .AsNoTracking()
            .Select(x => new { x.Key, x.Value })
            .ToListAsync(ct);
        var totals = counters
            .Concat(statistics)
            .Where(x => IsComparableCounter(x.Key))
            .GroupBy(x => BucketStamp().Replace(x.Key, "(bucket)"), StringComparer.Ordinal);
        foreach (var total in totals)
        {
            row[$"counter {total.Key}"] = total.Sum(x => x.Value).ToString(CultureInfo.InvariantCulture);
        }

        return row;
    }

    private static async Task<Guid> RunScenarioAsync(WarpTestServer server, Scenario scenario, CancellationToken ct)
    {
        var publisher = server.CreatePublisher();
        Guid jobId;
        switch (scenario)
        {
            case Scenario.Completed:
                jobId = await publisher.Enqueue(new UnitRequest());
                await publisher.SaveChangesAsync(ct);
                await server.WaitForJobState(jobId, State.Completed);
                break;

            case Scenario.FailedAfterRetry:
                jobId = await publisher.Enqueue(new ThrowExceptionRequest());
                await publisher.SaveChangesAsync(ct);
                await server.WaitForJobState(jobId, State.Failed);
                break;

            case Scenario.Cancelled:
                jobId = await publisher.Enqueue(new CancellableRequest());
                await publisher.SaveChangesAsync(ct);
                await server.WaitForJobState(jobId, State.Processing);
                await server.CreateCommandService().DeleteJob(jobId);
                await server.WaitForJobLog(jobId, "Cancelled");
                break;

            case Scenario.ParkedByOutcome:
                jobId = await publisher.Enqueue(new ParkInFutureRequest());
                await publisher.SaveChangesAsync(ct);
                await server.WaitForJobState(jobId, State.Scheduled);
                break;

            case Scenario.CompletedWithOtelMetricsSink:
                jobId = await publisher.Enqueue(new UnitRequest());
                await publisher.SaveChangesAsync(ct);
                await server.WaitForJobState(jobId, State.Completed);
                break;

            case Scenario.DeadlineMet:
                jobId = await publisher.Enqueue(new TimeoutTotalScopeRequest());
                await publisher.SaveChangesAsync(ct);
                await server.WaitForJobState(jobId, State.Completed);
                break;

            case Scenario.DeadlineMissed:
                // One second is the shortest budget there is (it is stored in whole seconds). The handler
                // runs for thirty, so the timeout ends it at the deadline and the job is finalized as
                // Deleted at or past it: a miss.
                var budget = new JobParameters().WithTimeout(TimeSpan.FromSeconds(1), TimeoutMode.Delete, TimeoutScope.Total);
                jobId = await publisher.Enqueue(new CancellableRequest(), budget);
                await publisher.SaveChangesAsync(ct);
                await server.WaitForJobState(jobId, State.Deleted);
                break;

            case Scenario.RequeuedOnceByOutcome:
                // The requeue a Wait-mode mutex, semaphore or rate limit asks for, then a normal run.
                jobId = await publisher.Enqueue(new RequeueOnceWithReasonRequest());
                await publisher.SaveChangesAsync(ct);
                await server.WaitForJobState(jobId, State.Completed);
                break;

            case Scenario.DeletedByOutcome:
                // The deletion a Skip-mode limit or a Delete-mode timeout asks for.
                jobId = await publisher.Enqueue(new DeleteByOutcomeRequest());
                await publisher.SaveChangesAsync(ct);
                await server.WaitForJobState(jobId, State.Deleted);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario");
        }

        return jobId;
    }

    // Latency histogram buckets and summed durations depend on how long this run took (a zero sum writes
    // no row at all), and the backlog gauge on when the sampler ticked: none is a property of the path.
    // The count beside each duration is compared.
    private static bool IsComparableCounter(string key) =>
        !key.Contains(":pct:", StringComparison.Ordinal)
        && !key.Contains(":dur", StringComparison.Ordinal)
        && !key.Contains(":pcth:", StringComparison.Ordinal)
        && !key.StartsWith("qbacklog", StringComparison.Ordinal)
        && !key.StartsWith("warpsys", StringComparison.Ordinal);

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}(-\d{2}){0,2}", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BucketStamp();

    // What a value looks like once everything that legitimately differs between two runs is taken out:
    // an id is "an id", an instant is "past" or "future", and metadata is its keys with their plain values.
    private static string Shape(string property, object? value, DateTime future) => value switch
    {
        null => "null",
        Guid => "(an id)",
        DateTime instant => instant > future ? "(future)" : "(past or now)",
        string metadata when string.Equals(property, nameof(Job.Metadata), StringComparison.Ordinal) => MetadataShape(metadata),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
    };

    private static string MetadataShape(string metadata)
    {
        var pairs = MetadataSerializer.Deserialize(metadata)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key}={PlainValue(x.Value)}");

        return string.Join(", ", pairs);
    }

    private static string PlainValue(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        if (Guid.TryParse(text, out _))
        {
            return "(an id)";
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) && text.Length >= 10
            ? "(an instant)"
            : text;
    }
}
