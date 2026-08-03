namespace Warp.Core.Logging;

/// <summary>
/// The lossy recording pipelines that can drop records when their bounded channel is full (§8.19/§8.21/§8.27).
/// Values start at 1 (§8.11). Used to key the in-process <see cref="DroppedRecordCounters"/> and the durable
/// <c>warpsys:records-dropped</c> stat so a drop is visible in-box, not only on the OTel meter.
/// </summary>
public enum DropPipeline
{
    /// <summary>Outbound adapter call-log records (<c>warp.adapter.records_dropped</c>).</summary>
    Adapter = 1,

    /// <summary>Inbound endpoint call-log records (<c>warp.endpoint.records_dropped</c>).</summary>
    Endpoint = 2,

    /// <summary>Client (browser) events (<c>warp.client.events.dropped</c>).</summary>
    Client = 3,
}

/// <summary>
/// Process-global counters for records dropped by the lossy recording pipelines. A drop already increments an
/// always-on OTel meter (reliable, because it never rides the lossy channel it reports on), but that is only
/// observable through an OTel backend. These in-process counters let the drop count also reach the durable
/// <c>Statistic</c> store so it is visible in Warp's own dashboard without OTel: a drop site calls
/// <see cref="Track"/> (a single interlocked add — safe on the recording-failure path), and the reporter drains
/// them with <see cref="Drain"/> and folds the delta to the DB. Per-process by design — each process reports the
/// records it itself dropped.
/// </summary>
public static class DroppedRecordCounters
{
    // Indexed by (int)DropPipeline - 1.
    private static readonly long[] Counts = new long[3];

    /// <summary>Record <paramref name="count"/> drops for <paramref name="pipeline"/>. Non-blocking; safe to call on the drop path.</summary>
    public static void Track(DropPipeline pipeline, long count)
    {
        if (count <= 0)
        {
            return;
        }

        System.Threading.Interlocked.Add(ref Counts[(int)pipeline - 1], count);
    }

    /// <summary>Atomically read and reset the accumulated drop count for <paramref name="pipeline"/> (the delta since the last drain).</summary>
    public static long Drain(DropPipeline pipeline)
        => System.Threading.Interlocked.Exchange(ref Counts[(int)pipeline - 1], 0);
}
