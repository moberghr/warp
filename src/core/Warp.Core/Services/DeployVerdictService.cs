using Microsoft.Extensions.Options;
using Warp.Core.Metrics;
using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>
/// The post-deploy verdict: one machine-readable answer to "is release X of application A healthy?" for a CI/CD step or a
/// person. Composes the deploy history (§8.23), the health board (#326) and the SLOs (§8.31); registered by <c>AddWarp</c>,
/// so a dashboard-only host serves it. Read-only.
/// </summary>
public interface IDeployVerdictService
{
    /// <summary>
    /// Judges the deploy <paramref name="query"/> names (the latest one when it gives no version). Board tiles scoped to the
    /// application or to none are evaluated over the complete fine buckets after the deploy (see
    /// <c>DeployVerdictMath.Window</c>) against their rules and baselines, and the enabled SLOs
    /// scoped the same way contribute their current state. Bounded: one deploy read, one batched board read and one SLO read
    /// (none for a superseded deploy), whatever the number of tiles and objectives. Returns null when the application has no
    /// recorded deploy matching the version and environment. With <see cref="DeployVerdictQuery.Board"/> only that board's
    /// tiles count (one more read, the board list); without it the tiles of every board do. Throws
    /// <see cref="MetricBoardNotFoundException"/> when the named board does not exist.
    /// </summary>
    Task<DeployVerdictModel?> GetVerdict(DeployVerdictQuery query, CancellationToken ct);
}

/// <summary><see cref="IDeployVerdictService"/> over the existing read services: it adds no query of its own.</summary>
public sealed class DeployVerdictService : IDeployVerdictService
{
    private readonly IApplicationDeployQueryService _applications;
    private readonly IMetricBoardQueryService _board;
    private readonly ISloQueryService _slos;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<WarpConfiguration> _options;

    public DeployVerdictService(
        IApplicationDeployQueryService applications,
        IMetricBoardQueryService board,
        ISloQueryService slos,
        TimeProvider timeProvider,
        IOptions<WarpConfiguration> options)
    {
        _applications = applications;
        _board = board;
        _slos = slos;
        _timeProvider = timeProvider;
        _options = options;
    }

    public async Task<DeployVerdictModel?> GetVerdict(DeployVerdictQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Resolved first, and whatever the window: a board name that matches nothing is the caller's error, never an
        // empty set of checks that could read as a pass.
        var board = query.Board is null ? null : await FindBoardAsync(query.Board, ct);

        // The newest MaxDeploys of the application: a version older than that is out of reach, like on the Deploys card.
        var deploys = await _applications.GetDeploys(query.Application, null, null, ct);
        var resolved = DeployVerdictMath.Resolve(deploys, query.Version, query.Environment);
        if (resolved is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // The board's cut: the window holds only the complete fine buckets after the deploy (§8.23).
        var configuration = _options.Value;
        var window = DeployVerdictMath.Window(
            resolved.Deploy.FirstSeenAt,
            resolved.Next?.FirstSeenAt,
            now,
            query.MinWindow,
            configuration.FineResolutionMinutes,
            configuration.MetricFlushInterval);

        // An empty window (no complete bucket after the deploy yet, or ever for a deploy superseded inside its first
        // bucket) has nothing to read; the board rejects it.
        var tiles = window.To > window.From
            ? (await ReadTilesAsync(board, window, ct))
            : [];

        // An SLO only has a current state, which describes whatever runs now: it says nothing about a superseded release.
        var slos = resolved.Next is null
            ? (await _slos.GetObjectives(ct)).Items
            : [];

        return DeployVerdictMath.Build(resolved, window, tiles, slos, query.MinCoverage, board?.Name);
    }

    private async Task<MetricBoardSummaryModel> FindBoardAsync(string name, CancellationToken ct)
    {
        var boards = await _board.GetBoards(ct);

        return boards.FirstOrDefault(x => MetricBoardStore.SameName(x.Name, name))
            ?? throw new MetricBoardNotFoundException($"No metric board is named '{name}'.");
    }

    // No board: every board's tiles, as before named boards existed (a tile moved to a named board keeps counting).
    private async Task<IReadOnlyList<MetricBoardTileResult>> ReadTilesAsync(MetricBoardSummaryModel? board, DeployVerdictWindow window, CancellationToken ct)
    {
        if (board is null)
        {
            return (await _board.GetAllBoards(window.From, window.To, ct)).Tiles;
        }

        // The Default board through its own read, which also covers a Default board that was never created (id 0).
        var read = board.IsDefault
            ? await _board.GetBoard(window.From, window.To, ct)
            : await _board.GetBoard(board.Id, window.From, window.To, ct);

        return read?.Tiles ?? throw new MetricBoardNotFoundException($"The metric board '{board.Name}' was deleted.");
    }
}
