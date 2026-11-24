using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Shouldly;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Worker;

[GenerateDatabaseTests]
public abstract class DispatcherDiagnosticsTestsBase : IntegrationTestBase
{
    protected DispatcherDiagnosticsTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenAListener_WhenADispatcherRunsJobs_ThenEveryTimingIsRecorded()
    {
        // Arrange — one worker and no prefetch, so the group is full while each job runs and the
        // second job's claim waits for the slot the first one frees: every instrument has something
        // to record. The meter is process-wide, so other tests may add measurements too; each
        // instrument is only required to have recorded something.
        var recorded = new ConcurrentDictionary<string, int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (string.Equals(instrument.Meter.Name, DispatcherDiagnostics.MeterName, StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => recorded.AddOrUpdate(instrument.Name, 1, (_, x) => x + 1));
        listener.SetMeasurementEventCallback<int>((instrument, _, _, _) => recorded.AddOrUpdate(instrument.Name, 1, (_, x) => x + 1));
        listener.Start();

        await using var server = await WarpTestServer.StartAsync(Fixture, config =>
        {
            config.UseDispatcher = true;
            config.WorkerCount = 1;
            config.PrefetchCount = 0;
            config.CompletionFlushInterval = TimeSpan.FromMilliseconds(50);
        });
        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new UnitRequest());
        await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await server.WaitForCompletion(TimeSpan.FromSeconds(8));

        // Assert
        recorded.Keys.ShouldBe(
            [
                "warp.dispatcher.worker.busy",
                "warp.dispatcher.worker.idle",
                "warp.dispatcher.claim.duration",
                "warp.dispatcher.claim.size",
                "warp.dispatcher.claim.free_slot_wait",
                "warp.dispatcher.channel.wait",
                "warp.dispatcher.flush.age",
                "warp.dispatcher.flush.duration",
                "warp.dispatcher.flush.size",
            ],
            ignoreOrder: true);
    }

    [TimedFact]
    public async Task GivenAListenerOnChannelWaitAlone_WhenADispatcherRunsJobs_ThenItIsRecordedAndNothingIsLeftBehind()
    {
        // Arrange — the claim remembered when each job was claimed whenever channel-wait had a listener
        // (and only if claim-duration had one too), but the worker forgot it only when worker-busy had
        // one: a listener on channel-wait alone recorded nothing, or grew the map by a job per claim.
        var waits = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (string.Equals(instrument.Meter.Name, DispatcherDiagnostics.MeterName, StringComparison.Ordinal)
                && string.Equals(instrument.Name, "warp.dispatcher.channel.wait", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref waits));
        listener.Start();

        await using var server = await WarpTestServer.StartAsync(Fixture, config =>
        {
            config.UseDispatcher = true;
            config.WorkerCount = 1;
            config.CompletionFlushInterval = TimeSpan.FromMilliseconds(50);
        });
        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new UnitRequest());
        await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await server.WaitForCompletion(TimeSpan.FromSeconds(8));

        // Assert — the map is process-wide, so only this test's jobs are looked for.
        await using var context = Fixture.CreateContext();
        var jobIds = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            System.Linq.Queryable.Select(context.Set<Warp.Core.Entities.Job>(), x => x.Id),
            Xunit.TestContext.Current.CancellationToken);
        jobIds.Count.ShouldBe(2);
        Volatile.Read(ref waits).ShouldBeGreaterThanOrEqualTo(2);
        jobIds.ShouldAllBe(x => !DispatcherDiagnostics.ClaimedAt.ContainsKey(x));
    }
}
