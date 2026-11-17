using System.Globalization;
using Warp.Core.Metrics;

namespace Warp.Core.Services;

/// <summary>What <see cref="IDeployVerdictService.GetVerdict"/> judges: an application's deploy, optionally narrowed by version and environment.</summary>
public sealed class DeployVerdictQuery
{
    /// <summary>The settle time used when the caller gives none.</summary>
    public const int DefaultMinWindowMinutes = 5;

    /// <summary>The longest settle time a caller may ask for (one day).</summary>
    public const int MaxMinWindowMinutes = 24 * 60;

    /// <summary>The longest application, version or environment accepted (the <c>application_deploy</c> column length).</summary>
    public const int MaxNameLength = 200;

    /// <summary>The application name (not the URL-safe id).</summary>
    public required string Application { get; init; }

    /// <summary>The version to judge; null judges the latest deploy.</summary>
    public string? Version { get; init; }

    /// <summary>The environment to look in; null looks in every environment and takes the newest match.</summary>
    public string? Environment { get; init; }

    /// <summary>
    /// The metric board whose tiles count, by name (ignoring case); null counts the tiles of every board, still filtered
    /// by the application scope. SLOs are unaffected either way.
    /// </summary>
    public string? Board { get; init; }

    /// <summary>A deploy younger than this gets <c>pending</c> instead of a premature verdict.</summary>
    public TimeSpan MinWindow { get; init; } = TimeSpan.FromMinutes(DefaultMinWindowMinutes);

    /// <summary>
    /// The share of checks (0 to 1) that must have data for a pass or warn; below it the verdict is <c>noData</c>. Default 0:
    /// any check with data is enough.
    /// </summary>
    public double MinCoverage { get; init; }

    /// <summary>
    /// Builds a query from raw query-string values: <paramref name="application"/> is required, blank optional values count
    /// as absent, <paramref name="minWindowMinutes"/> must be a whole number from 0 to <see cref="MaxMinWindowMinutes"/> and
    /// <paramref name="minCoverage"/> a number from 0 to 1.
    /// </summary>
    public static bool TryCreate(
        string? application,
        string? version,
        string? environment,
        string? minWindowMinutes,
        string? minCoverage,
        out DeployVerdictQuery query,
        out string error)
        => TryCreate(application, version, environment, minWindowMinutes, minCoverage, null, out query, out error);

    /// <summary>
    /// <see cref="TryCreate(string?, string?, string?, string?, string?, out DeployVerdictQuery, out string)"/> with a
    /// <paramref name="board"/> name (blank = every board; at most <c>MetricBoardValidation.MaxBoardNameLength</c> characters).
    /// </summary>
    public static bool TryCreate(
        string? application,
        string? version,
        string? environment,
        string? minWindowMinutes,
        string? minCoverage,
        string? board,
        out DeployVerdictQuery query,
        out string error)
    {
        query = null!;
        error = string.Empty;

        var name = Clean(application);
        if (name is null)
        {
            error = "'application' is required.";

            return false;
        }

        var cleanVersion = Clean(version);
        var cleanEnvironment = Clean(environment);
        if (name.Length > MaxNameLength || cleanVersion?.Length > MaxNameLength || cleanEnvironment?.Length > MaxNameLength)
        {
            error = $"'application', 'version' and 'environment' must be at most {MaxNameLength} characters.";

            return false;
        }

        var cleanBoard = Clean(board);
        if (cleanBoard?.Length > MetricBoardValidation.MaxBoardNameLength)
        {
            error = $"'board' must be at most {MetricBoardValidation.MaxBoardNameLength} characters.";

            return false;
        }

        var minutes = DefaultMinWindowMinutes;
        if (Clean(minWindowMinutes) is { } text
            && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes > MaxMinWindowMinutes))
        {
            error = $"'minWindowMinutes' must be a whole number from 0 to {MaxMinWindowMinutes}.";

            return false;
        }

        var coverage = 0d;
        if (Clean(minCoverage) is { } share
            && (!double.TryParse(share, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out coverage) || !double.IsFinite(coverage) || coverage > 1))
        {
            error = "'minCoverage' must be a number from 0 to 1.";

            return false;
        }

        query = new DeployVerdictQuery
        {
            Application = name,
            Version = cleanVersion,
            Environment = cleanEnvironment,
            Board = cleanBoard,
            MinWindow = TimeSpan.FromMinutes(minutes),
            MinCoverage = coverage,
        };

        return true;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
