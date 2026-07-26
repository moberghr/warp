using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Logging;
using Warp.Core.Observability;
using Warp.Core.Services;

namespace Warp.Worker.Services;

/// <summary>
/// Periodically samples per-queue backlog — the count of eligible (<see cref="State.Enqueued"/>,
/// <c>ScheduleTime &lt;= now</c>) jobs and the age of the oldest — and publishes it as the always-on
/// <c>warp.job.queue.depth</c> / <c>warp.job.queue.oldest_age_seconds</c> gauges (§8.26). Off the worker hot
/// path (§0.2/§6.1): one grouped read per tick on the server context. Under a DB-writing
/// <see cref="WarpConfiguration.JobMetricsSink"/> (<c>Database</c>/<c>Both</c>) it also upserts a per-queue
/// backlog <see cref="Statistic"/> the dashboard reads (a gauge — overwritten each tick, never a
/// <c>Counter</c>, so the aggregator never doubles it); under <c>Otel</c> the gauges carry it and no rows are
/// written. The grouped query is served by the existing <c>{Kind, CurrentState, Queue, ScheduleTime}</c> index.
/// </summary>
public sealed class BacklogSampler<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly TimeProvider _time;
    private readonly WarpServerConfiguration _configuration;

    public BacklogSampler(
        IWarpServerContext serverContext,
        TimeProvider time,
        IOptions<WarpServerConfiguration> configuration)
    {
        _context = serverContext.Context;
        _time = time;
        _configuration = configuration.Value;
    }

    public string Name => "BacklogSampler";

    public string? LockKey => "warp:backlog-sample";

    public TimeSpan? DefaultInterval => _configuration.BacklogSampleInterval;

    public bool RerunImmediately => false;

    public bool LogOnSuccess => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var backlog = await _context.Set<Job>()
            .Where(x => x.Kind == JobKind.Job)
            .Where(x => x.CurrentState == State.Enqueued)
            .Where(x => x.ScheduleTime <= now)
            .GroupBy(x => x.Queue)
            .Select(g => new BacklogRow(g.Key, g.LongCount(), g.Min(x => x.ScheduleTime)))
            .ToListAsync(ct);

        var samples = backlog
            .ConvertAll(b => new BacklogSample(b.Queue, b.Depth, Math.Max(0, (now - b.Oldest).TotalSeconds)));

        // Always-on: publish the gauge snapshot regardless of sink (empty ⇒ gauges report nothing this tick).
        WarpTelemetry.SetBacklogSnapshot(samples);

        if (_configuration.JobMetricsSink is RecordingSink.Database or RecordingSink.Both)
        {
            await UpsertBacklogStatisticsAsync(samples, ct);
        }

        return backlog.Count > 0 ? $"Sampled backlog for {backlog.Count} queue(s)" : null;
    }

    // Upserts the point-in-time backlog Statistic rows the dashboard reads (queue-global — backlog is not
    // application-attributable, §8.23). Zeroes previously-recorded queues absent from this tick (drained to
    // empty) so the dashboard shows 0, not a stale depth, then overwrites the present queues. Never writes
    // Counter rows under the qbacklog prefix, so CounterAggregator leaves it alone.
    private async Task UpsertBacklogStatisticsAsync(IReadOnlyList<BacklogSample> samples, CancellationToken ct)
    {
        var existing = await _context.Set<Statistic>()
            .Where(x => x.Key.StartsWith(QueueBacklogKeys.Prefix + ":"))
            .ToDictionaryAsync(x => x.Key, StringComparer.Ordinal, ct);

        // A drained queue's backlog is 0, not its last non-zero reading — reset everything, then set present.
        foreach (var stat in existing.Values)
        {
            stat.Value = 0;
        }

        foreach (var sample in samples)
        {
            var queue = QueueBacklogKeys.Sanitize(sample.Queue);
            SetStatistic(existing, QueueBacklogKeys.Total(queue, QueueBacklogKeys.DepthToken), sample.Depth);
            SetStatistic(existing, QueueBacklogKeys.Total(queue, QueueBacklogKeys.OldestAgeToken), (long)sample.OldestAgeSeconds);
        }

        await _context.SaveChangesAsync(ct);
    }

    private void SetStatistic(Dictionary<string, Statistic> existing, string key, long value)
    {
        if (existing.TryGetValue(key, out var stat))
        {
            stat.Value = value;

            return;
        }

        var added = new Statistic { Key = key, Value = value };
        existing[key] = added;
        _context.Set<Statistic>().Add(added);
    }

    private sealed record BacklogRow(string Queue, long Depth, DateTime Oldest);
}
