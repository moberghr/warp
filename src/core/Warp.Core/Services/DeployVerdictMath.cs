using System.Globalization;
using Warp.Core.Enums;
using Warp.Core.Metrics;
using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>
/// The pure half of the post-deploy verdict: picking the deploy out of the history, the window, turning board tiles and SLOs
/// into checks, and the aggregation. No I/O, so every rule is unit-tested without a database.
/// </summary>
internal static class DeployVerdictMath
{
    private const double CoverageTolerance = 1e-9;

    private const int DefaultPercentile = 95;

    /// <summary>The deploy, its predecessor and its successor in the same environment.</summary>
    internal sealed record ResolvedDeploy(ApplicationDeployModel Deploy, ApplicationDeployModel? Previous, ApplicationDeployModel? Next);

    /// <summary>
    /// Finds the deploy to judge in one application's history (any order): the newest one matching the version (when given)
    /// and the environment (when given), plus its neighbours in that deploy's environment. Null when nothing matches.
    /// </summary>
    public static ResolvedDeploy? Resolve(IReadOnlyList<ApplicationDeployModel> deploys, string? version, string? environment)
    {
        var ordered = deploys
            .OrderByDescending(x => x.FirstSeenAt)
            .ThenBy(x => x.Version, StringComparer.Ordinal)
            .ToList();
        var deploy = ordered
            .Where(x => version is null || string.Equals(x.Version, version, StringComparison.Ordinal))
            .FirstOrDefault(x => environment is null || string.Equals(x.Environment ?? string.Empty, environment, StringComparison.Ordinal));
        if (deploy is null)
        {
            return null;
        }

        var index = ordered.IndexOf(deploy);
        var previous = ordered
            .Skip(index + 1)
            .FirstOrDefault(x => SameEnvironment(x, deploy));
        var next = ordered
            .Take(index)
            .LastOrDefault(x => SameEnvironment(x, deploy));

        return new ResolvedDeploy(deploy, previous, next);
    }

    /// <summary>
    /// The evaluation window (§8.23). A verdict judges only complete fine buckets that start at or after the deploy: the
    /// mixed bucket holding the deploy carries pre-deploy traffic, so the window starts at <paramref name="deployedAt"/>
    /// rounded UP to the next fine bucket boundary. It ends where the board cuts its data (the start of the fine bucket
    /// holding <c>now − flushInterval</c>), or, for a superseded deploy, at the next deploy rounded DOWN to a boundary
    /// (whichever is earlier). It is at most <see cref="IMetricQueryService.MaxRangeDays"/> long.
    /// <para>
    /// A live deploy is pending until that clean window is at least <paramref name="minWindow"/> long (and holds at least
    /// one bucket, so a zero minWindow still waits for one): <c>SettlesAt</c> is the end of the bucket that completes the
    /// minimum, plus <paramref name="flushInterval"/> for its last measurements to be written. A superseded deploy keeps
    /// the settled-over-its-lifetime rule (its window can never grow, so waiting for minWindow would leave it pending
    /// forever): it is judged once its last bucket is complete, or straight away when its clean window is empty (the
    /// verdict is then noData, never pending).
    /// </para>
    /// </summary>
    public static DeployVerdictWindow Window(DateTime deployedAt, DateTime? supersededAt, DateTime now, TimeSpan minWindow, int fineMinutes, TimeSpan flushInterval)
    {
        fineMinutes = Math.Max(1, fineMinutes);
        var flush = flushInterval > TimeSpan.Zero ? flushInterval : TimeSpan.Zero;
        var cleanStart = CeilToBucket(deployedAt, fineMinutes);
        var completeUntil = MetricTiers.BucketStart(MetricTier.Fine, now - flush, fineMinutes);

        DateTime end;
        DateTime settlesAt;
        if (supersededAt is { } next)
        {
            end = MetricTiers.BucketStart(MetricTier.Fine, next, fineMinutes);
            settlesAt = end > cleanStart ? end + flush : next;
        }
        else
        {
            var required = CeilToBucket(cleanStart + minWindow, fineMinutes);
            end = DateTime.MaxValue;
            settlesAt = (required > cleanStart ? required : cleanStart.AddMinutes(fineMinutes)) + flush;
        }

        var to = end < completeUntil ? end : completeUntil;
        if (to < cleanStart)
        {
            to = cleanStart;
        }

        var maxLength = TimeSpan.FromDays(IMetricQueryService.MaxRangeDays);
        var emptySuperseded = supersededAt is not null && end <= cleanStart;

        return new DeployVerdictWindow
        {
            From = to - cleanStart > maxLength ? to - maxLength : cleanStart,
            To = to,
            MinWindowSeconds = (int)minWindow.TotalSeconds,
            SettlesAt = settlesAt,
            Settled = emptySuperseded || now >= settlesAt,
        };
    }

