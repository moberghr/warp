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
public sealed class DispatcherWorkerAvailability
{
    private int _outstanding;

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
    /// by the dispatcher for a job it claimed but could not deliver.
    /// </summary>
    public void Release() => Interlocked.Decrement(ref _outstanding);

    /// <summary>
    /// Returns <paramref name="count"/> jobs' capacity in one step.
    /// </summary>
    public void Release(int count) => Interlocked.Add(ref _outstanding, -count);
}
