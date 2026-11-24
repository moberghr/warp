using System.Diagnostics;

namespace Warp.Worker;

/// <summary>
/// Tracks how many jobs a dispatcher group has claimed but not yet finished, so the dispatcher can
/// size its next claim to the capacity that is genuinely free.
/// <para>
/// This is ONE counter on purpose. The obvious alternative — idle workers plus channel occupancy —
/// reads two values that move independently: a worker removes its job from the channel BEFORE it
/// starts running it, so in the window between those two events the job is counted in neither, and
/// the dispatcher sees a free slot that is not free. Claims sized that way over-shoot the configured
/// depth (including claiming ahead when <c>PrefetchCount</c> is 0). Counting outstanding jobs
/// instead is consistent by construction: the dispatcher reserves at claim time, and the slot is
/// released exactly once, when the job is finished with or handed back.
/// </para>
/// </summary>
public sealed class DispatcherWorkerAvailability : IDisposable
{
    // Released when the group goes from no free slot to one: the dispatcher of a full group waits on it
    // instead of polling (rule 6.19). At most one pending release; a stale one costs one extra check.
    private readonly SemaphoreSlim _slotFreed = new(0, 1);
    private readonly Lock _slotFreedLock = new();
    private int _outstanding;
    private long _freedAt;

    public DispatcherWorkerAvailability(int workerCount, int prefetchCount)
    {
        Capacity = workerCount + prefetchCount;
    }

    /// <summary>
    /// Jobs this group may hold at once: one per worker, plus the configured prefetch depth.
    /// </summary>
    public int Capacity { get; }

    public int Outstanding => Volatile.Read(ref _outstanding);

    /// <summary>
    /// Free capacity to size the next claim to. Never negative.
    /// </summary>
    public int Available => Math.Max(0, Capacity - Volatile.Read(ref _outstanding));

    /// <summary>
    /// Books <paramref name="count"/> claimed jobs against capacity. Only the dispatcher calls this,
    /// and only after the rows are committed as Processing.
    /// </summary>
    public void Reserve(int count) => Interlocked.Add(ref _outstanding, count);

    /// <summary>
    /// Returns one job's capacity. Called once per reserved job — by the worker that finishes it, or
    /// by the dispatcher for a job it claimed but could not deliver. Returns the jobs still outstanding
    /// right after this one was returned (the dispatcher may claim more a moment later).
    /// </summary>
    public int Release()
    {
        var remaining = Interlocked.Decrement(ref _outstanding);
        if (remaining != Capacity - 1)
        {
            return remaining;
        }

        // The group went from no free slot to one: wake its dispatcher. Opt-in timing
        // (DispatcherDiagnostics.FreeSlotWait): remember the moment, so the next claim can record how long
        // that slot waited for it.
        if (DispatcherDiagnostics.FreeSlotWait.Enabled)
        {
            Interlocked.CompareExchange(ref _freedAt, Stopwatch.GetTimestamp(), 0);
        }

        SignalSlotFreed();

        return remaining;
    }

    /// <summary>
    /// Returns <paramref name="count"/> jobs' capacity in one step.
    /// </summary>
    public void Release(int count)
    {
        var after = Interlocked.Add(ref _outstanding, -count);
        if (after < Capacity && after + count >= Capacity)
        {
            SignalSlotFreed();
        }
    }

    /// <summary>
    /// Waits until a worker frees a slot in a full group, or <paramref name="timeout"/> (a backstop) passes.
    /// </summary>
    internal Task<bool> WaitForFreeSlotAsync(TimeSpan timeout, CancellationToken ct) => _slotFreed.WaitAsync(timeout, ct);

    /// <summary>
    /// When a slot was freed in a group that had none, since the last call; 0 if none was.
    /// </summary>
    internal long TakeFreedAt() => Interlocked.Exchange(ref _freedAt, 0);

    public void Dispose() => _slotFreed.Dispose();

    private void SignalSlotFreed()
    {
        lock (_slotFreedLock)
        {
            if (_slotFreed.CurrentCount == 0)
            {
                _slotFreed.Release();
            }
        }
    }
}
