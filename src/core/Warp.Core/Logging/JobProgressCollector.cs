using System.Collections.Concurrent;
using Warp.Core.Data.Entities;

namespace Warp.Core.Logging;

internal class JobProgressCollector
{
    public Guid JobId { get; set; }

    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    public Guid? WorkerId { get; set; }

    private readonly ConcurrentDictionary<string, int> _current = new();
    private readonly Dictionary<string, int> _lastDrained = [];
    private readonly Lock _drainLock = new();

    public void Report(string name, int percent)
    {
        _current[name] = percent;
    }

    // A failed save lost these rows: forget that they were drained, so the next drain writes each again
    // with its current value.
    public void Forget(IEnumerable<JobLog> rows)
    {
        lock (_drainLock)
        {
            foreach (var name in rows.Select(x => x.Name).OfType<string>())
            {
                _lastDrained.Remove(name);
            }
        }
    }

    public List<JobLog> Drain()
    {
        // Hot path: jobs that don't call ReportProgress hit this every monitor tick.
        // Skip the lock + allocation when there's nothing to drain.
        if (_current.IsEmpty)
        {
            return [];
        }

        lock (_drainLock)
        {
            var rows = new List<JobLog>();
            var now = TimeProvider.GetUtcNow().UtcDateTime;
            foreach (var kvp in _current)
            {
                if (_lastDrained.TryGetValue(kvp.Key, out var last) && last == kvp.Value)
                {
                    continue;
                }

                _lastDrained[kvp.Key] = kvp.Value;
                rows.Add(new JobLog
                {
                    JobId = JobId,
                    EventType = "Progress",
                    Timestamp = now,
                    Level = "Information",
                    Message = string.Empty,
                    Name = kvp.Key,
                    Value = (short)kvp.Value,
                    WorkerId = WorkerId,
                });
            }

            return rows;
        }
    }
}
