using System.Threading.Channels;
using Shouldly;
using Warp.Core.Adapters;
using Warp.Core.Enums;

namespace Warp.Tests.Adapters;

/// <summary>
/// Shutdown-drain budget coverage for <see cref="AdapterCallFlusher{TContext}"/>. The doc contract on
/// <c>AdapterFlush.ShutdownDrainBudget</c> promises a slow/unreachable database cannot hang host shutdown
/// past the budget — which requires the budget token to bound the in-flight persist itself, not just the
/// between-batches loop check.
/// </summary>
[Trait("Category", "NoDb")]
public class AdapterFlusherDrainTests
{
    [TimedFact]
    public async Task DrainRemaining_PersistHangs_ReturnsWithinBudget()
    {
        // A flush that honours its token but otherwise never completes — the unreachable-DB shape. With
        // the budget only checked between batches, the drain would hang here for as long as the flush does.
        var channel = Channel.CreateBounded<AdapterCallRecord>(16);
        channel.Writer.TryWrite(MakeRecord()).ShouldBeTrue();
        channel.Writer.Complete();

        var drain = AdapterCallFlusher<Warp.Tests.TestContext>.DrainRemainingAsync(
            channel.Reader,
            (batch, ct) => Task.Delay(Timeout.Infinite, ct),
            budget: TimeSpan.FromMilliseconds(250));

        var completed = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(3)));

        completed.ShouldBe(drain, "the drain must return once its budget elapses, even mid-persist");
        await drain;
    }

    [TimedFact]
    public async Task DrainRemaining_FlushCompletes_DrainsAllBufferedRecords()
    {
        var channel = Channel.CreateBounded<AdapterCallRecord>(16);
        channel.Writer.TryWrite(MakeRecord()).ShouldBeTrue();
        channel.Writer.TryWrite(MakeRecord()).ShouldBeTrue();
        channel.Writer.TryWrite(MakeRecord()).ShouldBeTrue();
        channel.Writer.Complete();

        var flushed = new List<AdapterCallRecord>();

        await AdapterCallFlusher<Warp.Tests.TestContext>.DrainRemainingAsync(
            channel.Reader,
            (batch, _) =>
            {
                flushed.AddRange(batch);

                return Task.CompletedTask;
            },
            budget: TimeSpan.FromSeconds(5));

        flushed.Count.ShouldBe(3);
    }

    private static AdapterCallRecord MakeRecord()
        => new()
        {
            AdapterName = "vendor",
            Operation = "GET /ping",
            Timestamp = DateTime.UtcNow,
            DurationMs = 1,
            Attempts = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "test-host",
        };
}
