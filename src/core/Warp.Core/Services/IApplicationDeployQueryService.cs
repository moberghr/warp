using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>
/// Read-only deploy history (#325, §8.23): the deploys Warp detected from <c>ApplicationVersion</c> changes. A separate
/// interface from <see cref="IApplicationQueryService"/> on purpose: that one is registered with <c>TryAdd</c>, so a host
/// may already implement it (registered before <c>AddWarp</c>), and adding members to it would break that host's build.
/// Registered by <c>AddWarp</c> (dashboard-only hosts resolve it) and implemented by <see cref="ApplicationQueryService{TContext}"/>
/// over the user's <c>TContext</c>; deploys are control-plane rows, always in Warp's database.
/// </summary>
public interface IApplicationDeployQueryService
{
    /// <summary>The most rows <see cref="GetDeploys"/> returns.</summary>
    public const int MaxDeploys = 500;

    /// <summary>
    /// Detected deploys (first instance of a new <c>ApplicationVersion</c>, §8.23), newest first, capped at
    /// <see cref="MaxDeploys"/> rows. <paramref name="application"/> null ⇒ every application. <paramref name="since"/>
    /// (inclusive) and <paramref name="until"/> (exclusive) bound <c>FirstSeenAt</c>; null leaves that side open, so
    /// <c>GetDeploys(app, null, null)</c>'s first row is the application's latest deploy.
    /// </summary>
    Task<IReadOnlyList<ApplicationDeployModel>> GetDeploys(string? application, DateTime? since, DateTime? until, CancellationToken ct = default);
}
