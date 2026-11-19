namespace Warp.Core.Models;

/// <summary>
/// One detected deploy (§8.23): the first time an instance of <see cref="Application"/> registered with
/// <see cref="Version"/> in <see cref="Environment"/>. <see cref="InstanceId"/> / <see cref="InstanceName"/> name that
/// first instance, which may since have stopped.
/// </summary>
public sealed class ApplicationDeployModel
{
    public Guid Id { get; init; }

    public string Application { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    /// <summary>The reporting instance's environment, or null when it reported none.</summary>
    public string? Environment { get; init; }

    public DateTime FirstSeenAt { get; init; }

    public Guid InstanceId { get; init; }

    public string? InstanceName { get; init; }
}
