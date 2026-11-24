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
internal sealed class CompletionFlusher<TContext> : IDisposable
    where TContext : DbContext
{
    private readonly CompletionBatch _batch;
    private readonly DispatcherWorkerAvailability _availability;
    private readonly TimeProvider _timeProvider;
    private readonly ServerTaskSignals<TContext> _signals;
    private readonly IWarpNotificationTransport _notificationTransport;
    private readonly ILogger _logger;
    private readonly KeyValuePair<string, object?> _diagnosticsTag;

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

    private const int FinalFlushBudgetSeconds = 5;

    // Completed when a write ends, then replaced: a worker that found the group holding too many unwritten
    // completions waits on the one current when it added (#348).
    private TaskCompletionSource _writeEnded = NewWriteEnded();

    // Cancelled when StopAsync's token fires with a write still running.
    private readonly CancellationTokenSource _lifetime = new();

    // Read once: a handler that outlived the shutdown adds its completion after the host has disposed
    // this flusher, and reading the token of a disposed source throws before anything is written.
    private readonly CancellationToken _lifetimeToken;
    private readonly Lock _stopGate = new();
    private bool _stopped;

    public CompletionFlusher(
        CompletionBatch batch,
        DispatcherWorkerAvailability availability,
        TimeProvider timeProvider,
        ServerTaskSignals<TContext> signals,
        IWarpNotificationTransport notificationTransport,
        ILogger logger,
        KeyValuePair<string, object?> diagnosticsTag)
    {
        _lifetimeToken = _lifetime.Token;
        _batch = batch;
        _diagnosticsTag = diagnosticsTag;
        _availability = availability;
        _timeProvider = timeProvider;
        _signals = signals;
        _notificationTransport = notificationTransport;
        _logger = logger;
    }

    public void Start()
    {
        _loop = Task.Run(RunAsync, CancellationToken.None);
    }

    public void Dispose() => _lifetime.Dispose();

    /// <summary>
    /// Buffers a finished job; the first completion of a buffer starts its timer. After
    /// <see cref="StopAsync"/> has made its final flush (a handler that outlived the shutdown), the
    /// completion is written at once instead, since nothing would write the buffer any more.
    /// <para>
    /// Backpressure (#348): when the group already holds <see cref="CompletionBatch.BackpressureThreshold"/>
    /// unwritten completions, the worker waits for the write in flight to end before it takes its next
    /// job, still holding its slot, so the group claims nothing more meanwhile. A slow or hung database
    /// then stops the group instead of growing the number of finished jobs a crash would run again. Under
    /// normal load the buffer never gets there and this is one comparison. The wait ends early on
    /// <paramref name="cancellationToken"/>: the completion is buffered, and the final flush writes it.
    /// </para>
    /// </summary>
    public ValueTask AddAsync(PendingCompletion completion, CancellationToken cancellationToken = default)
    {
        lock (_stopGate)
        {
            if (!_stopped)
            {
                // Taken before adding: a write that ends in between completes it, and the worker goes on.
                var writeEnded = Volatile.Read(ref _writeEnded).Task;
                Buffer(completion);

                return _batch.Count < _batch.BackpressureThreshold
                    ? default
                    : WaitForWriteAsync(writeEnded, cancellationToken);
            }

            _batch.Add(completion, out _);
        }

        return new ValueTask(FlushSafelyAsync());
    }

    private static TaskCompletionSource NewWriteEnded() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async ValueTask WaitForWriteAsync(Task writeEnded, CancellationToken cancellationToken)
    {
        try
        {
            await writeEnded.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping: the completion is buffered and the final flush writes it.
        }
    }

    // Releases the workers waiting on the write that just ended, and starts the next one's wait.
    private void EndWrite() => Interlocked.Exchange(ref _writeEnded, NewWriteEnded()).TrySetResult();

    private void Buffer(PendingCompletion completion)
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
    /// Ends the loop and writes whatever is still buffered, waiting no longer than the host's shutdown
    /// token: a write still running when it fires is cancelled, and its jobs stay Processing for
    /// StaleJobRecovery, the same as a crash in that window. Called once every worker of the group has
    /// stopped; a completion added after it is written inline (<see cref="AddAsync"/>).
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stopGate)
        {
            _stopped = true;
        }

        _wake.Writer.TryComplete();
        if (_loop != null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("A completion write was still running at shutdown; cancelling it");
                await _lifetime.CancelAsync().ConfigureAwait(false);
            }
        }

        // The final flush has a budget of its own, as the other drain pipelines do (rule 2.19): the host's
        // token may already have fired while the workers stopped, and cancelling the final write on it
        // would drop every completion still buffered.
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(FinalFlushBudgetSeconds));
            var flushed = await _batch.FlushAsync(budget.Token);
            await AnnounceAsync(flushed, budget.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Final batch flush on shutdown failed");
        }
        finally
        {
            EndWrite();
        }
    }

    private void Wake() => _wake.Writer.TryWrite(true);

    private async Task WakeWhenDueAsync(long generation)
    {
        try
        {
            await Task.Delay(_batch.FlushInterval, _timeProvider, _lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shut down: the final flush has taken this buffer.
            return;
        }

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
        while (await WaitForWakeAsync().ConfigureAwait(false))
        {
            _wake.Reader.TryRead(out _);

            // Several buffers may be due back to back: one filled while the last was being written.
            while (_batch.Count > 0 && IsDue())
            {
                await FlushSafelyAsync().ConfigureAwait(false);
            }
        }
    }

    // False when the flusher stops, or is cancelled at shutdown.
    private async Task<bool> WaitForWakeAsync()
    {
        try
        {
            return await _wake.Reader.WaitToReadAsync(_lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
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
                DispatcherDiagnostics.FlushSize.Record(_batch.Count, _diagnosticsTag);
                if (Interlocked.Exchange(ref _bufferStartedAt, 0) is var startedAt && startedAt != 0)
                {
                    DispatcherDiagnostics.FlushAge.Record(DispatcherDiagnostics.Since(startedAt), _diagnosticsTag);
                }
            }

            var flushed = await _batch.FlushAsync(_lifetimeToken);
            if (timed)
            {
                DispatcherDiagnostics.FlushDuration.Record(DispatcherDiagnostics.Since(start), _diagnosticsTag);
            }

            await AnnounceAsync(flushed, _lifetimeToken);
        }
        catch (Exception ex)
        {
            // The drained jobs stay Processing; StaleJobRecovery requeues them.
            _logger.LogError(ex, "Failed to flush completion batch");
        }
        finally
        {
            EndWrite();
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
