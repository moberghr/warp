using Shouldly;
using Warp.Core.Services;

namespace Warp.Tests.Observability;

/// <summary>
/// NoDb coverage for <see cref="WarpCounterBuffer"/> — the in-memory accumulator the worker sums
/// counter increments into instead of writing a row per increment.
/// <para>
/// The property that matters is conservation: every increment must land in exactly one drained
/// batch. The buffer is written by every worker on the server and drained concurrently by the
/// flusher, so a drain that is not atomic with respect to concurrent adds silently loses or
/// double-counts metrics — and metrics that are quietly wrong are worse than metrics that are
/// missing, because nothing surfaces the error.
/// </para>
/// </summary>
[Trait("Category", "NoDb")]
public class WarpCounterBufferTests
{
    [Fact]
    public void Add_SameKeyManyTimes_SumsIntoOneEntry()
    {
        var buffer = new WarpCounterBuffer();

        for (var i = 0; i < 1_000; i++)
        {
            buffer.Add("stats:succeeded", 1);
        }

        var drained = buffer.Drain();

        drained.ShouldHaveSingleItem();
        drained[0].Key.ShouldBe("stats:succeeded");
        drained[0].Value.ShouldBe(1_000);
    }

    [Fact]
    public void Drain_LeavesBufferEmpty()
    {
        var buffer = new WarpCounterBuffer();
        buffer.Add("a", 5);

        buffer.Drain().ShouldHaveSingleItem();
        buffer.Drain().ShouldBeEmpty();
        buffer.PendingKeyCount.ShouldBe(0);
    }

    [Fact]
    public void Add_NegativeAndPositive_NetsOut()
    {
        var buffer = new WarpCounterBuffer();
        buffer.Add("k", 10);
        buffer.Add("k", -4);

        buffer.Drain().ShouldHaveSingleItem().Value.ShouldBe(6);
    }

    /// <summary>
    /// Writers and a concurrent drainer must conserve the total. This is the case the key-by-key
    /// TryRemove in <see cref="WarpCounterBuffer.Drain"/> exists for: swapping the dictionary instead
    /// lets an increment land in an instance that has already been materialised, and it vanishes with
    /// no trace — measured at 8.4% dropped under eight concurrent writers.
    /// </summary>
    [Fact]
    public async Task AddAndDrain_Concurrently_ConservesEveryIncrement()
    {
        const int Writers = 8;
        const int PerWriter = 25_000;

        var buffer = new WarpCounterBuffer();
        var drained = 0L;
        using var stop = new CancellationTokenSource();

        var drainer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var (_, value) in buffer.Drain())
                {
                    Interlocked.Add(ref drained, value);
                }

                await Task.Yield();
            }
        });

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < PerWriter; i++)
            {
                // Two keys, so the drain is exercised against both a hot key and key churn.
                buffer.Add(i % 2 == 0 ? "even" : "odd", 1);
            }
        })));

        await stop.CancelAsync();
        await drainer;

        // Whatever the drainer did not take is still in the buffer; the two together must be exact.
        foreach (var (_, value) in buffer.Drain())
        {
            Interlocked.Add(ref drained, value);
        }

        drained.ShouldBe(Writers * (long)PerWriter);
    }
}
