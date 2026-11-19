namespace Warp.Core.Models;

/// <summary>The wire values of <see cref="DeployVerdictModel.Verdict"/>: strings, so a CI script can compare them directly.</summary>
public static class DeployVerdicts
{
    /// <summary>Every check with data is Healthy or Acknowledged.</summary>
    public const string Pass = "pass";

    /// <summary>At least one check is Warning and none is Breaching.</summary>
    public const string Warn = "warn";

    /// <summary>At least one check is Breaching.</summary>
    public const string Fail = "fail";

    /// <summary>
    /// The deploy is younger than the settle window and still the latest in its environment: ask again at
    /// <see cref="DeployVerdictWindow.SettlesAt"/>. A superseded deploy is never pending, since its window cannot grow.
    /// </summary>
    public const string Pending = "pending";

    /// <summary>
    /// No check counts, every one that does has no data, or fewer than the requested <c>minCoverage</c> share of the checks
    /// have data and none is breaching.
    /// </summary>
    public const string NoData = "noData";
}

/// <summary>
/// The post-deploy verdict: is release <see cref="DeployVerdictDeploy.Version"/> of an application healthy? Built from the
/// health-board tiles evaluated over <see cref="Window"/> and the current state of the application's SLOs.
/// </summary>
public sealed class DeployVerdictModel
{
    /// <summary>One of <see cref="DeployVerdicts"/>.</summary>
    public string Verdict { get; init; } = DeployVerdicts.NoData;

    /// <summary>A one-line summary for a CI log, e.g. <c>pass (2 of 9 checks had data): 2 healthy, 7 no data</c>.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// How many of <see cref="Checks"/> had data (any state but noData). A pass resting on few checks is weak evidence; a CI
    /// step can demand a share with the <c>minCoverage</c> query parameter.
    /// </summary>
    public int ChecksWithData { get; init; }

    /// <summary>The number of <see cref="Checks"/>.</summary>
    public int ChecksTotal { get; init; }

    /// <summary>
    /// The board whose tiles were judged, as stored (the <c>board</c> parameter matches it ignoring case), or null when the
    /// tiles of every board counted.
    /// </summary>
    public string? Board { get; init; }

    public DeployVerdictDeploy Deploy { get; init; } = new();

    public DeployVerdictWindow Window { get; init; } = new();

    /// <summary>Board tiles first (board order), then SLOs (by name).</summary>
    public IReadOnlyList<DeployVerdictCheck> Checks { get; init; } = [];
}

/// <summary>The deploy the verdict is about (§8.23 deploy history).</summary>
public sealed class DeployVerdictDeploy
{
    public string Application { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    /// <summary>Null when the deploying instance reported no environment.</summary>
    public string? Environment { get; init; }

    public DateTime FirstSeenAt { get; init; }

    public string? InstanceName { get; init; }

    /// <summary>The version deployed before this one in the same environment, when it is still in the deploy history.</summary>
    public string? PreviousVersion { get; init; }

    /// <summary>
    /// The version that replaced this one in the same environment, or null while it is the latest. A superseded deploy is
    /// judged over its own lifetime only, and without SLOs (their current state describes the newer release).
    /// </summary>
    public string? SupersededBy { get; init; }
}

/// <summary>
/// The window the board tiles were evaluated over: only complete fine buckets (<c>FineResolutionMinutes</c> wide) that
/// start after the deploy, so no pre-deploy traffic is judged (§8.23).
/// </summary>
public sealed class DeployVerdictWindow
{
    /// <summary>
    /// The deploy's <c>FirstSeenAt</c> rounded UP to the next fine bucket boundary: the mixed bucket holding the deploy is
    /// never counted. Moved forward only when the window would exceed the 90-day read limit.
    /// </summary>
    public DateTime From { get; init; }

    /// <summary>
    /// Where the board's complete data ends (the start of the fine bucket holding <c>now − MetricFlushInterval</c>), or, for
    /// a superseded deploy, the next deploy's <c>FirstSeenAt</c> rounded DOWN to a bucket boundary when that is earlier.
    /// Equal to <see cref="From"/> while no complete bucket follows the deploy.
    /// </summary>
    public DateTime To { get; init; }

    /// <summary>The settle time the caller asked for (<c>minWindowMinutes</c>), in seconds.</summary>
    public int MinWindowSeconds { get; init; }

    /// <summary>
    /// When the verdict stops being pending. Live deploy: when the clean window holds <c>minWindow</c> of complete data,
    /// i.e. <see cref="From"/> + minWindow rounded up to a bucket end (at least one bucket), plus <c>MetricFlushInterval</c>.
    /// Superseded deploy: when its last bucket is complete (the next deploy rounded down, plus the flush interval), or the
    /// next deploy's <c>FirstSeenAt</c> when it was replaced before a complete bucket (then the verdict is noData).
    /// </summary>
    public DateTime SettlesAt { get; init; }

    /// <summary>False while the verdict is pending.</summary>
    public bool Settled { get; init; }
}

/// <summary>The wire values of <see cref="DeployVerdictCheck.Source"/>.</summary>
public static class DeployVerdictSources
{
    public const string Tile = "tile";

    public const string Slo = "slo";
}

/// <summary>One input to the verdict: a health-board tile or an SLO.</summary>
public sealed class DeployVerdictCheck
{
    /// <summary>One of <see cref="DeployVerdictSources"/>.</summary>
    public string Source { get; init; } = DeployVerdictSources.Tile;

    /// <summary>The tile id or the SLO id.</summary>
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>The application the tile or SLO is scoped to; null when it covers every application.</summary>
    public string? Application { get; init; }

    /// <summary><c>healthy</c>, <c>warning</c>, <c>breaching</c>, <c>acknowledged</c> or <c>noData</c>.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>For an acknowledged tile, the state its rule gives (warning or breaching); null otherwise.</summary>
    public string? UnacknowledgedState { get; init; }

    public bool Acknowledged { get; init; }

    /// <summary>
    /// The tile's value over the window, or the SLO's observed value (its attainment): a share for the rate kinds and for a
    /// metric latency (events within the threshold) or gauge threshold (observed time compliant) objective, else the
    /// observed latency, count, ratio or drop.
    /// </summary>
    public double? Value { get; init; }

    /// <summary>A deviation tile's baseline (the same window 1 or 7 days earlier).</summary>
    public double? Baseline { get; init; }

    /// <summary>
    /// A threshold tile's threshold, a deviation tile's allowed deviation, or the SLO's target on the same scale as
    /// <see cref="Value"/>: for a metric latency or gauge threshold objective that is the required share
    /// (<c>percentile / 100</c>), and the threshold itself is in <see cref="Threshold"/>.
    /// </summary>
    public double? Target { get; init; }

    /// <summary>
    /// A metric latency or gauge threshold SLO only: the threshold (in the instrument's unit) the share in <see cref="Value"/>
    /// is measured against. Null for every other check.
    /// </summary>
    public double? Threshold { get; init; }

    /// <summary>SLOs only: the fraction of error budget left (negative when the objective is missed).</summary>
    public double? BudgetRemaining { get; init; }

    public string Reason { get; init; } = string.Empty;
}
