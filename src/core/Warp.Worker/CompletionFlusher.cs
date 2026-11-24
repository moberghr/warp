using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Core.Services;

namespace Warp.Worker;

/// <summary>
/// Writes a dispatcher worker group's completions (rule 6.13). Workers only add to the group's
/// <see cref="CompletionBatch"/> and go back to their channel; this one loop does every write, so no
/// worker waits on the database for a job it has already finished, and the group never has two flushes
/// in flight. While a write runs, the next buffer fills behind it.
/// <para>
/// A buffer is flushed when it is full, when the group has nothing left claimed, or when its own timer
/// (started by its first completion) fires and it is still waiting. Whoever sees a trigger wakes the
/// loop, and the loop checks the triggers again before it writes, so a stale wake writes nothing.
/// </para>
/// </summary>
internal sealed class CompletionFlusher<TContext>
    where TContext : DbContext
{
    private readonly CompletionBatch _batch;
    private readonly DispatcherWorkerAvailability _availability;
    private readonly TimeProvider _timeProvider;
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly ILogger _logger;

    // Coalescing wake: one pending wake is enough, the loop re-reads the triggers itself.
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    // The generation of the buffer whose timer has fired, or -1.
    private long _dueGeneration = -1;

    // When the current buffer's first completion arrived, for the opt-in flush-age timing; 0 if unknown.
    private long _bufferStartedAt;
    private Task? _loop;

    public CompletionFlusher(
        CompletionBatch batch,
        DispatcherWorkerAvailability availability,
        TimeProvider timeProvider,
        ServerTaskSignals<TContext> signals,
        IWarpNotificationTransport notificationTransport,
        ILogger logger)
    {
        _batch = batch;
        _availability = availability;
        _timeProvider = timeProvider;
        _signals = signals;
        _notificationTransport = notificationTransport;
        _logger = logger;
    }

    public void Start()
    {
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Buffers a finished job. The first completion of a buffer starts its timer.</summary>
    public void Add(PendingCompletion completion)
    {
        if (_batch.Add(completion, out var generation))
        {
            if (DispatcherDiagnostics.FlushAge.Enabled)
            {
                Interlocked.Exchange(ref _bufferStartedAt, Stopwatch.GetTimestamp());
            }

            _ = WakeWhenDueAsync(generation);
        }

        if (_batch.IsFull)
        {
            Wake();
        }
    }

    /// <summary>
    /// Called by a worker after it hands its slot back, with the jobs still outstanding at that moment:
    /// when the group had nothing left claimed, what is buffered is written, so the last job of a burst
    /// and a lone job never wait for the timer. The buffer is marked due here, like a timer marks its
    /// own: by the time the loop runs, the dispatcher may have claimed the next job, and a live read of
    /// the outstanding count would then leave this completion waiting for the timer.
    /// </summary>
    public void SlotReleased(int remaining)
    {
        if (remaining == 0)
        {
            MarkDue(_batch.Generation);
            Wake();
        }
    }

    /// <summary>
    /// Ends the loop and writes whatever is still buffered. Called once every worker of the group has
    /// stopped, so nothing is added after it.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _wake.Writer.TryComplete();
        if (_loop != null)
        {
            await _loop.ConfigureAwait(false);
        }

        try
        {
            var flushed = await _batch.FlushAsync();
            await AnnounceAsync(flushed, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Final batch flush on shutdown failed");
        }
    }

    private void Wake() => _wake.Writer.TryWrite(true);

    private async Task WakeWhenDueAsync(long generation)
    {
        await Task.Delay(_batch.FlushInterval, _timeProvider).ConfigureAwait(false);

        // By generation, not IsTimeElapsed: the timer and that clock are different sources, and the
        // timer may fire a hair early (rule 6.13). A buffer drained since then is not this timer's.
        MarkDue(generation);
        Wake();
    }

    // Keeps the newest generation marked due, so a late timer for a buffer already written cannot
    // overwrite the mark of the one filling now.
    private void MarkDue(long generation)
    {
        var current = Interlocked.Read(ref _dueGeneration);
        while (generation > current)
        {
            var seen = Interlocked.CompareExchange(ref _dueGeneration, generation, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    private bool IsDue() =>
        _batch.IsFull
        || _availability.Outstanding == 0
        || _batch.Holds(Interlocked.Read(ref _dueGeneration));

    private async Task RunAsync()
    {
        while (await _wake.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            _wake.Reader.TryRead(out _);

            // Several buffers may be due back to back: one filled while the last was being written.
            while (_batch.Count > 0 && IsDue())
            {
                await FlushSafelyAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task FlushSafelyAsync()
    {
        try
        {
            var timed = DispatcherDiagnostics.FlushDuration.Enabled;
            var start = 0L;
            if (timed)
            {
                // Opt-in timings (DispatcherDiagnostics). Read before the drain; a completion added in
                // between lands in this flush and is counted with it.
                start = Stopwatch.GetTimestamp();
                DispatcherDiagnostics.FlushSize.Record(_batch.Count);
                if (Interlocked.Exchange(ref _bufferStartedAt, 0) is var startedAt && startedAt != 0)
                {
                    DispatcherDiagnostics.FlushAge.Record(DispatcherDiagnostics.Since(startedAt));
                }
            }

            var flushed = await _batch.FlushAsync();
            if (timed)
            {
                DispatcherDiagnostics.FlushDuration.Record(DispatcherDiagnostics.Since(start));
            }

            await AnnounceAsync(flushed, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The drained jobs stay Processing; StaleJobRecovery requeues them.
            _logger.LogError(ex, "Failed to flush completion batch");
        }
    }

    // What the flush took goes with the wake, as in WarpWorkerService (rule 6.11).
    private async Task AnnounceAsync(FlushedJobs flushed, CancellationToken cancellationToken)
    {
        _signals.SignalJobsFinalized(flushed.Parents, flushed.Settled);
        await NotificationDispatch.FireAsync(
            _notificationTransport,
            [new Notification(NotificationKind.JobFinalized, FinalizedParentsPayload.Encode(flushed.Parents, flushed.Settled))],
            cancellationToken);
    }
}
