using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Warp.Worker;

/// <summary>
/// Opt-in timings of the dispatcher path, for finding where a job's wall time goes: whether workers
/// wait for jobs, how long a free slot waits for the next claim, how long a claimed job waits for a
/// worker, and how long a finished job waits to be written. Nothing is measured unless a listener
/// subscribed to the meter (<c>dotnet-counters --counters Warp.Diagnostics.Dispatcher</c>, a
/// <see cref="MeterListener"/>): each site asks its instrument's <c>Enabled</c> first, so the cost on
/// the worker path otherwise is one field read. Separate from the always-on <c>Warp</c> meter: these are
/// diagnostics, not product metrics. All times are milliseconds.
/// </summary>
internal static class DispatcherDiagnostics
{
    public const string MeterName = "Warp.Diagnostics.Dispatcher";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Wall time a worker spends on one job, from taking it off the channel to handing its slot back.</summary>
    public static readonly Counter<double> WorkerBusy = Meter.CreateCounter<double>("warp.dispatcher.worker.busy", "ms");

    /// <summary>Time a worker waits for its next job after handing a slot back.</summary>
    public static readonly Counter<double> WorkerIdle = Meter.CreateCounter<double>("warp.dispatcher.worker.idle", "ms");

    /// <summary>One claim's database round trip.</summary>
    public static readonly Histogram<double> ClaimDuration = Meter.CreateHistogram<double>("warp.dispatcher.claim.duration", "ms");

    /// <summary>Jobs one claim returned (an empty claim records 0).</summary>
    public static readonly Histogram<int> ClaimSize = Meter.CreateHistogram<int>("warp.dispatcher.claim.size", "{job}");

    /// <summary>From the group having no free slot to having one again and starting the claim that fills it.</summary>
    public static readonly Histogram<double> FreeSlotWait = Meter.CreateHistogram<double>("warp.dispatcher.claim.free_slot_wait", "ms");

    /// <summary>From a claim returning a job to a worker taking it off the channel.</summary>
    public static readonly Histogram<double> ChannelWait = Meter.CreateHistogram<double>("warp.dispatcher.channel.wait", "ms");

    /// <summary>Age of a completion buffer's first entry when its flush starts.</summary>
    public static readonly Histogram<double> FlushAge = Meter.CreateHistogram<double>("warp.dispatcher.flush.age", "ms");

    /// <summary>One completion flush's database write.</summary>
    public static readonly Histogram<double> FlushDuration = Meter.CreateHistogram<double>("warp.dispatcher.flush.duration", "ms");

    /// <summary>Completions one flush wrote.</summary>
    public static readonly Histogram<int> FlushSize = Meter.CreateHistogram<int>("warp.dispatcher.flush.size", "{job}");

    /// <summary>
    /// When each claimed job's claim returned, for <see cref="ChannelWait"/>. Filled only while
    /// <see cref="ChannelWait"/> has a listener; the worker that takes the job removes its entry, so a
    /// listener detached mid-run strands at most the jobs claimed in that moment.
    /// </summary>
    public static readonly ConcurrentDictionary<Guid, long> ClaimedAt = new();

    public static double Since(long timestamp) => Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
}
