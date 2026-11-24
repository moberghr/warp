using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Warp.MatrixBench;

/// <summary>One job id's executions as its handler saw them.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct Execution(string Kind, int Count);

/// <summary>
/// Counts handler executions per job id, in process, and when each handler finished. The counts are the
/// duplicate-execution check; the finish times are the completion histogram. Neither costs a statement.
/// </summary>
public sealed class ExecutionRecorder
{
    private readonly ConcurrentDictionary<Guid, Execution> _executions = new();
    private readonly ConcurrentQueue<long> _finished = new();
    private int _started;

    public int Started => Volatile.Read(ref _started);

    public IReadOnlyDictionary<Guid, Execution> Executions => _executions;

    public void Begin(Guid jobId, string kind)
    {
        _executions.AddOrUpdate(
            jobId,
            static (_, x) => new Execution(x, 1),
            static (_, x, _) => x with { Count = x.Count + 1 },
            kind);
        Interlocked.Increment(ref _started);
    }

    public void End() => _finished.Enqueue(Stopwatch.GetTimestamp());

    public long[] FinishedTimestamps() => [.. _finished];

    public void Reset()
    {
        _executions.Clear();
        _finished.Clear();
        Interlocked.Exchange(ref _started, 0);
    }
}
