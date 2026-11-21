using Shouldly;
using Warp.Core.Buffering;

namespace Warp.Tests.Core;

// DrainQueue + DrainService are the one queue-and-drain model behind the adapter, endpoint and
// client-event recorders and the enqueue-notification relay, so the shutdown-drain contract is tested
// once here instead of per pipeline. Plain ints: the drain does not care what it carries.
[Trait("Category", "NoDb")]
public class DrainServiceTests
{
    [TimedFact]
    public async Task DrainRemaining_FlushHangs_ReturnsWithinBudget()
    {
        // A flush that honours its token but otherwise never completes: the unreachable-database shape.
        // The budget must bound the in-flight flush, not only the check between batches.
        var queue = new DrainQueue<int>(16);
        queue.TryAdd(1).ShouldBeTrue();
        queue.Complete();

        var drain = DrainService<int>.DrainRemainingAsync(
            queue.Reader,
            (_, ct) => Task.Delay(Timeout.Infinite, ct),
            TimeSpan.FromMilliseconds(250),
            batchSize: 10);

        await drain.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
    }

    [TimedFact]
    public async Task DrainRemaining_SmallBatchSize_FlushesEveryItemInBatches()
    {
        var queue = new DrainQueue<int>(16);
        queue.TryAdd(1).ShouldBeTrue();
        queue.TryAdd(2).ShouldBeTrue();
        queue.TryAdd(3).ShouldBeTrue();
        queue.Complete();
        var batches = new List<List<int>>();

        await DrainService<int>.DrainRemainingAsync(
            queue.Reader,
            (batch, _) =>
            {
                batches.Add(batch);

                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            batchSize: 2);

        batches.Select(x => x.Count).ShouldBe([2, 1]);
        batches.SelectMany(x => x).ShouldBe([1, 2, 3]);
    }

    [TimedFact]
    public void TryAdd_QueueFull_ReturnsFalse()
    {
        var queue = new DrainQueue<int>(1);

        queue.TryAdd(1).ShouldBeTrue();
        queue.TryAdd(2).ShouldBeFalse();
    }

    [TimedFact]
    public void TryAdd_AfterComplete_ReturnsFalse()
    {
        var queue = new DrainQueue<int>(4);
        queue.Complete();

        queue.TryAdd(1).ShouldBeFalse();
    }
}
