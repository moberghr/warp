using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Notifiers;
using Warp.Core.Services;
using Warp.Core.Slo;

namespace Warp.Worker.Services;

/// <summary>
/// Evaluates SLO objectives (§8.31) against the durable <c>Statistic</c>/<c>Counter</c> aggregates and upserts
/// their rolling <see cref="SloEvaluation"/> status — the actionable layer over the metrics jobstat / queue-wait /
/// backlog / deadline already fold. Runs off the worker hot path as a periodic <see cref="IServerTask"/> (the
/// analogue of <c>CounterAggregator</c> / <c>ErrorGroupAggregator</c>); reads only, plus the one status row per
/// objective. Rate objectives (success-rate, deadline-attainment) are windowed over the tiered history buckets
/// (fine / hourly / daily, §8.30); latency objectives read the tiered <c>pcth</c> percentile histogram over the
/// window; backlog reads the current gauge. Metric kinds 6–11 (ratio, threshold, volume floor, gap, latency, gauge threshold) read
/// <see cref="MetricPoint"/> rows across every tier overlapping the window (and baseline), prorated by overlap. A
/// healthy→breaching edge buffers a <see cref="SloBreachedEvent"/> (or <c>BacklogBreached</c> for a depth
/// objective), dispatched post-commit from <see cref="OnCommittedAsync"/> (§8.25).
/// </summary>
public sealed class SloEvaluator<TContext> : IServerTask
    where TContext : DbContext
{
    // Scale applied to prorated metric events before EvaluateRate's integer counts, and the largest total that
    // still fits a long once scaled.
    private const double RateScale = 1_000;
    private const double MaxRateEvents = 1e15;

    // The one "no observations" shape every arm reports, the unknown-kind arm included; the caller maps
    // HasData: false to NoData.
    private static EvalResult NoDataResult => new(1.0, 1.0, 0.0, 0.0, HasData: false);

    private readonly DbContext _context;
    private readonly WarpServerConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly WarpNotifierDispatcher _notifierDispatcher;
    private readonly ILogger<SloEvaluator<TContext>> _logger;
    private readonly List<WarpOperationalEvent> _pendingAlerts = [];
    private readonly Dictionary<MetricReadKey, List<MetricRawRow>> _metricReads = [];
    private readonly Dictionary<string, MetricKind?> _instrumentKinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _parsedTags = new(StringComparer.Ordinal);

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

    // Test seam: how many metric reads this instance served from the total-only (tier, bucket) shape.
    internal int TotalOnlyReads { get; private set; }

    public string Name => "EvaluateSlos";

    public string? LockKey => "warp:slo-eval";

    public TimeSpan? DefaultInterval => _configuration.SloEvaluationInterval;

    public bool RerunImmediately => false;

    public bool LogOnSuccess => false;

    public async Task<string?> ExecuteAsync(CancellationToken ct)
    {
        _pendingAlerts.Clear();
        _metricReads.Clear();
        _instrumentKinds.Clear();
        _parsedTags.Clear();

        var definitions = await _context.Set<SloDefinition>()
            .Where(x => x.Enabled)
            .ToListAsync(ct);

        if (definitions.Count == 0)
        {
            return null;
        }

        await LoadInstrumentKindsAsync(definitions, ct);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var existing = await _context.Set<SloEvaluation>().ToDictionaryAsync(x => x.SloDefinitionId, ct);

        // Each aggregate family (jobstat / qwait / qbacklog / deadline) is scanned at most once per tick and
        // reused across every objective that reads it.
        var cache = new Dictionary<string, IReadOnlyList<KeyVal>>(StringComparer.Ordinal);

        foreach (var def in definitions)
        {
            var result = await ComputeIsolatedAsync(def, now, cache, ct);

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

    // One objective's failure (corrupt stored data driving the arithmetic out of range, an unexpected value) must not
    // stop every other objective's evaluation: it is logged at Error with the objective's id and name and reported
    // as NoData, and the tick goes on. A database error is the exception: it aborts the task's transaction on
    // PostgreSQL, so the tick's save fails anyway and the next tick retries everything.
    private async Task<EvalResult> ComputeIsolatedAsync(SloDefinition def, DateTime now, Dictionary<string, IReadOnlyList<KeyVal>> cache, CancellationToken ct)
    {
        try
        {
            return await ComputeAsync(def, now, cache, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // The message stays type-only (an exception message can quote stored data); the stack goes to Debug.
            _logger.LogError(
                "SLO objective {Id} '{Name}' ({Kind}) failed to evaluate ({ExceptionType}) — reported as NoData; the other objectives were evaluated.",
                def.Id,
                def.Name,
                def.Kind,
                ex.GetType().FullName);
            _logger.LogDebug(ex, "SLO objective {Id} evaluation failure detail.", def.Id);

            return NoDataResult;
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
            SloKind.MetricRatio => ComputeMetricRatioAsync(def, now, ct),
            SloKind.MetricThreshold => ComputeMetricThresholdAsync(def, now, ct),
            SloKind.MetricVolumeFloor => ComputeMetricVolumeFloorAsync(def, now, ct),
            SloKind.MetricGap => ComputeMetricGapAsync(def, now, ct),
            SloKind.MetricLatency => ComputeMetricLatencyAsync(def, now, ct),
            SloKind.MetricGaugeThreshold => ComputeMetricGaugeThresholdAsync(def, now, ct),
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

        return Task.FromResult(NoDataResult);
    }

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

        return new EvalResult(att, budget, burnShort, burnLong, HasData: succW + failW > 0);
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

        return new EvalResult(att, budget, burnShort, burnLong, HasData: cntW > 0);
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
        var found = false;
        foreach (var row in await LoadMergedAsync(QueueBacklogKeys.Prefix + ":", cache, ct))
        {
            if (QueueBacklogKeys.TryParseTotal(row.Key, out var queue, out var token)
                && string.Equals(queue, def.Dimension, StringComparison.Ordinal)
                && string.Equals(token, QueueBacklogKeys.DepthToken, StringComparison.Ordinal))
            {
                depth = row.Value;
                found = true;
            }
        }

        var (att, budget, burn) = SloMath.EvaluateThreshold(depth, def.TargetValue);

        // A depth of 0 is legitimately healthy (empty queue), but no matching gauge at all means the objective's
        // queue name never emitted metrics — a misconfigured dimension, surfaced as NoData rather than green.
        return new EvalResult(att, budget, burn, burn, HasData: found);
    }

    // MetricRatio (§E): good = events whose RatioTag matches GoodValues, bad = events matching BadValues, every other
    // value ignored. With BaselineDays the target is the maximum drop against the same window that many days earlier.
    private async Task<EvalResult> ComputeMetricRatioAsync(SloDefinition def, DateTime now, CancellationToken ct)
    {
        if (!TryParseFilter(def, def.TagFilter, out var filter)
            || string.IsNullOrEmpty(def.RatioTag)
            || !MetricValueList.TryParse(def.GoodValues, out var good, out _)
            || !MetricValueList.TryParse(def.BadValues, out var bad, out _))
        {
            return CorruptDefinition(def);
        }

        var current = await LoadWindowAsync(def, def.Dimension, now, TimeSpan.Zero, byTags: true, ct);
        if (current is null)
        {
            return NoDataResult;
        }

        var ratioTag = def.RatioTag;

        // One pass per window: the filter is checked once per row, then the ratio tag's value is tested against both
        // lists (independently, as two separate predicates always were).
        (double Good, double Bad) GoodAndBad(MetricWindow window, DateTime from)
        {
            double goodEvents = 0;
            double badEvents = 0;
            foreach (var row in window.Rows)
            {
                var fraction = Overlap(row, from, window.To, window.Now);
                if (fraction > 0 && filter.Matches(row.Tags))
                {
                    var value = row.Tags.GetValueOrDefault(ratioTag);
                    if (good.Matches(value))
                    {
                        goodEvents += row.Events * fraction;
                    }

                    if (bad.Matches(value))
                    {
                        badEvents += row.Events * fraction;
                    }
                }
            }

            return (goodEvents, badEvents);
        }

        var (goodW, badW) = GoodAndBad(current, current.From);
        var (goodS, badS) = GoodAndBad(current, current.ShortFrom);

        // A ratio has two totals, so it refuses a negative one on either side (TryEvents' rule, applied to the pair).
        if (goodW < 0 || badW < 0)
        {
            return NegativeEvents(def);
        }

        if (goodW + badW <= 0)
        {
            return NoDataResult;
        }

        if (def.BaselineDays is not { } days)
        {
            return FromMath(Rate(goodW, badW, def.TargetValue), Rate(goodS, badS, def.TargetValue));
        }

        var previous = await LoadWindowAsync(def, def.Dimension, now, TimeSpan.FromDays(days), byTags: true, ct);
        if (previous is null)
        {
            return NoDataResult;
        }

        var (goodB, badB) = GoodAndBad(previous, previous.From);
        if (goodB + badB <= 0)
        {
            return NoDataResult;
        }

        var baselineRatio = goodB / (goodB + badB);
        var wide = SloMath.EvaluateRatioDrop(baselineRatio, goodW / (goodW + badW), def.TargetValue);
        var recent = goodS + badS > 0
            ? SloMath.EvaluateRatioDrop(baselineRatio, goodS / (goodS + badS), def.TargetValue)
            : SloMath.NoDataResult;

        return FromMath(wide, recent);
    }

    // MetricThreshold (§E): matching events must not exceed N. NoData only when the instrument has no rows at all in
    // the window — traffic with zero matches is Healthy at 0, so "5xx = 0" is not a false NoData.
    private async Task<EvalResult> ComputeMetricThresholdAsync(SloDefinition def, DateTime now, CancellationToken ct)
    {
        if (!TryParseFilter(def, def.TagFilter, out var filter))
        {
            return CorruptDefinition(def);
        }

        var window = await LoadWindowAsync(def, def.Dimension, now, TimeSpan.Zero, byTags: !filter.IsEmpty, ct);
        if (window is null || !HasRows(window))
        {
            return NoDataResult;
        }

        if (!TryEvents(window, filter.Matches, window.From, out var observed))
        {
            return NegativeEvents(def);
        }

        var observedShort = Events(window, filter.Matches, window.ShortFrom);

        return FromMath(SloMath.EvaluateCount(observed, def.TargetValue), SloMath.EvaluateCount(observedShort, def.TargetValue));
    }

    // MetricVolumeFloor (§E): matching events now vs the same window BaselineDays earlier, prorated by overlap.
    private async Task<EvalResult> ComputeMetricVolumeFloorAsync(SloDefinition def, DateTime now, CancellationToken ct)
    {
        if (!TryParseFilter(def, def.TagFilter, out var filter) || def.BaselineDays is not { } days)
        {
            return CorruptDefinition(def);
        }

        var current = await LoadWindowAsync(def, def.Dimension, now, TimeSpan.Zero, byTags: !filter.IsEmpty, ct);
        var previous = await LoadWindowAsync(def, def.Dimension, now, TimeSpan.FromDays(days), byTags: !filter.IsEmpty, ct);
        if (current is null || previous is null)
        {
            return NoDataResult;
        }

        if (!TryEvents(previous, filter.Matches, previous.From, out var baseline))
        {
            return NegativeEvents(def);
        }

        if (baseline <= 0)
        {
            return NoDataResult;
        }

        if (!TryEvents(current, filter.Matches, current.From, out var observed))
        {
            return NegativeEvents(def);
        }

        var observedShort = Events(current, filter.Matches, current.ShortFrom);
        var baselineShort = Events(previous, filter.Matches, previous.ShortFrom);
        var recent = baselineShort > 0
            ? SloMath.EvaluateVolumeFloor(observedShort, baselineShort, def.TargetValue)
            : SloMath.NoDataResult;

        return FromMath(SloMath.EvaluateVolumeFloor(observed, baseline, def.TargetValue), recent);
    }

    // MetricGap (§E): events(primary, TagFilter) − events(secondary, SecondaryTagFilter) must not exceed N. NoData when
    // neither instrument has rows in the window. A gap may legitimately be negative, so it does not use TryEvents.
    private async Task<EvalResult> ComputeMetricGapAsync(SloDefinition def, DateTime now, CancellationToken ct)
    {
        if (!TryParseFilter(def, def.TagFilter, out var filter)
            || !TryParseFilter(def, def.SecondaryTagFilter, out var secondaryFilter)
            || string.IsNullOrEmpty(def.SecondaryMetric))
        {
            return CorruptDefinition(def);
        }

        var primary = await LoadWindowAsync(def, def.Dimension, now, TimeSpan.Zero, byTags: !filter.IsEmpty, ct);
        var secondary = await LoadWindowAsync(def, def.SecondaryMetric, now, TimeSpan.Zero, byTags: !secondaryFilter.IsEmpty, ct);
        if (primary is null || secondary is null || (!HasRows(primary) && !HasRows(secondary)))
        {
            return NoDataResult;
        }

        var gap = Events(primary, filter.Matches, primary.From) - Events(secondary, secondaryFilter.Matches, secondary.From);
        var gapShort = Events(primary, filter.Matches, primary.ShortFrom) - Events(secondary, secondaryFilter.Matches, secondary.ShortFrom);

        return FromMath(SloMath.EvaluateCount(gap, def.TargetValue), SloMath.EvaluateCount(gapShort, def.TargetValue));
    }

    // MetricLatency: a histogram's events split at the threshold by bucket upper bound (SloMath.TrySplitAtThreshold),
    // then the good-event ratio against Percentile/100 — "p95 <= X" is "at most 5% of events slower than X", which is
    // the rate model's allowed error. Bucket CSVs cannot be summed in SQL, so the rows come back as stored and each is
    // split on its own ladder (rows with different bounds never need merging). NoData when no event in the window has
    // bucket detail.
    private async Task<EvalResult> ComputeMetricLatencyAsync(SloDefinition def, DateTime now, CancellationToken ct)
    {
        if (!TryParseFilter(def, def.TagFilter, out var filter) || def.Percentile is not { } percentile || percentile is < 1 or > 99)
        {
            return CorruptDefinition(def);
        }

        var from = now.AddSeconds(-def.WindowSeconds);
        var shortFrom = ShortWindowStart(def, now);
        var rows = await LoadRawAsync(def, def.Dimension, from, now, byTags: true, withBuckets: true, ct);
        if (rows is null)
        {
            return NoDataResult;
        }

        double goodW = 0, badW = 0, goodS = 0, badS = 0;
        var unreadable = 0;
        var bounds = new Dictionary<string, double[]?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var fraction = Overlap(row.Tier, row.BucketStart, from, now, now);
            if (fraction <= 0 || !filter.Matches(ParsedTags(row.Tags)))
            {
                continue;
            }

            if (!TrySplit(row, def.TargetValue, bounds, out var within, out var over))
            {
                unreadable++;

                continue;
            }

            goodW += within * fraction;
            badW += over * fraction;

            var recent = Overlap(row.Tier, row.BucketStart, shortFrom, now, now);
            goodS += within * recent;
            badS += over * recent;
        }

        if (unreadable > 0)
        {
            _logger.LogDebug("SLO objective {Id} '{Name}' skipped {Count} metric row(s) without readable bucket counts.", def.Id, def.Name, unreadable);
        }

        if (goodW + badW <= 0)
        {
            return NoDataResult;
        }

        var target = percentile / 100.0;

        return FromMath(Rate(goodW, badW, target), Rate(goodS, badS, target));
    }

    // MetricGaugeThreshold: the matching rows of each (tier, bucket) are combined - counts and sums add, extremes take
    // the min / max - which is exactly what the stored rows of several processes mean: the bucket average is the
    // sample-weighted mean across processes and series, never their total. Each bucket with samples then complies or
    // violates (SloMath.TryGaugeVerdict), weighted by the time it covers in the window, so an hourly row counts for an
    // hour and a fine row for five minutes; bad fraction = violating time / observed time, fed to the rate budget
    // against Percentile/100. NoData when no bucket in the window has samples.
    private async Task<EvalResult> ComputeMetricGaugeThresholdAsync(SloDefinition def, DateTime now, CancellationToken ct)
    {
        if (!TryParseFilter(def, def.TagFilter, out var filter) || def.Percentile is not { } percentile || percentile is < 1 or > 99)
        {
            return CorruptDefinition(def);
        }

        var from = now.AddSeconds(-def.WindowSeconds);
        var shortFrom = ShortWindowStart(def, now);
        var rows = await LoadRawAsync(def, def.Dimension, from, now, byTags: !filter.IsEmpty, withBuckets: false, ct);
        if (rows is null)
        {
            return NoDataResult;
        }

        var aggregate = def.GaugeAggregate ?? SloGaugeAggregate.Average;
        var comparison = def.Comparison ?? SloComparison.AtMost;
        double goodW = 0, badW = 0, goodS = 0, badS = 0;
        var buckets = rows
            .Where(x => filter.IsEmpty || filter.Matches(ParsedTags(x.Tags)))
            .GroupBy(x => (x.Tier, x.BucketStart));

        foreach (var bucket in buckets)
        {
            var (tier, start) = bucket.Key;
            var count = bucket.Sum(x => x.Count);
            var min = bucket.Aggregate((double?)null, (acc, x) => MetricBuckets.MinOf(acc, x.Min));
            var max = bucket.Aggregate((double?)null, (acc, x) => MetricBuckets.MaxOf(acc, x.Max));
            if (!SloMath.TryGaugeVerdict(aggregate, comparison, def.TargetValue, count, bucket.Sum(x => x.Sum), min, max, out var violates))
            {
                continue;
            }

            var wide = CoveredSeconds(tier, start, from, now, now);
            var recent = CoveredSeconds(tier, start, shortFrom, now, now);
            if (violates)
            {
                badW += wide;
                badS += recent;
            }
            else
            {
                goodW += wide;
                goodS += recent;
            }
        }

        if (goodW + badW <= 0)
        {
            return NoDataResult;
        }

        var target = percentile / 100.0;

        return FromMath(Rate(goodW, badW, target), Rate(goodS, badS, target));
    }

    // The metric maths return SloMath.NoDataResult (all NaN) for non-finite input or an overflowing result, and
    // Classify(NaN) would read Healthy — so NoData is decided HERE, before the shared classification. A non-finite
    // short window only zeroes the fast-burn signal.
    private static EvalResult FromMath((double Attainment, double BudgetRemaining, double BurnRate) wide, (double Attainment, double BudgetRemaining, double BurnRate) recent)
    {
        if (SloMath.IsNoData(wide))
        {
            return NoDataResult;
        }

        var burnShort = SloMath.IsNoData(recent) ? 0.0 : recent.BurnRate;

        return new EvalResult(wide.Attainment, wide.BudgetRemaining, burnShort, wide.BurnRate);
    }

    // EvaluateRate takes integer counts; prorated (or fractional counter) events are scaled so the ratio keeps its
    // precision. Negative or absurd totals (an up-down counter driven below zero) are NoData, never a rate.
    private static (double Attainment, double BudgetRemaining, double BurnRate) Rate(double good, double bad, double target)
    {
        var total = good + bad;
        if (good < 0 || bad < 0 || !double.IsFinite(total) || total > MaxRateEvents)
        {
            return SloMath.NoDataResult;
        }

        return SloMath.EvaluateRate((long)Math.Round(good * RateScale), (long)Math.Round(total * RateScale), target);
    }

    // The objective's window [now - WindowSeconds, now) and its fast-burn start, both shifted back by `shift` (zero for
    // the current window, BaselineDays for a baseline), with the instrument's rows over it. Null when the objective
    // cannot be evaluated against this instrument, which the caller reports as NoData.
    private async Task<MetricWindow?> LoadWindowAsync(SloDefinition def, string instrument, DateTime now, TimeSpan shift, bool byTags, CancellationToken ct)
    {
        var from = now.AddSeconds(-def.WindowSeconds) - shift;
        var rows = await LoadMetricAsync(def, instrument, from, now - shift, byTags, ct);

        return rows is null ? null : new MetricWindow(rows, from, ShortWindowStart(def, now) - shift, now - shift, now);
    }

    // Every tier overlapping [from, to) for one instrument (and the objective's application), summed in SQL per
    // (tier, bucket, tags) - or per (tier, bucket) alone when nothing reads the tags (an empty filter on a kind
    // other than ratio, the total-only shape MetricQueryService uses) - and cached for the tick. The range filter is
    // MetricPointQueries.InRange, shared with MetricQueryService and applied here to the server context's own set
    // (§2.14: the query service runs on the user's TContext). Rollup deletes a source in the transaction
    // that sums it into its parent, so a value lives in exactly one tier and nothing is counted twice. Returns null
    // when the objective cannot be evaluated against this instrument (a gauge, or rows the registry has not named
    // yet), which the caller reports as NoData.
    private async Task<List<MetricEventRow>?> LoadMetricAsync(SloDefinition def, string instrument, DateTime from, DateTime to, bool byTags, CancellationToken ct)
    {
        var raw = await LoadRawAsync(def, instrument, from, to, byTags, withBuckets: false, ct);
        if (raw is null)
        {
            return null;
        }

        // A non-empty read only gets past LoadRawAsync with a registry kind, which is what makes the event quantity known.
        if (raw.Count == 0 || await InstrumentKindAsync(instrument, ct) is not { } eventKind)
        {
            return [];
        }

        return raw.ConvertAll(x => new MetricEventRow(x.Tier, x.BucketStart, ParsedTags(x.Tags), MetricPointQueries.EventQuantity(eventKind, x.Count, x.Sum)));
    }

    // The raw (tier, bucket[, tags]) rows of one instrument over [from, to), or null when the objective cannot read it:
    // an instrument of a kind the objective does not accept (SloValidation.AcceptsInstrument), or rows the registry has
    // not named yet. With `withBuckets` the rows come back as stored, bucket CSVs included (they cannot be summed in SQL).
    private async Task<List<MetricRawRow>?> LoadRawAsync(SloDefinition def, string instrument, DateTime from, DateTime to, bool byTags, bool withBuckets, CancellationToken ct)
    {
        var kind = await InstrumentKindAsync(instrument, ct);
        if (kind is { } known && !SloValidation.AcceptsInstrument(def.Kind, known))
        {
            // Validation rejects the mismatch at upsert and seed; reaching here means the instrument changed kind since.
            LogUnreadableInstrument(def, instrument, known);

            return null;
        }

        var key = new MetricReadKey(instrument, def.Application, from, to, byTags, withBuckets);
        if (!_metricReads.TryGetValue(key, out var raw))
        {
            raw = await ReadMetricRowsAsync(instrument, def.Application, from, to, byTags, withBuckets, ct);
            _metricReads[key] = raw;
        }

        if (kind is null)
        {
            // The flusher writes the registry row in a second SaveChanges after the points, so rows without a kind
            // are a brief race; without the kind the event quantity (Sum vs Count) is unknown. A registry row that
            // never arrives (its upsert keeps failing, or it was cleaned up while points remain) would otherwise read
            // as NoData forever without a word, so say so — throttled, naming only the objective and instrument.
            if (raw.Count == 0)
            {
                return [];
            }

            if (SloEvaluatorWarnings.ShouldWarnUnregistered(def.Id, instrument, _timeProvider.GetUtcNow().UtcDateTime))
            {
                _logger.LogWarning(
                    "SLO objective {Id} '{Name}' reads instrument '{Instrument}', which has metric points but no instrument registry row, so its event quantity is unknown — reported as NoData until the registry row is written.",
                    def.Id,
                    def.Name,
                    instrument);
            }

            return null;
        }

        return raw;
    }

    private void LogUnreadableInstrument(SloDefinition def, string instrument, MetricKind known)
    {
        if (known == MetricKind.Gauge)
        {
            _logger.LogError(
                "SLO objective {Id} '{Name}' reads gauge instrument '{Instrument}', which has no event count — reported as NoData.",
                def.Id,
                def.Name,
                instrument);

            return;
        }

        _logger.LogError(
            "SLO objective {Id} '{Name}' ({Kind}) reads {InstrumentKind} instrument '{Instrument}', which this kind cannot evaluate — reported as NoData.",
            def.Id,
            def.Name,
            def.Kind,
            known,
            instrument);
    }

    private async Task<List<MetricRawRow>> ReadMetricRowsAsync(string instrument, string? application, DateTime from, DateTime to, bool byTags, bool withBuckets, CancellationToken ct)
    {
        // The same tier-bounded range filter as the dashboard read, on this task's own (server) context.
        var points = MetricPointQueries.InRange(
            _context.Set<MetricPoint>().AsNoTracking(),
            instrument,
            from,
            to,
            application,
            FineMinutes);

        if (withBuckets)
        {
            // As the dashboard's percentile read: the partial rows as stored, each split on its own ladder in memory.
            var stored = await points
                .Select(x =>
                    new
                    {
                        x.Tier,
                        x.BucketStart,
                        x.Tags,
                        x.Count,
                        x.Sum,
                        x.Min,
                        x.Max,
                        x.Bounds,
                        x.Buckets,
                    })
                .ToListAsync(ct);

            return stored.ConvertAll(x => new MetricRawRow(x.Tier, DateTime.SpecifyKind(x.BucketStart, DateTimeKind.Utc), x.Tags, x.Count, x.Sum, x.Min, x.Max, x.Bounds, x.Buckets));
        }

        if (!byTags)
        {
            TotalOnlyReads++;
        }

        // The same grouped projection as the dashboard read (MetricPointQueries.Summed), run on this context.
        var grouped = await MetricPointQueries.Summed(points, byTags).ToListAsync(ct);

        return grouped.ConvertAll(x => new MetricRawRow(x.Tier, DateTime.SpecifyKind(x.BucketStart, DateTimeKind.Utc), byTags ? x.Tags : MetricSeries.NoTags, x.Count, x.Sum, x.Min, x.Max, null, null));
    }

    // The registry kind of every instrument an enabled metric objective reads, loaded in one query at the start of the
    // tick into the per-tick cache. A name with no ordinal match is left out, so InstrumentKindAsync looks it up on its
    // own under the database collation, exactly as before (a case-insensitive SQL Server collation still resolves a
    // differently-cased name, and an unregistered name still reads as null).
    private async Task LoadInstrumentKindsAsync(List<SloDefinition> definitions, CancellationToken ct)
    {
        var names = definitions
            .Where(x => SloValidation.IsMetricKind(x.Kind))
            .SelectMany(x => new[] { x.Dimension, x.Kind == SloKind.MetricGap ? x.SecondaryMetric : null })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (names.Count == 0)
        {
            return;
        }

        var kinds = await _context.Set<MetricInstrument>()
            .AsNoTracking()
            .Where(x => names.Contains(x.Name))
            .Select(x =>
                new
                {
                    x.Name,
                    x.Kind,
                })
            .ToListAsync(ct);

        foreach (var row in kinds)
        {
            _instrumentKinds[row.Name] = row.Kind;
        }
    }

    private async Task<MetricKind?> InstrumentKindAsync(string instrument, CancellationToken ct)
    {
        if (_instrumentKinds.TryGetValue(instrument, out var cached))
        {
            return cached;
        }

        var kind = await _context.Set<MetricInstrument>()
            .AsNoTracking()
            .Where(x => x.Name == instrument)
            .Select(x => (MetricKind?)x.Kind)
            .FirstOrDefaultAsync(ct);
        _instrumentKinds[instrument] = kind;

        return kind;
    }

    private Dictionary<string, string> ParsedTags(string canonical)
    {
        if (!_parsedTags.TryGetValue(canonical, out var tags))
        {
            tags = MetricFilter.ParseCanonicalTags(canonical);
            _parsedTags[canonical] = tags;
        }

        return tags;
    }

    // Events of the window's rows matching the predicate, each weighted by how much of its bucket overlaps
    // [from, window.To). The bucket still being written (it straddles `now`) holds only events up to now, so its width
    // is clipped at now before prorating — otherwise the newest, partial bucket would be scaled down a second time.
    private double Events(MetricWindow window, Func<IReadOnlyDictionary<string, string>, bool> matches, DateTime from)
    {
        double total = 0;
        foreach (var row in window.Rows)
        {
            var fraction = Overlap(row, from, window.To, window.Now);
            if (fraction > 0 && matches(row.Tags))
            {
                total += row.Events * fraction;
            }
        }

        return total;
    }

    // Events, refusing a total below zero (an up-down counter driven negative is not a count of events; the caller
    // reports it through NegativeEvents). The gap arm does not use it: a gap may legitimately be negative.
    private bool TryEvents(MetricWindow window, Func<IReadOnlyDictionary<string, string>, bool> matches, DateTime from, out double events)
    {
        events = Events(window, matches, from);

        return events is not < 0;
    }

    private bool HasRows(MetricWindow window)
        => window.Rows.Exists(x => Overlap(x, window.From, window.To, window.Now) > 0);

    private double Overlap(MetricEventRow row, DateTime from, DateTime to, DateTime now)
        => Overlap(row.Tier, row.BucketStart, from, to, now);

    private double Overlap(MetricTier tier, DateTime bucketStart, DateTime from, DateTime to, DateTime now)
        => MetricWindows.Overlap(tier, bucketStart, from, to, now, FineMinutes);

    // Seconds of [from, to) a bucket covers, its width clipped at now like Overlap's (MetricWindows, shared with the
    // metrics health board).
    private double CoveredSeconds(MetricTier tier, DateTime bucketStart, DateTime from, DateTime to, DateTime now)
        => MetricWindows.CoveredSeconds(tier, bucketStart, from, to, now, FineMinutes);

    // A row's bucket counts split at the threshold; false for a row without bucket detail (the rollup drops it when a
    // sum would not fit the column) or with a corrupt bounds/counts CSV. Bounds are parsed once per distinct ladder.
    private static bool TrySplit(MetricRawRow row, double threshold, Dictionary<string, double[]?> boundsCache, out long within, out long over)
    {
        within = 0;
        over = 0;
        if (row.Bounds is null || row.Buckets is null)
        {
            return false;
        }

        if (!boundsCache.TryGetValue(row.Bounds, out var bounds))
        {
            bounds = MetricBuckets.TryParseBounds(row.Bounds, out var parsed) ? parsed : null;
            boundsCache[row.Bounds] = bounds;
        }

        return bounds is not null
            && MetricBuckets.TryParseCounts(row.Buckets, bounds.Length + 1, out var counts)
            && SloMath.TrySplitAtThreshold(bounds, counts, threshold, out within, out over);
    }

    private int FineMinutes => Math.Max(1, _configuration.FineResolutionMinutes);

    private bool TryParseFilter(SloDefinition def, string? text, out MetricFilter filter)
    {
        if (MetricFilter.TryParse(text, out filter, out var error))
        {
            return true;
        }

        _logger.LogError("SLO objective {Id} '{Name}' has an invalid tag filter: {Error} — reported as NoData.", def.Id, def.Name, error);

        return false;
    }

    // An event total below zero (an up-down counter driven negative) is not a count of events: NoData, never a rate
    // or a count. Debug only — it is a property of the data, not a fault, and it recurs every tick while it lasts.
    private EvalResult NegativeEvents(SloDefinition def)
    {
        _logger.LogDebug("SLO objective {Id} '{Name}' ({Kind}) saw a negative event total — reported as NoData.", def.Id, def.Name, def.Kind);

        return NoDataResult;
    }

    // A stored definition that no longer parses (edited past validation, or version skew): loud, and NoData.
    private EvalResult CorruptDefinition(SloDefinition def)
    {
        _logger.LogError("SLO objective {Id} '{Name}' ({Kind}) is missing a required field — reported as NoData.", def.Id, def.Name, def.Kind);

        return NoDataResult;
    }

    // The breach event's description of what a metric objective reads. Built only from the objective's own
    // definition (instrument names, filters, value lists), never from a tag value observed in the data (§1.2).
    private static string? MetricLabel(SloDefinition def)
    {
        var baseline = def.BaselineDays is { } days ? $" vs {days}d baseline" : string.Empty;

        return def.Kind switch
        {
            SloKind.MetricRatio => $"{def.Dimension}{FilterLabel(def.TagFilter)} ({def.RatioTag}: {def.GoodValues} / {def.BadValues}){baseline}",
            SloKind.MetricThreshold => $"{def.Dimension}{FilterLabel(def.TagFilter)}",
            SloKind.MetricVolumeFloor => $"{def.Dimension}{FilterLabel(def.TagFilter)}{baseline}",
            SloKind.MetricGap => $"{def.Dimension}{FilterLabel(def.TagFilter)} - {def.SecondaryMetric}{FilterLabel(def.SecondaryTagFilter)}",
            SloKind.MetricLatency => string.Create(CultureInfo.InvariantCulture, $"{def.Dimension}{FilterLabel(def.TagFilter)} p{def.Percentile} <= {def.TargetValue}"),
            SloKind.MetricGaugeThreshold => string.Create(CultureInfo.InvariantCulture, $"{def.Dimension}{FilterLabel(def.TagFilter)} {GaugeAggregateLabel(def.GaugeAggregate)} {(def.Comparison == SloComparison.AtLeast ? ">=" : "<=")} {def.TargetValue} in {def.Percentile}% of the window"),
            _ => null,
        };
    }

    private static string GaugeAggregateLabel(SloGaugeAggregate? aggregate) => aggregate switch
    {
        SloGaugeAggregate.Min => "min",
        SloGaugeAggregate.Max => "max",
        _ => "avg",
    };

    private static string FilterLabel(string? filter)
        => string.IsNullOrWhiteSpace(filter) ? string.Empty : $" [{filter}]";

    // Windowed threshold latency: observed = percentile over the window's pcth buckets; burnShort = percentile
    // over the recent (fine) buckets, so a latency spike in the fast-burn window is caught.
    private static EvalResult ThresholdWindowed(Dictionary<int, long> wide, Dictionary<int, long> recent, SloDefinition def)
    {
        var percentile = def.Percentile ?? 95;
        var observed = SloMath.Percentile(wide, percentile);
        var (att, budget, burnLong) = SloMath.EvaluateThreshold(observed, def.TargetValue);
        var (_, _, burnShort) = SloMath.EvaluateThreshold(SloMath.Percentile(recent, percentile), def.TargetValue);

        return new EvalResult(att, budget, burnShort, burnLong, HasData: wide.Values.Sum() > 0);
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
            Metric = MetricLabel(def),
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

    private readonly record struct MetricReadKey(string Instrument, string? Application, DateTime From, DateTime To, bool ByTags, bool WithBuckets);

    // Bounds and Buckets are only set on the with-buckets shape.
    private sealed record MetricRawRow(MetricTier Tier, DateTime BucketStart, string Tags, long Count, double Sum, double? Min, double? Max, string? Bounds, string? Buckets);

    // One objective window's rows and bounds: [From, To) with its fast-burn start, read at `Now` (To differs from Now
    // only for a baseline window).
    private sealed record MetricWindow(List<MetricEventRow> Rows, DateTime From, DateTime ShortFrom, DateTime To, DateTime Now);

    // Events = Sum for counters and up-down counters, Count for histograms (§E).
    private sealed record MetricEventRow(MetricTier Tier, DateTime BucketStart, IReadOnlyDictionary<string, string> Tags, double Events);

    // HasData is false when no observation matched the objective's dimension in the window — the caller maps that
    // to SloState.NoData so a misconfigured dimension is visible instead of a false-green Healthy.
    private sealed record EvalResult(double Attainment, double Budget, double BurnShort, double BurnLong, bool HasData = true);
}

/// <summary>
/// Throttle state for the evaluator's recurring warnings. The evaluator is scoped (a fresh instance per tick), so the
/// state lives here, outside the generic type (one copy per process, not per <c>TContext</c>).
/// </summary>
internal static class SloEvaluatorWarnings
{
    internal static readonly TimeSpan UnregisteredInterval = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<(int ObjectiveId, string Instrument), DateTime> LastUnregistered = new();

    /// <summary>True at most once per <see cref="UnregisteredInterval"/> per (objective, instrument).</summary>
    public static bool ShouldWarnUnregistered(int objectiveId, string instrument, DateTime now)
    {
        var key = (objectiveId, instrument);
        while (true)
        {
            if (!LastUnregistered.TryGetValue(key, out var last))
            {
                if (LastUnregistered.TryAdd(key, now))
                {
                    return true;
                }

                continue;
            }

            if (now - last < UnregisteredInterval)
            {
                return false;
            }

            if (LastUnregistered.TryUpdate(key, now, last))
            {
                return true;
            }
        }
    }
}
