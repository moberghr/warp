using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Worker;

[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
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
        // to record. The meter is process-wide, so only the measurements tagged with this server's
        // worker group count.
        var recorded = new ConcurrentDictionary<(string Instrument, string? Group), int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (string.Equals(instrument.Meter.Name, DispatcherDiagnostics.MeterName, StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => recorded.AddOrUpdate((instrument.Name, GroupOf(tags)), 1, (_, x) => x + 1));
        listener.SetMeasurementEventCallback<int>((instrument, _, tags, _) => recorded.AddOrUpdate((instrument.Name, GroupOf(tags)), 1, (_, x) => x + 1));
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
        var group = await GroupOfAsync(server);
        recorded.Keys
            .Where(x => string.Equals(x.Group, group, StringComparison.Ordinal))
            .Select(x => x.Instrument)
            .ShouldBe(
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
        var waits = new ConcurrentBag<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (string.Equals(instrument.Meter.Name, DispatcherDiagnostics.MeterName, StringComparison.Ordinal)
                && string.Equals(instrument.Name, "warp.dispatcher.channel.wait", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => waits.Add(GroupOf(tags)));
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

        // Assert — the meter and the map are process-wide, so only this test's group and jobs count.
        await using var context = Fixture.CreateContext();
        var jobIds = await context.Set<Job>()
            .Select(x => x.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        jobIds.Count.ShouldBe(2);
        var group = await GroupOfAsync(server);
        waits.Count(x => string.Equals(x, group, StringComparison.Ordinal)).ShouldBe(2);
        jobIds.ShouldAllBe(x => !DispatcherDiagnostics.ClaimedAt.ContainsKey(x));
    }

    private static string? GroupOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, DispatcherDiagnostics.GroupTagName, StringComparison.Ordinal))
            {
                return tag.Value as string;
            }
        }

        return null;
    }

    private async Task<string> GroupOfAsync(WarpTestServer server)
    {
        await using var context = Fixture.CreateContext();
        var groupId = await context.Set<WorkerGroup>()
            .Where(x => x.ServerId == server.ServerId)
            .Select(x => x.Id)
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);

        return groupId.ToString();
    }
}
