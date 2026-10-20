using System.Collections.Concurrent;
using Warp.Core.Data.Entities;

namespace Warp.Core.Logging;

internal class JobLogCollector
{
    /// <summary>
    /// One microsecond: the coarsest timestamp precision Warp stores (PostgreSQL), so two stamps this
    /// far apart can never collapse into the same stored value.
    /// </summary>
    private const long MinStepTicks = TimeSpan.TicksPerMicrosecond;

    /// <summary>
    /// Lines captured since the last <see cref="Drain"/>.
    /// </summary>
    private readonly ConcurrentQueue<JobLog> _entries = new();

    /// <summary>
    /// Ticks of the last timestamp handed out, so the next one can be made strictly later.
    /// </summary>
    private long _lastTicks;

    public Guid JobId { get; set; }

    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    public Guid? WorkerId { get; set; }

    public void Add(string level, string message, string? exception = null)
    {
        _entries.Enqueue(new JobLog
        {
            JobId = JobId,
            Timestamp = NextTimestamp(),
            Level = level,
            Message = message,
            Exception = exception,
            WorkerId = WorkerId,
        });
    }

    public List<JobLog> Drain()
    {
        var list = new List<JobLog>();
        while (_entries.TryDequeue(out var entry))
        {
            list.Add(entry);
        }

        return list;
    }

    /// <summary>
    /// The clock, or one microsecond past the previous stamp if the clock has not moved that far.
    /// </summary>
    /// <remarks>
    /// Strictly increasing per job, so the dashboard's newest-first order is the order the handler
    /// wrote its lines. Without it, lines logged within one clock tick tie on Timestamp and fall back
    /// to the random Guid Id, which scrambles them.
    /// </remarks>
    private DateTime NextTimestamp()
    {
        var now = TimeProvider.GetUtcNow().UtcDateTime.Ticks;
        while (true)
        {
            var last = Interlocked.Read(ref _lastTicks);
            var next = Math.Max(now, last + MinStepTicks);
            if (Interlocked.CompareExchange(ref _lastTicks, next, last) == last)
            {
                return new DateTime(next, DateTimeKind.Utc);
            }
        }
    }
}

internal static class JobLogContext
{
    private static readonly AsyncLocal<JobLogCollector?> _current = new();

    public static JobLogCollector? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
