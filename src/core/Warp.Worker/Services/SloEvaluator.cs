using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Notifiers;
using Warp.Core.Services;

namespace Warp.Worker.Services;

/// <summary>
/// Evaluates SLO objectives (§8.30) against the durable <c>Statistic</c>/<c>Counter</c> aggregates and upserts
/// their rolling <see cref="SloEvaluation"/> status — the actionable layer over the metrics jobstat / queue-wait /
/// backlog / deadline already fold. Runs off the worker hot path as a periodic <see cref="IServerTask"/> (the
/// analogue of <c>CounterAggregator</c> / <c>ErrorGroupAggregator</c>); reads only, plus the one status row per
/// objective. Rate objectives (success-rate, deadline-attainment) are windowed via the hourly history buckets;
/// latency objectives read the lifetime percentile histogram; backlog reads the current gauge. A healthy→breaching
/// edge buffers a <see cref="SloBreachedEvent"/> (or <c>BacklogBreached</c> for a depth objective), dispatched
/// post-commit from <see cref="OnCommittedAsync"/> (§8.25).
/// </summary>
public sealed class SloEvaluator<TContext> : IServerTask
    where TContext : DbContext
{
    private readonly DbContext _context;
    private readonly WarpServerConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly WarpNotifierDispatcher _notifierDispatcher;
    private readonly ILogger<SloEvaluator<TContext>> _logger;
    private readonly List<WarpOperationalEvent> _pendingAlerts = [];

    public SloEvaluator(
        IWarpServerContext serverContext,
        IOptions<WarpServerConfiguration> configuration,
        TimeProvider timeProvider,
        WarpNotifierDispatcher notifierDispatcher,
        ILogger<SloEvaluator<TContext>> logger)
    {
        _context = serverContext.Context;
        _configuration = configuration.Value;
        _timeProvider = timeProvider;
        _notifierDispatcher = notifierDispatcher;
        _logger = logger;
    }

    public string Name => "EvaluateSlos";

    public string? LockKey => "warp:slo-eval";

    public TimeSpan? DefaultInterval => _configuration.SloEvaluationInterval;

    public bool RerunImmediately => false;

    public bool LogOnSuccess => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        _pendingAlerts.Clear();

        var definitions = await _context.Set<SloDefinition>()
            .Where(x => x.Enabled)
            .ToListAsync(ct);

        if (definitions.Count == 0)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var existing = await _context.Set<SloEvaluation>().ToDictionaryAsync(x => x.SloDefinitionId, ct);

        // Each aggregate family (jobstat / qwait / qbacklog / deadline) is scanned at most once per tick and
        // reused across every objective that reads it.
        var cache = new Dictionary<string, IReadOnlyList<KeyVal>>(StringComparer.Ordinal);

        foreach (var def in definitions)
        {
            var result = await ComputeAsync(def, now, cache, ct);

            existing.TryGetValue(def.Id, out var eval);
            var ackActive = eval?.AcknowledgedUntil is { } until && until > now;
            var state = SloMath.Classify(result.Budget, ackActive);
            var previous = eval?.State ?? SloState.Healthy;

            if (eval is null)
            {
                eval = new SloEvaluation { SloDefinitionId = def.Id };
                _context.Set<SloEvaluation>().Add(eval);
            }

            eval.Attainment = result.Attainment;
            eval.BudgetRemaining = result.Budget;
            eval.BurnRateShort = result.BurnShort;
            eval.BurnRateLong = result.BurnLong;
            eval.State = state;
            eval.LastEvaluatedAt = now;

            // Fire only on the healthy→breaching edge (never every tick, never while acknowledged).
            if (state == SloState.Breaching && previous != SloState.Breaching)
            {
                BufferAlert(def, result, now);
            }
        }

        await _context.SaveChangesAsync(ct);

        return $"Evaluated {definitions.Count} SLO objective(s)";
    }

    public async Task OnCommittedAsync(CancellationToken ct)
    {
        foreach (var evt in _pendingAlerts)
        {
            await _notifierDispatcher.DispatchAsync(evt, CancellationToken.None);
        }
    }

    private Task<EvalResult> ComputeAsync(SloDefinition def, DateTime now, Dictionary<string, IReadOnlyList<KeyVal>> cache, CancellationToken ct)
        => def.Kind switch
        {
            SloKind.SuccessRate => ComputeSuccessRateAsync(def, now, cache, ct),
            SloKind.DeadlineAttainment => ComputeDeadlineAsync(def, now, cache, ct),
            SloKind.ExecutionLatency => ComputeExecutionLatencyAsync(def, now, cache, ct),
            SloKind.QueueWaitLatency => ComputeQueueWaitLatencyAsync(def, now, cache, ct),
            SloKind.BacklogDepth => ComputeBacklogAsync(def, cache, ct),
            _ => Task.FromResult(new EvalResult(1.0, 1.0, 0.0, 0.0)),
        };

    private async Task<EvalResult> ComputeSuccessRateAsync(SloDefinition def, DateTime now, Dictionary<string, IReadOnlyList<KeyVal>> cache, CancellationToken ct)
    {
        var windowStart = now.AddSeconds(-def.WindowSeconds);
        var shortStart = ShortWindowStart(def, now);
        long succW = 0, failW = 0, succS = 0, failS = 0;

        foreach (var (token, bucket, value) in ReadJobstatHistory(def, await LoadMergedAsync(JobstatPrefix(def), cache, ct)))
        {
            var inW = bucket >= windowStart;
            var inS = bucket >= shortStart;
            if (string.Equals(token, JobStatsKeys.SucceededToken, StringComparison.Ordinal))
            {
                succW += inW ? value : 0;
                succS += inS ? value : 0;
            }
            else if (string.Equals(token, JobStatsKeys.FailedToken, StringComparison.Ordinal))
            {
                failW += inW ? value : 0;
                failS += inS ? value : 0;
            }
        }

        var (att, budget, burnLong) = SloMath.EvaluateRate(succW, succW + failW, def.TargetValue);
        var (_, _, burnShort) = SloMath.EvaluateRate(succS, succS + failS, def.TargetValue);

        return new EvalResult(att, budget, burnShort, burnLong);
    }

    private async Task<EvalResult> ComputeDeadlineAsync(SloDefinition def, DateTime now, Dictionary<string, IReadOnlyList<KeyVal>> cache, CancellationToken ct)
    {
        var windowStart = now.AddSeconds(-def.WindowSeconds);
        var shortStart = ShortWindowStart(def, now);
        long cntW = 0, missW = 0, cntS = 0, missS = 0;

        var app = def.Application is null ? null : DeadlineKeys.Sanitize(def.Application);
        var prefix = app is null ? DeadlineKeys.Prefix + ":" : DeadlineKeys.AppPrefix + ":" + app + ":";

        foreach (var row in await LoadMergedAsync(prefix, cache, ct))
        {
            string token;
            DateTime bucket;
            if (app is null)
            {
                if (!DeadlineKeys.TryParseHistory(row.Key, out var type, out token, out _, out bucket) || !string.Equals(type, def.Dimension, StringComparison.Ordinal))
                {
                    continue;
                }
            }
            else
            {
                if (!DeadlineKeys.TryParseAppHistory(row.Key, out _, out var type, out token, out _, out bucket) || !string.Equals(type, def.Dimension, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            var inW = bucket >= windowStart;
            var inS = bucket >= shortStart;
            if (string.Equals(token, DeadlineKeys.CountToken, StringComparison.Ordinal))
            {
                cntW += inW ? row.Value : 0;
                cntS += inS ? row.Value : 0;
            }
            else if (string.Equals(token, DeadlineKeys.MissToken, StringComparison.Ordinal))
            {
                missW += inW ? row.Value : 0;
                missS += inS ? row.Value : 0;
            }
        }

        var (att, budget, burnLong) = SloMath.EvaluateRate(cntW - missW, cntW, def.TargetValue);
        var (_, _, burnShort) = SloMath.EvaluateRate(cntS - missS, cntS, def.TargetValue);

        return new EvalResult(att, budget, burnShort, burnLong);
    }

    private async Task<EvalResult> ComputeExecutionLatencyAsync(SloDefinition def, DateTime now, Dictionary<string, IReadOnlyList<KeyVal>> cache, CancellationToken ct)
    {
        var windowStart = now.AddSeconds(-def.WindowSeconds);
        var shortStart = ShortWindowStart(def, now);
        var wide = new Dictionary<int, long>();
        var recent = new Dictionary<int, long>();

        // Windowed percentile from the tiered pcth histogram (§8.30) — the recent (fine) buckets give fast-burn.
        foreach (var row in await LoadMergedAsync(JobStatsKeys.Prefix + ":", cache, ct))
        {
            if (JobStatsKeys.TryParsePctHistory(row.Key, out var dim, out var id, out var upperMs, out _, out var bucket)
                && string.Equals(dim, JobStatsKeys.TypeMarker, StringComparison.Ordinal)
                && string.Equals(id, def.Dimension, StringComparison.Ordinal))
            {
                if (bucket >= windowStart)
                {
                    wide[upperMs] = wide.GetValueOrDefault(upperMs) + row.Value;
                }

                if (bucket >= shortStart)
                {
                    recent[upperMs] = recent.GetValueOrDefault(upperMs) + row.Value;
                }
            }
        }

        return ThresholdWindowed(wide, recent, def);
    }

    private async Task<EvalResult> ComputeQueueWaitLatencyAsync(SloDefinition def, DateTime now, Dictionary<string, IReadOnlyList<KeyVal>> cache, CancellationToken ct)
    {
        var windowStart = now.AddSeconds(-def.WindowSeconds);
        var shortStart = ShortWindowStart(def, now);
        var wide = new Dictionary<int, long>();
        var recent = new Dictionary<int, long>();

        foreach (var row in await LoadMergedAsync(QueueWaitKeys.Prefix + ":", cache, ct))
        {
            if (QueueWaitKeys.TryParsePctHistory(row.Key, out var queue, out var upperMs, out _, out var bucket)
                && string.Equals(queue, def.Dimension, StringComparison.Ordinal))
            {
                if (bucket >= windowStart)
                {
                    wide[upperMs] = wide.GetValueOrDefault(upperMs) + row.Value;
                }

                if (bucket >= shortStart)
                {
                    recent[upperMs] = recent.GetValueOrDefault(upperMs) + row.Value;
                }
            }
        }

        return ThresholdWindowed(wide, recent, def);
    }

    private async Task<EvalResult> ComputeBacklogAsync(SloDefinition def, Dictionary<string, IReadOnlyList<KeyVal>> cache, CancellationToken ct)
    {
        long depth = 0;
        foreach (var row in await LoadMergedAsync(QueueBacklogKeys.Prefix + ":", cache, ct))
        {
            if (QueueBacklogKeys.TryParseTotal(row.Key, out var queue, out var token)
                && string.Equals(queue, def.Dimension, StringComparison.Ordinal)
                && string.Equals(token, QueueBacklogKeys.DepthToken, StringComparison.Ordinal))
            {
                depth = row.Value;
            }
        }

        var (att, budget, burn) = SloMath.EvaluateThreshold(depth, def.TargetValue);

        return new EvalResult(att, budget, burn, burn);
    }

    // Windowed threshold latency: observed = percentile over the window's pcth buckets; burnShort = percentile
    // over the recent (fine) buckets, so a latency spike in the fast-burn window is caught.
    private static EvalResult ThresholdWindowed(Dictionary<int, long> wide, Dictionary<int, long> recent, SloDefinition def)
    {
        var percentile = def.Percentile ?? 95;
        var observed = SloMath.Percentile(wide, percentile);
        var (att, budget, burnLong) = SloMath.EvaluateThreshold(observed, def.TargetValue);
        var (_, _, burnShort) = SloMath.EvaluateThreshold(SloMath.Percentile(recent, percentile), def.TargetValue);

        return new EvalResult(att, budget, burnShort, burnLong);
    }

    // Success-rate reads the app-agnostic jobstat family, or the disjoint per-app slice when the objective is
    // application-scoped (§8.23).
    private static string JobstatPrefix(SloDefinition def)
        => def.Application is null
            ? JobStatsKeys.Prefix + ":"
            : JobStatsKeys.AppPrefix + ":" + JobStatsKeys.Sanitize(def.Application) + ":";

    // Fast-burn short window = the objective window / 12, floored to 5 minutes — small enough to catch a fast
    // burn and now populated by the fine (5-min) metrics tier (§8.30), which is what makes real fast-burn possible.
    private static DateTime ShortWindowStart(SloDefinition def, DateTime now)
        => now.AddSeconds(-Math.Max(300, def.WindowSeconds / 12.0));

    // Returns each matching jobstat history bucket as (token, bucket-start, value). Bucket-start is the tier's
    // bucket start (fine 5-min / hourly / daily, §8.30) — the caller sums buckets whose start falls in the
    // window, and the recent (fine) buckets populate the short fast-burn window.
    private static IEnumerable<(string Token, DateTime Bucket, long Value)> ReadJobstatHistory(SloDefinition def, IReadOnlyList<KeyVal> rows)
    {
        foreach (var row in rows)
        {
            if (def.Application is null)
            {
                if (JobStatsKeys.TryParseHistory(row.Key, out var dim, out var id, out var token, out _, out var bucket)
                    && string.Equals(dim, JobStatsKeys.TypeMarker, StringComparison.Ordinal)
                    && string.Equals(id, def.Dimension, StringComparison.Ordinal))
                {
                    yield return (token, bucket, row.Value);
                }
            }
            else if (JobStatsKeys.TryParseAppHistory(row.Key, out _, out var dim, out var id, out var token, out _, out var bucket)
                && string.Equals(dim, JobStatsKeys.TypeMarker, StringComparison.Ordinal)
                && string.Equals(id, def.Dimension, StringComparison.Ordinal))
            {
                yield return (token, bucket, row.Value);
            }
        }
    }

    private void BufferAlert(SloDefinition def, EvalResult result, DateTime now)
    {
        var isBacklog = def.Kind == SloKind.BacklogDepth;
        var severity = result.BurnShort > 2.0 ? WarpEventSeverity.Error : WarpEventSeverity.Warning;

        _logger.LogWarning(
            "SLO objective '{Name}' ({Kind} for {Dimension}) is breaching: budget {Budget:P0}, burn short {BurnShort:F1}x / long {BurnLong:F1}x.",
            def.Name,
            def.Kind,
            def.Dimension,
            result.Budget,
            result.BurnShort,
            result.BurnLong);

        _pendingAlerts.Add(new SloBreachedEvent
        {
            Type = isBacklog ? WarpEventType.BacklogBreached : WarpEventType.SloBreached,
            Severity = severity,
            TimestampUtc = now,
            MachineName = Environment.MachineName,
            Application = def.Application,
            Message = $"SLO '{def.Name}' breaching: {def.Kind} for {def.Dimension} (budget {result.Budget:P0})",
            Name = def.Name,
            Kind = def.Kind,
            Dimension = def.Dimension,
            Attainment = result.Attainment,
            TargetValue = def.TargetValue,
            BudgetRemaining = result.Budget,
            BurnRateShort = result.BurnShort,
            BurnRateLong = result.BurnLong,
            WindowSeconds = def.WindowSeconds,
        });
    }

    private async Task<IReadOnlyList<KeyVal>> LoadMergedAsync(string prefix, Dictionary<string, IReadOnlyList<KeyVal>> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(prefix, out var cached))
        {
            return cached;
        }

        // Mirrors JobQueryService.LoadMergedStatsAsync: union committed Statistic rows with not-yet-folded
        // Counter rows so a read between aggregator ticks is still correct.
        var stats = await _context.Set<Statistic>()
            .AsNoTracking()
            .Where(x => x.Key.StartsWith(prefix))
            .Select(x => new { x.Key, x.Value })
            .ToListAsync(ct);

        var pending = await _context.Set<Counter>()
            .AsNoTracking()
            .Where(x => x.Key.StartsWith(prefix))
            .GroupBy(x => x.Key)
            .Select(g => new { Key = g.Key, Value = g.Sum(c => (long)c.Value) })
            .ToListAsync(ct);

        var merged = stats
            .Concat(pending)
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Select(g => new KeyVal(g.Key, g.Sum(x => x.Value)))
            .ToList();

        cache[prefix] = merged;

        return merged;
    }

    private readonly record struct KeyVal(string Key, long Value);

    private sealed record EvalResult(double Attainment, double Budget, double BurnShort, double BurnLong);
}
