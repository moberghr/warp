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
    /// application or to none are evaluated over <c>[deploy, now)</c> against their rules and baselines, and the enabled SLOs
    /// scoped the same way contribute their current state. Bounded: one deploy read, one batched board read and one SLO read
    /// (none for a superseded deploy), whatever the number of tiles and objectives. Returns null when the application has no
    /// recorded deploy matching the version and environment.
    /// </summary>
    Task<DeployVerdictModel?> GetVerdict(DeployVerdictQuery query, CancellationToken ct);
}

/// <summary><see cref="IDeployVerdictService"/> over the existing read services: it adds no query of its own.</summary>
public sealed class DeployVerdictService : IDeployVerdictService
{
    private readonly IApplicationQueryService _applications;
    private readonly IMetricBoardQueryService _board;
    private readonly ISloQueryService _slos;
    private readonly TimeProvider _timeProvider;

    public DeployVerdictService(IApplicationQueryService applications, IMetricBoardQueryService board, ISloQueryService slos, TimeProvider timeProvider)
    {
        _applications = applications;
        _board = board;
        _slos = slos;
        _timeProvider = timeProvider;
    }

    public async Task<DeployVerdictModel?> GetVerdict(DeployVerdictQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The newest MaxDeploys of the application: a version older than that is out of reach, like on the Deploys card.
        var deploys = await _applications.GetDeploys(query.Application, null, null, ct);
        var resolved = DeployVerdictMath.Resolve(deploys, query.Version, query.Environment);
        if (resolved is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var window = DeployVerdictMath.Window(resolved.Deploy.FirstSeenAt, resolved.Next?.FirstSeenAt, now, query.MinWindow);

        // An empty window (the deploy is this instant) has nothing to read; the board rejects it.
        var tiles = window.To > window.From
            ? (await _board.GetBoard(window.From, window.To, ct)).Tiles
            : [];

        // An SLO only has a current state, which describes whatever runs now: it says nothing about a superseded release.
        var slos = resolved.Next is null
            ? (await _slos.GetObjectives(ct)).Items
            : [];

        return DeployVerdictMath.Build(resolved, window, tiles, slos);
    }
}