    /// <summary>A tile or SLO with no application covers every application; a scoped one counts only for its own.</summary>
    public static bool AppliesTo(string? scope, string application)
    {
        var normalized = MetricPointQueries.NormalizeApplication(scope);

        return normalized is null || string.Equals(normalized, MetricPointQueries.NormalizeApplication(application), StringComparison.Ordinal);
    }

    /// <summary>A board tile as a check; null when the tile has no status rule (nothing to judge).</summary>
    public static DeployVerdictCheck? TileCheck(MetricBoardTileResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Status is not { } state)
        {
            return null;
        }

        var tile = result.Tile;
        var unacknowledged = result.UnacknowledgedStatus ?? state;
        var deviation = tile.StatusRule == MetricTileStatusRule.Deviation;
        var baseline = deviation ? Baseline(result) : null;
        var target = deviation ? tile.MaxDeviation : tile.Threshold;

        return new DeployVerdictCheck
        {
            Source = DeployVerdictSources.Tile,
            Id = tile.Id,
            Name = tile.Name,
            Application = tile.Application,
            State = Token(state),
            UnacknowledgedState = state == SloState.Acknowledged ? Token(unacknowledged) : null,
            Acknowledged = state == SloState.Acknowledged,
            Value = Finite(result.Current),
            Baseline = Finite(baseline),
            Target = Finite(target),
            Reason = TileReason(result, state, unacknowledged, baseline),
        };
    }

    /// <summary>
    /// The numeric target an SLO check reports, on the same scale as its value (the attainment). For most kinds that is the
    /// objective's target value; a metric latency or gauge threshold objective's attainment is a SHARE (of events within the
    /// threshold, of observed time compliant), so its target is the required share, <c>percentile / 100</c>, and the
    /// threshold travels in <see cref="DeployVerdictCheck.Threshold"/> and the reason.
    /// </summary>
    public static double SloTarget(SloObjectiveModel slo)
    {
        ArgumentNullException.ThrowIfNull(slo);

        return SharesWithinThreshold(slo.Kind) ? (slo.Percentile ?? DefaultPercentile) / 100.0 : slo.TargetValue;
    }

    /// <summary>
    /// The observed side of an SLO reason, worded like the SLO page (<c>sloDisplay.formatObserved</c>): a percentage for the
    /// rate kinds, a share of the baseline volume, a count, milliseconds, a ratio drop in points, or a share within the
    /// threshold.
    /// </summary>
    public static string SloObserved(SloObjectiveModel slo)
    {
        ArgumentNullException.ThrowIfNull(slo);

        return slo.Kind switch
        {
            SloKind.MetricGaugeThreshold => $"{Percent(slo.Attainment)} compliant",
            SloKind.MetricLatency => $"{Percent(slo.Attainment)} ≤ {Plain(slo.TargetValue)}",
            SloKind.MetricRatio when slo.BaselineDays is not null => $"a drop of {Points(slo.Attainment)}",
            SloKind.MetricVolumeFloor => $"{Percent(slo.Attainment)} of baseline",
            _ => ObjectiveValue(slo.Kind, slo.Attainment),
        };
    }

    /// <summary>The target side of an SLO reason, worded like the SLO page (<c>sloDisplay.formatTarget</c>).</summary>
    public static string SloTargetText(SloObjectiveModel slo)
    {
        ArgumentNullException.ThrowIfNull(slo);

        var percentile = (slo.Percentile ?? DefaultPercentile).ToString(CultureInfo.InvariantCulture);

        return slo.Kind switch
        {
            SloKind.MetricGaugeThreshold
                => $"{GaugeAggregate(slo.GaugeAggregate)} {(slo.Comparison == SloComparison.AtLeast ? "≥" : "≤")} {Plain(slo.TargetValue)} in {percentile}%",
            SloKind.MetricLatency => $"p{percentile} ≤ {Plain(slo.TargetValue)}",
            SloKind.MetricRatio when slo.BaselineDays is { } days
                => $"a drop of at most {Points(slo.TargetValue)} from the {days.ToString(CultureInfo.InvariantCulture)}-day baseline",
            SloKind.MetricVolumeFloor => $"≥ {Percent(slo.TargetValue)} of baseline",
            _ => ObjectiveValue(slo.Kind, slo.TargetValue),
        };
    }

    /// <summary>
    /// An SLO as a check, from its latest evaluation. An objective never evaluated, or last evaluated before the verdict
    /// window starts (<paramref name="windowStart"/>: the deploy's first complete bucket), has no data about this release yet.
    /// </summary>
    /// <remarks>
    /// Only the evaluation time can be held to the clean window. The SLO's state is computed over its own rolling window
    /// (hours to days), which always holds pre-deploy traffic as well; the verdict cannot re-cut it without re-evaluating the
    /// objective, so an SLO weighs the release's traffic among older traffic, as it does on the SLO page.
    /// </remarks>
    public static (DeployVerdictCheck Check, SloState State) SloCheck(SloObjectiveModel slo, DateTime windowStart)
    {
        ArgumentNullException.ThrowIfNull(slo);

        var (state, reason) = slo switch
        {
            { Evaluated: false } => (SloState.NoData, "Not evaluated yet."),
            { LastEvaluatedAt: { } at } when at < windowStart
                => (SloState.NoData, $"Last evaluated at {Instant(at)}, before the deploy's first complete bucket ({Instant(windowStart)})."),
            _ => (slo.State, SloReason(slo)),
        };
        var evaluated = slo.Evaluated && state != SloState.NoData;

        var check = new DeployVerdictCheck
        {
            Source = DeployVerdictSources.Slo,
            Id = slo.Id,
            Name = slo.Name,
            Application = slo.Application,
            State = Token(state),
            Acknowledged = state == SloState.Acknowledged,
            Value = evaluated ? Finite(slo.Attainment) : null,
            Target = Finite(SloTarget(slo)),
            Threshold = SharesWithinThreshold(slo.Kind) ? Finite(slo.TargetValue) : null,
            BudgetRemaining = evaluated ? Finite(slo.BudgetRemaining) : null,
            Reason = reason,
        };

        return (check, state);
    }

    /// <summary>
    /// The verdict: pending until settled; then any Breaching fails, any Warning warns, any Healthy or Acknowledged passes,
    /// and nothing but NoData (or no checks at all) is noData. Acknowledged never fails or warns. When fewer than
    /// <paramref name="minCoverage"/> of the checks have data, a would-be pass or warn is noData (too little evidence either
    /// way); a fail stays a fail, since a breaching check is evidence whatever the others say.
    /// </summary>
    public static string Aggregate(IReadOnlyCollection<SloState> states, bool settled, double minCoverage = 0)
    {
        ArgumentNullException.ThrowIfNull(states);

        if (!settled)
        {
            return DeployVerdicts.Pending;
        }

        if (states.Contains(SloState.Breaching))
        {
            return DeployVerdicts.Fail;
        }

        if (!Covered(states, minCoverage))
        {
            return DeployVerdicts.NoData;
        }

        if (states.Contains(SloState.Warning))
        {
            return DeployVerdicts.Warn;
        }

        return states.Any(x => x is SloState.Healthy or SloState.Acknowledged) ? DeployVerdicts.Pass : DeployVerdicts.NoData;
    }

    /// <summary>True when the share of checks with data reaches <paramref name="minCoverage"/> (always true at 0).</summary>
    public static bool Covered(IReadOnlyCollection<SloState> states, double minCoverage)
    {
        ArgumentNullException.ThrowIfNull(states);

        if (minCoverage <= 0)
        {
            return true;
        }

        // Compare the share, with a tolerance: minCoverage * Count rounds up past an exact count (0.56 * 25 =
        // 14.000000000000002), so 14 of 25 would miss a 0.56 bar it meets.
        return states.Count > 0 && (double)WithData(states) / states.Count >= minCoverage - CoverageTolerance;
    }

    /// <summary>Assembles the verdict from the resolved deploy, the window and the inputs of the application.</summary>
    public static DeployVerdictModel Build(
        ResolvedDeploy resolved,
        DeployVerdictWindow window,
        IReadOnlyList<MetricBoardTileResult> tiles,
        IReadOnlyList<SloObjectiveModel> slos,
        double minCoverage = 0)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(slos);

        var deploy = resolved.Deploy;
        var checks = new List<(DeployVerdictCheck Check, SloState State)>();
        foreach (var tile in tiles.Where(x => AppliesTo(x.Tile.Application, deploy.Application)))
        {
            if (TileCheck(tile) is { } check)
            {
                checks.Add((check, tile.Status!.Value));
            }
        }

        checks.AddRange(slos
            .Where(x => x.Enabled)
            .Where(x => AppliesTo(x.Application, deploy.Application))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => SloCheck(x, window.From)));

        var states = checks.ConvertAll(x => x.State);
        var verdict = Aggregate(states, window.Settled, minCoverage);

        return new DeployVerdictModel
        {
            Verdict = verdict,
            Summary = Summary(verdict, states, window, minCoverage),
            ChecksWithData = WithData(states),
            ChecksTotal = states.Count,
            Deploy = new DeployVerdictDeploy
            {
                Application = deploy.Application,
                Version = deploy.Version,
                Environment = deploy.Environment,
                FirstSeenAt = deploy.FirstSeenAt,
                InstanceName = deploy.InstanceName,
                PreviousVersion = resolved.Previous?.Version,
                SupersededBy = resolved.Next?.Version,
            },
            Window = window,
            Checks = checks.ConvertAll(x => x.Check),
        };
    }

    /// <summary>The wire token of a state: <c>healthy</c>, <c>warning</c>, <c>breaching</c>, <c>acknowledged</c> or <c>noData</c>.</summary>
    public static string Token(SloState state) => state switch
    {
        SloState.Healthy => "healthy",
        SloState.Warning => "warning",
        SloState.Breaching => "breaching",
        SloState.Acknowledged => "acknowledged",
        _ => "noData",
    };

    internal static string Summary(string verdict, IReadOnlyCollection<SloState> states, DeployVerdictWindow window, double minCoverage = 0)
    {
        if (string.Equals(verdict, DeployVerdicts.Pending, StringComparison.Ordinal))
        {
            return $"pending: settles at {Instant(window.SettlesAt)}";
        }

        // Only a superseded deploy settles with an empty window: it was replaced before a complete bucket after it.
        if (window.Settled && window.To <= window.From)
        {
            return $"{verdict}: superseded at {Instant(window.SettlesAt)}, before a complete metric bucket after the deploy; nothing to judge";
        }

        var coverage = $"{WithData(states).ToString(CultureInfo.InvariantCulture)} of {states.Count.ToString(CultureInfo.InvariantCulture)} checks had data";
        if (states.Count == 0)
        {
            return $"{verdict} ({coverage}): no board tile with a rule and no SLO covers this application";
        }

        if (!Covered(states, minCoverage) && !string.Equals(verdict, DeployVerdicts.Fail, StringComparison.Ordinal))
        {
            coverage += $", below minCoverage {minCoverage.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        var parts = new[] { SloState.Breaching, SloState.Warning, SloState.Acknowledged, SloState.Healthy, SloState.NoData }
            .Select(x => (State: x, Count: states.Count(y => y == x)))
            .Where(x => x.Count > 0)
            .Select(x => $"{x.Count.ToString(CultureInfo.InvariantCulture)} {Phrase(x.State)}");

        return $"{verdict} ({coverage}): {string.Join(", ", parts)}";
    }

    private static string TileReason(MetricBoardTileResult result, SloState state, SloState unacknowledged, double? baseline)
    {
        var tile = result.Tile;
        if (unacknowledged == SloState.NoData)
        {
            return result.InstrumentKind is null
                ? $"Instrument '{tile.Instrument}' is not in the metric registry."
                : "No data in the window or its baseline.";
        }

        var detail = tile.StatusRule == MetricTileStatusRule.Deviation
            ? $"{Number(result.Current)} vs {(tile.BaselineDays ?? 7).ToString(CultureInfo.InvariantCulture)}-day baseline {Number(baseline)} (max {Direction(tile.DeviationDirection)} {Number(tile.MaxDeviation)})"
            : $"{Number(result.Current)} vs {Bound(tile.Comparison)} {Number(tile.Threshold)}";

        return state == SloState.Acknowledged
            ? $"Acknowledged noise (would be {Phrase(unacknowledged)}): {detail}."
            : $"{Capitalized(Phrase(state))}: {detail}.";
    }

    private static string SloReason(SloObjectiveModel slo)
    {
        var detail = $"observed {SloObserved(slo)} vs target {SloTargetText(slo)}, budget remaining {Budget(slo.BudgetRemaining)}";

        return slo.State == SloState.Acknowledged && slo.AcknowledgedUntil is { } until
            ? $"Acknowledged until {Instant(until)}: {detail}."
            : $"{Capitalized(Phrase(slo.State))}: {detail}.";
    }

    private static DateTime CeilToBucket(DateTime utc, int fineMinutes)
    {
        var floor = MetricTiers.BucketStart(MetricTier.Fine, utc, fineMinutes);

        return floor == utc ? floor : floor.AddMinutes(fineMinutes);
    }

    private static int WithData(IReadOnlyCollection<SloState> states) => states.Count(x => x != SloState.NoData);

    private static bool SameEnvironment(ApplicationDeployModel a, ApplicationDeployModel b)
        => string.Equals(a.Environment ?? string.Empty, b.Environment ?? string.Empty, StringComparison.Ordinal);

    private static string Phrase(SloState state) => state switch
    {
        SloState.NoData => "no data",
        _ => Token(state),
    };

    private static double? Baseline(MetricBoardTileResult result) => result.Tile.BaselineDays == 1 ? result.DayAgo : result.WeekAgo;

    private static string Bound(SloComparison? comparison) => comparison == SloComparison.AtLeast ? "at least" : "at most";

    private static string Direction(MetricDeviationDirection? direction) => direction switch
    {
        MetricDeviationDirection.Drop => "drop",
        MetricDeviationDirection.Rise => "rise",
        _ => "change",
    };

    // The kinds whose attainment is the share of events (latency) or observed time (gauge) on the right side of a threshold.
    private static bool SharesWithinThreshold(SloKind kind) => kind is SloKind.MetricLatency or SloKind.MetricGaugeThreshold;

    // sloDisplay.formatObjectiveValue: rate kinds as a percentage, count kinds rounded, the job latency kinds in ms.
    private static string ObjectiveValue(SloKind kind, double value) => kind switch
    {
        SloKind.SuccessRate or SloKind.DeadlineAttainment or SloKind.MetricRatio or SloKind.MetricVolumeFloor => Percent(value),
        SloKind.MetricThreshold or SloKind.MetricGap or SloKind.BacklogDepth => Rounded(value),
        _ => $"{Rounded(value)} ms",
    };

    private static string GaugeAggregate(SloGaugeAggregate? aggregate) => aggregate switch
    {
        SloGaugeAggregate.Min => "min",
        SloGaugeAggregate.Max => "max",
        _ => "avg",
    };

    private static string Percent(double fraction)
        => double.IsFinite(fraction) ? $"{(fraction * 100).ToString("F2", CultureInfo.InvariantCulture)}%" : "n/a";

    private static string Points(double fraction)
        => double.IsFinite(fraction) ? $"{(fraction * 100).ToString("F2", CultureInfo.InvariantCulture)} pts" : "n/a";

    private static string Plain(double value) => double.IsFinite(value) ? value.ToString("G7", CultureInfo.InvariantCulture) : "n/a";

    // Math.round in the UI: halves round up (towards +∞), not to even.
    private static string Rounded(double value)
        => double.IsFinite(value) ? Math.Floor(value + 0.5).ToString("0", CultureInfo.InvariantCulture) : "n/a";

    // sloDisplay.formatBudget: the share of the error budget left, as a whole percentage (negative once overspent).
    private static string Budget(double value)
        => double.IsFinite(value) ? $"{Math.Floor((value * 100) + 0.5).ToString("0", CultureInfo.InvariantCulture)}%" : "n/a";

    private static string Capitalized(string text) => string.Concat(text[..1].ToUpperInvariant(), text[1..]);

    private static string Number(double? value) => Finite(value) is { } x ? x.ToString("G4", CultureInfo.InvariantCulture) : "n/a";

    private static string Instant(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // JSON cannot carry NaN or infinity; a non-finite value is "nothing to show".
    private static double? Finite(double? value) => value is { } x && double.IsFinite(x) ? x : null;
}
