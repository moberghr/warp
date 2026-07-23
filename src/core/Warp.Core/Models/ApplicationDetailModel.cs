using Warp.Core.Enums;

namespace Warp.Core.Models;

/// <summary>
/// The Applications detail payload (§8.19 multi-app observability): one application's unified instance
/// list (server ∪ non-server) plus its version/environment spread. Deliberately lean — per-app adapter /
/// endpoint / job-execution activity rollups are served by the existing per-app readers
/// (<c>IAdapterQueryService.GetAdapterStatsByApplication</c>, <c>IEndpointQueryService.GetEndpointStatsByApplication</c>,
/// <c>IJobQueryService.GetJobExecutionMetrics(application)</c>) which the API composes separately.
/// </summary>
public sealed class ApplicationDetailModel
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<InstanceView> Instances { get; init; } = [];

    public IReadOnlyList<string> Versions { get; init; } = [];

    public IReadOnlyList<string> Environments { get; init; } = [];
}

/// <summary>
/// A single instance's detail: its unified <see cref="InstanceView"/> plus the most-recent lifecycle
/// events from <c>ApplicationInstanceLog</c> (newest first), covering both server and non-server instances.
/// </summary>
public sealed class ApplicationInstanceDetailModel
{
    public InstanceView Instance { get; init; } = new();

    public IReadOnlyList<ApplicationInstanceLogModel> RecentEvents { get; init; } = [];
}

/// <summary>One lifecycle event row (register / heartbeat-lost / recovered / stopped / stale-swept).</summary>
public sealed class ApplicationInstanceLogModel
{
    public Guid Id { get; init; }

    public Guid InstanceId { get; init; }

    public string ApplicationName { get; init; } = string.Empty;

    public DateTime Timestamp { get; init; }

    public ApplicationInstanceEventType EventType { get; init; }

    public string? Message { get; init; }
}
