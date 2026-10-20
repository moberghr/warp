using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core.Logging;

namespace Warp.Tests.Observability;

[Trait("Category", "NoDb")]
public class JobLogCollectorTests
{
    [TimedFact]
    public void Drain_ReturnsAllEntries_AndClearsQueue()
    {
        var collector = new JobLogCollector { JobId = Guid.NewGuid() };
        collector.Add("Information", "msg1");
        collector.Add("Warning", "msg2");

        var entries = collector.Drain();

        entries.Count.ShouldBe(2);
        entries[0].Message.ShouldBe("msg1");
        entries[1].Message.ShouldBe("msg2");

        // Second drain should be empty
        collector.Drain().Count.ShouldBe(0);
    }

    [TimedFact]
    public void Add_WithinOneClockTick_StampsStrictlyIncreasingMicroseconds()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        var collector = new JobLogCollector { JobId = Guid.NewGuid(), TimeProvider = clock };
        collector.Add("Information", "first");
        collector.Add("Information", "second");
        collector.Add("Information", "third");

        var entries = collector.Drain();

        entries.Select(x => x.Timestamp).ShouldBe(
        [
            clock.GetUtcNow().UtcDateTime,
            clock.GetUtcNow().UtcDateTime.AddMicroseconds(1),
            clock.GetUtcNow().UtcDateTime.AddMicroseconds(2),
        ]);
    }

    [TimedFact]
    public void Add_ClockAheadOfLastStamp_UsesTheClock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        var collector = new JobLogCollector { JobId = Guid.NewGuid(), TimeProvider = clock };
        collector.Add("Information", "first");
        clock.Advance(TimeSpan.FromSeconds(1));
        collector.Add("Information", "second");

        var entries = collector.Drain();

        entries[1].Timestamp.ShouldBe(clock.GetUtcNow().UtcDateTime);
    }

    [TimedFact]
    public void Drain_ReturnsEmptyList_WhenQueueIsEmpty()
    {
        var collector = new JobLogCollector { JobId = Guid.NewGuid() };

        var entries = collector.Drain();

        entries.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Drain_DoesNotLoseEntries_WhenAddAndDrainConcurrently()
    {
        var collector = new JobLogCollector { JobId = Guid.NewGuid() };
        const int totalEntries = 1000;
        var allDrained = new List<Warp.Core.Data.Entities.JobLog>();

        // Producer: add entries from multiple threads
        var producerTask = Task.Run(
            async () =>
            {
                var tasks = Enumerable.Range(0, totalEntries).Select(i =>
                    Task.Run(() => collector.Add("Information", $"msg-{i}"), Xunit.TestContext.Current.CancellationToken));
                await Task.WhenAll(tasks);
            },
            Xunit.TestContext.Current.CancellationToken);

        // Consumer: drain periodically while producer is running
        var producerDone = false;
        var consumerTask = Task.Run(
            async () =>
            {
                while (!producerDone)
                {
                    allDrained.AddRange(collector.Drain());
                    await Task.Delay(1, Xunit.TestContext.Current.CancellationToken);
                }

                // Final drain to catch any remaining entries
                allDrained.AddRange(collector.Drain());
            },
            Xunit.TestContext.Current.CancellationToken);

        await producerTask;
        producerDone = true;
        await consumerTask;

        // All entries should be accounted for — none lost
        allDrained.Count.ShouldBe(totalEntries);
    }
}
