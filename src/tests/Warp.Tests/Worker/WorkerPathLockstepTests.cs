using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
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
public abstract class WorkerPathLockstepTestsBase : IntegrationTestBase
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
    }

    // Two full hosts run one after the other, and the failing case waits out a retry delay in each.
    [TimedTheory(30_000)]
    [InlineData(Scenario.Completed)]
    [InlineData(Scenario.FailedAfterRetry)]
    [InlineData(Scenario.Cancelled)]
    [InlineData(Scenario.ParkedByOutcome)]
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
    }

    private async Task<Dictionary<string, string>> RunToRestAsync(Scenario scenario, bool useDispatcher)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Fixture.ResetAsync();
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            cfg =>
            {
                cfg.UseDispatcher = useDispatcher;
                cfg.WorkerCount = 2;
                cfg.CompletionBatchSize = 10;
                cfg.CompletionFlushInterval = TimeSpan.FromMilliseconds(50);
            });

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

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario");
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

        return row;
    }

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
