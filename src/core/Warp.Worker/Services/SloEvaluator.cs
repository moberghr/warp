using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Notifiers;
using Warp.Core.Services;

namespace Warp.Worker.Services;

/// <summary>
/// Evaluates SLO objectives (§8.31) against the durable <c>Statistic</c>/<c>Counter</c> aggregates and upserts
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
    private readonly IMetricSource _metrics;
    private readonly WarpServerConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly WarpNotifierDispatcher _notifierDispatcher;
    private readonly ILogger<SloEvaluator<TContext>> _logger;
    private readonly List<WarpOperationalEvent> _pendingAlerts = [];

    public SloEvaluator(
        IWarpServerContext serverContext,
        IMetricSource metrics,
        IOptions<WarpServerConfiguration> configuration,
        TimeProvider timeProvider,
        WarpNotifierDispatcher notifierDispatcher,
        ILogger<SloEvaluator<TContext>> logger)
    {
        _context = serverContext.Context;
        _metrics = metrics;
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

        foreach (var def in definitions)
        {
            var result = await ComputeAsync(def, now, ct);

            existing.TryGetValue(def.Id, out var eval);
            var ackActive = eval?.AcknowledgedUntil is { } until && until > now;
            var state = result.HasData ? SloMath.Classify(result.Budget, ackActive) : SloState.NoData;
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

    private Task<EvalResult> ComputeAsync(SloDefinition def, DateTime now, CancellationToken ct)
        => def.Kind switch
        {
            SloKind.SuccessRate => ComputeSuccessRateAsync(def, now, ct),
            SloKind.DeadlineAttainment => ComputeDeadlineAsync(def, now, ct),
            SloKind.ExecutionLatency => ComputeLatencyAsync(def, now, $"{JobStatsKeys.Prefix}:{JobStatsKeys.TypeMarker}:{def.Dimension}", ct),
            SloKind.QueueWaitLatency => ComputeLatencyAsync(def, now, $"{QueueWaitKeys.Prefix}:{def.Dimension}", ct),
            SloKind.BacklogDepth => ComputeBacklogAsync(def, ct),
            _ => UnsupportedKindAsync(def),
        };

    // An out-of-range Kind reaching here means corrupt/version-skewed data (the column is a plain int with no DB
    // check constraint) — the API and config-seed paths validate Kind, so this is defense-in-depth. Report it as
    // NoData (HasData: false) rather than a false-green Healthy, and log it loudly so the bad row is visible.
    private Task<EvalResult> UnsupportedKindAsync(SloDefinition def)
    {
        _logger.LogError(
            "SLO objective {Id} '{Name}' has an unsupported Kind value {Kind} — evaluation skipped (reported as NoData).",
            def.Id,
            def.Name,
            (int)def.Kind);

        return Task.FromResult(new EvalResult(1.0, 1.0, 0.0, 0.0, HasData: false));
    }

    private async Task<EvalResult> ComputeSuccessRateAsync(SloDefinition def, DateTime now, CancellationToken ct)
    {
        var windowStart = now.AddSeconds(-def.WindowSeconds);
        var shortStart = ShortWindowStart(def, now);

        var (succW, succS) = await WindowedSumAsync(JobstatHistoryBase(def, JobStatsKeys.SucceededToken), windowStart, shortStart, ct);
        var (failW, failS) = await WindowedSumAsync(JobstatHistoryBase(def, JobStatsKeys.FailedToken), windowStart, shortStart, ct);

        var (att, budget, burnLong) = SloMath.EvaluateRate(succW, succW + failW, def.TargetValue);
        var (_, _, burnShort) = SloMath.EvaluateRate(succS, succS + failS, def.TargetValue);

        return new EvalResult(att, budget, burnShort, burnLong, HasData: succW + failW > 0);
    }

    private async Task<EvalResult> ComputeDeadlineAsync(SloDefinition def, DateTime now, CancellationToken ct)
    {
        var windowStart = now.AddSeconds(-def.WindowSeconds);
        var shortStart = ShortWindowStart(def, now);

        var (cntW, cntS) = await WindowedSumAsync(DeadlineHistoryBase(def, DeadlineKeys.CountToken), windowStart, shortStart, ct);
        var (missW, missS) = await WindowedSumAsync(DeadlineHistoryBase(def, DeadlineKeys.MissToken), windowStart, shortStart, ct);

        var (att, budget, burnLong) = SloMath.EvaluateRate(cntW - missW, cntW, def.TargetValue);
        var (_, _, burnShort) = SloMath.EvaluateRate(cntS - missS, cntS, def.TargetValue);

        return new EvalResult(att, budget, burnShort, burnLong, HasData: cntW > 0);
    }

    private async Task<EvalResult> ComputeLatencyAsync(SloDefinition def, DateTime now, string pctBase, CancellationToken ct)
    {
        var windowStart = now.AddSeconds(-def.WindowSeconds);
        var shortStart = ShortWindowStart(def, now);
        var percentile = def.Percentile ?? 95;
        var metric = new MetricRef(pctBase);

        // Windowed percentile from the tiered pcth histogram (§8.30); the recent (short) window gives fast-burn.
        var observed = await _metrics.GetPercentileAsync(metric, percentile, new MetricWindow(windowStart, DateTime.MaxValue), ct);
        var recent = await _metrics.GetPercentileAsync(metric, percentile, new MetricWindow(shortStart, DateTime.MaxValue), ct);

        var (att, budget, burnLong) = SloMath.EvaluateThreshold(observed, def.TargetValue);
        var (_, _, burnShort) = SloMath.EvaluateThreshold(recent, def.TargetValue);

        // A latency metric's smallest bucket bound is > 0, so observed == 0 means no samples matched (NoData),
        // not a real 0 ms percentile.
        return new EvalResult(att, budget, burnShort, burnLong, HasData: observed > 0);
    }

    private async Task<EvalResult> ComputeBacklogAsync(SloDefinition def, CancellationToken ct)
    {
        var depth = await _metrics.GetGaugeAsync(new MetricRef(QueueBacklogKeys.Total(def.Dimension, QueueBacklogKeys.DepthToken)), ct);
        var (att, budget, burn) = SloMath.EvaluateThreshold(depth ?? 0, def.TargetValue);

        // A depth of 0 is legitimately healthy (empty queue); a missing gauge (null) means the objective's queue
        // never emitted metrics — a misconfigured dimension, surfaced as NoData rather than green.
        return new EvalResult(att, budget, burn, burn, HasData: depth.HasValue);
    }

    // Sums a windowed history metric (via the metric seam) into (window-total, short-window-total). The seam
    // returns fine-resolution buckets over [windowStart, ∞); the short fast-burn window is the subset on/after shortStart.
    private async Task<(long Window, long Short)> WindowedSumAsync(string baseKey, DateTime windowStart, DateTime shortStart, CancellationToken ct)
    {
        var series = await _metrics.GetSeriesAsync(
            new SeriesQuery(new MetricRef(baseKey), new MetricWindow(windowStart, DateTime.MaxValue), MetricResolution.Fine, MetricAggregation.Sum), ct);

        long window = 0, shortWindow = 0;
        foreach (var bucket in series)
        {
            window += bucket.Value;
            if (bucket.BucketStart >= shortStart)
            {
                shortWindow += bucket.Value;
            }
        }

        return (window, shortWindow);
    }

    // The success-rate history base key (before the tier suffix) for a token, app-agnostic or the disjoint per-app
    // slice (§8.23) — built via the real key builder so format/sanitization matches how it was written.
    private static string JobstatHistoryBase(SloDefinition def, string token)
        => def.Application is null
            ? JobStatsKeys.History(JobStatsKeys.TypeMarker, def.Dimension, token, string.Empty)
            : JobStatsKeys.AppHistory(JobStatsKeys.Sanitize(def.Application), JobStatsKeys.TypeMarker, def.Dimension, token, string.Empty);

    // The deadline-attainment history base key for a token, app-agnostic or per-app.
    private static string DeadlineHistoryBase(SloDefinition def, string token)
        => def.Application is null
            ? DeadlineKeys.History(def.Dimension, token, string.Empty)
            : DeadlineKeys.AppHistory(DeadlineKeys.Sanitize(def.Application), def.Dimension, token, string.Empty);

    // Fast-burn short window = the objective window / 12, floored to 5 minutes — small enough to catch a fast
    // burn and now populated by the fine (5-min) metrics tier (§8.30), which is what makes real fast-burn possible.
    private static DateTime ShortWindowStart(SloDefinition def, DateTime now)
        => now.AddSeconds(-Math.Max(300, def.WindowSeconds / 12.0));

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

    // HasData is false when no observation matched the objective's dimension in the window — the caller maps that
    // to SloState.NoData so a misconfigured dimension is visible instead of a false-green Healthy.
    private sealed record EvalResult(double Attainment, double Budget, double BurnShort, double BurnLong, bool HasData = true);
}
