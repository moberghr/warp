using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace Warp.Core.Buffering;

/// <summary>
/// The reader side of a lossy in-process pipeline: one background loop that waits for items, takes up
/// to <see cref="BatchSize"/> at a time and hands them to <see cref="FlushAsync"/>. Holds no thread while
/// idle. Subclasses supply the reader and the flush; the loop and the shutdown drain are written once here.
/// <para>
/// Shutdown: stop accepting new items (<see cref="CompleteWriting"/>), stop the loop, then drain what is
/// still queued within <see cref="ShutdownDrainBudget"/>. The budget token, never the host's shutdown
/// token, is passed into the flush, so graceful shutdown cannot discard the queued tail and a hung
/// database or transport cannot hang shutdown either. What is left when the budget runs out is dropped:
/// these pipelines carry diagnostics and wake-ups, not an audit trail.
/// </para>
/// <para>
/// <see cref="FlushAsync"/> must not throw: an exception escaping it ends the loop and, with the
/// default host settings, stops the host. Log and drop instead.
/// </para>
/// <para>
/// Public because subclasses live in packages that compose against Core's public API only (§0.5).
/// </para>
/// </summary>
public abstract class DrainService<TItem> : BackgroundService
{
    /// <summary>The single reader of the pipeline's queue.</summary>
    protected abstract ChannelReader<TItem> Reader { get; }

    /// <summary>The most items handed to one <see cref="FlushAsync"/> call.</summary>
    protected abstract int BatchSize { get; }

    /// <summary>How long the shutdown drain may take in total, flushes included.</summary>
    protected abstract TimeSpan ShutdownDrainBudget { get; }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        CompleteWriting();

        // base.StopAsync signals stoppingToken and awaits ExecuteAsync, so the loop has stopped reading
        // before the drain below starts (the queue has a single reader).
        await base.StopAsync(cancellationToken);

        await DrainRemainingAsync(Reader, FlushAsync, ShutdownDrainBudget, BatchSize);
    }

    /// <summary>
    /// Drains <paramref name="reader"/> in batches until it is empty or <paramref name="budget"/> runs
    /// out. Static so tests can prove the budget bounds a hanging flush without a host or a database.
    /// </summary>
    internal static async Task DrainRemainingAsync(
        ChannelReader<TItem> reader,
        Func<List<TItem>, CancellationToken, Task> flush,
        TimeSpan budget,
        int batchSize)
    {
        using var cts = new CancellationTokenSource(budget);

        while (!cts.IsCancellationRequested)
        {
            var batch = TakeBatch(reader, batchSize);
            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                await flush(batch, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Budget elapsed mid-flush: stop draining, whatever the flush does with the cancellation.
                return;
            }
        }
    }

    /// <summary>Stops the queue accepting new items. Called first thing at shutdown.</summary>
    protected abstract void CompleteWriting();

    /// <summary>Handles one batch. Must not throw (see the class remarks).</summary>
    protected abstract Task FlushAsync(List<TItem> batch, CancellationToken cancellationToken);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await Reader.WaitToReadAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var batch = TakeBatch(Reader, BatchSize);
            if (batch.Count > 0)
            {
                await FlushAsync(batch, stoppingToken);
            }
        }
    }

    private static List<TItem> TakeBatch(ChannelReader<TItem> reader, int batchSize)
    {
        var batch = new List<TItem>();
        while (batch.Count < batchSize && reader.TryRead(out var item))
        {
            batch.Add(item);
        }

        return batch;
    }
}
