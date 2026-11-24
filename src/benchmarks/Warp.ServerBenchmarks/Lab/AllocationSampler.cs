using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// Which types the process allocates, from the runtime's own allocation sampling (one
/// GCAllocationTick event per ~100 KB allocated, carrying the type of the object that crossed the
/// threshold). In-process, so it needs no tool installed; opt-in with <c>WARP_LAB_ALLOC=1</c>. A type's
/// share of the samples is its share of the bytes, to within sampling noise.
/// </summary>
internal sealed class AllocationSampler : EventListener
{
    // GC keyword; GCAllocationTick is raised at Verbose.
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private const int AllocationTickEventId = 10;

    private readonly ConcurrentDictionary<string, long> _bytesByType = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _contextsByType = new(StringComparer.Ordinal);
    private readonly long _allocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
    private readonly IDisposable _contexts;

    // One subscription per EF listener: disposing only the AllListeners one left each EF listener holding
    // this sampler, so repeated scenarios accumulated callbacks and kept old samples alive.
    private readonly List<IDisposable> _listeners = [];
    private volatile bool _sampling = true;

    public AllocationSampler()
    {
        // Every DbContext instance EF initializes, by type: each one builds EF's per-context services.
        _contexts = DiagnosticListener.AllListeners.Subscribe(new ListenerObserver(this));
    }

    public static bool IsRequested =>
        string.Equals(Environment.GetEnvironmentVariable("WARP_LAB_ALLOC"), "1", StringComparison.Ordinal);

    /// <summary>Stops sampling and prints the top types by sampled bytes.</summary>
    public void Report(int jobs, int top = 30)
    {
        _sampling = false;
        var allocated = GC.GetTotalAllocatedBytes(precise: false) - _allocatedAtStart;
        var sampled = _bytesByType.Values.Sum();

        Console.WriteLine($"-- allocations (drain window) {new string('-', 30)}");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  allocated {allocated / 1024.0 / 1024.0:N1} MB over {jobs:N0} jobs = {allocated / 1024.0 / Math.Max(1, jobs):N1} KB/job (whole process, includes the harness)"));

        foreach (var (type, bytes) in _bytesByType.OrderByDescending(x => x.Value).Take(top))
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {100.0 * bytes / Math.Max(1, sampled),5:N1}%  {bytes / 1024.0 / Math.Max(1, jobs),7:N2} KB/job  {type}"));
        }

        StopListening();
        foreach (var (type, count) in _contextsByType.OrderByDescending(x => x.Value))
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  contexts initialized: {count / (double)Math.Max(1, jobs),6:N3}/job  {type}"));
        }
    }

    public override void Dispose()
    {
        StopListening();
        base.Dispose();
    }

    private void StopListening()
    {
        _contexts.Dispose();
        lock (_listeners)
        {
            foreach (var listener in _listeners)
            {
                listener.Dispose();
            }

            _listeners.Clear();
        }
    }

    private sealed class ListenerObserver(AllocationSampler sampler) : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener value)
        {
            if (string.Equals(value.Name, DbLoggerCategory.Name, StringComparison.Ordinal))
            {
                lock (sampler._listeners)
                {
                    sampler._listeners.Add(value.Subscribe(new EventObserver(sampler)));
                }
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }

    private sealed class EventObserver(AllocationSampler sampler) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (sampler._sampling
                && string.Equals(value.Key, CoreEventId.ContextInitialized.Name, StringComparison.Ordinal)
                && value.Value is ContextInitializedEventData data)
            {
                sampler._contextsByType.AddOrUpdate(data.Context.GetType().Name, 1, (_, x) => x + 1);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (string.Equals(eventSource.Name, "Microsoft-Windows-DotNETRuntime", StringComparison.Ordinal))
        {
            EnableEvents(eventSource, EventLevel.Verbose, GcKeyword);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!_sampling || eventData.EventId != AllocationTickEventId || eventData.PayloadNames is null)
        {
            return;
        }

        var type = "?";
        var amount = 0L;
        for (var i = 0; i < eventData.PayloadNames.Count; i++)
        {
            switch (eventData.PayloadNames[i])
            {
                case "TypeName":
                    type = eventData.Payload![i] as string ?? "?";
                    break;
                case "AllocationAmount64":
                    amount = Convert.ToInt64(eventData.Payload![i], CultureInfo.InvariantCulture);
                    break;
            }
        }

        _bytesByType.AddOrUpdate(type, amount, (_, x) => x + amount);
    }
}
