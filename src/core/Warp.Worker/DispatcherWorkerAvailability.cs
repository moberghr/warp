namespace Warp.Worker;

/// <summary>
/// Tracks how many workers in a dispatcher group are currently executing a job, so the dispatcher
/// can size a claim to the workers that are actually free.
/// <para>
/// The dispatcher cannot infer this from the channel: a worker pulls its job OUT of the channel
/// before running it, so channel occupancy drops to zero while every worker is busy. Sizing claims
/// on channel space alone therefore reads "all free" at exactly the moment nothing is free, and
/// claims a further batch that sits Processing with nothing to run it.
/// </para>
/// </summary>
public sealed class DispatcherWorkerAvailability
{
    private int _busy;

    public DispatcherWorkerAvailability(int workerCount)
    {
        WorkerCount = workerCount;
    }

    public int WorkerCount { get; }

    /// <summary>
    /// Workers not currently inside a handler. Never negative — the counter is only decremented by
    /// the worker that incremented it, in a finally block.
    /// </summary>
    public int Idle => Math.Max(0, WorkerCount - Volatile.Read(ref _busy));

    public void EnterBusy() => Interlocked.Increment(ref _busy);

    public void ExitBusy() => Interlocked.Decrement(ref _busy);
}
