using System.Text.Json;
using System.Text.Json.Serialization;
using Warp.Core.Enums;

namespace Warp.Core.Models;

public class UnifiedJobDetailModel
{
    /// <summary>
    /// How many continuations and spawned jobs the detail lists. A fan-out handler can spawn tens of
    /// thousands, and the dashboard refetches the detail while it is open.
    /// </summary>
    public const int MaxRelatedJobs = 50;

    // Core (always present)
    public Guid Id { get; set; }

    public JobKind Kind { get; set; }

    public string? Type { get; set; }

    public State CurrentState { get; set; }

    public DateTime CreateTime { get; set; }

    public CancellationMode CancellationMode { get; set; }

    // Payload
    public string? Message { get; set; }

    // Job-specific
    public string? HandlerType { get; set; }

    public DateTime? ScheduleTime { get; set; }

    // Batch-specific
    public int TotalJobs { get; set; }

    public int CompletedJobs { get; set; }

    public int FailedJobs { get; set; }

    public ContinuationOptions? ContinuationOptions { get; set; }

    // Message-specific
    public string? Queue { get; set; }

    // Flow
    public Guid? TraceId { get; set; }

    public ContinuationInfo? ParentJob { get; set; }

    public ContinuationInfo? SpawnedByJob { get; set; }

    /// <summary>
    /// The first <see cref="MaxRelatedJobs"/> continuations, oldest first. <see cref="ContinuationCount"/> has the total.
    /// </summary>
    public List<ContinuationInfo> Continuations { get; set; } = [];

    /// <summary>
    /// How many continuations exist, including any beyond the <see cref="Continuations"/> cap.
    /// </summary>
    public int ContinuationCount { get; set; }

    /// <summary>
    /// The first <see cref="MaxRelatedJobs"/> jobs this job's handler spawned, oldest first. <see cref="SpawnedJobCount"/> has the total.
    /// </summary>
    public List<ContinuationInfo> SpawnedJobs { get; set; } = [];

    /// <summary>
    /// How many jobs this job's handler spawned, including any beyond the <see cref="SpawnedJobs"/> cap.
    /// </summary>
    public int SpawnedJobCount { get; set; }

    // Origin: the inbound HTTP request that started this trace (reverse drill-down)
    public JobOriginModel? Origin { get; set; }

    // Metadata
    [JsonIgnore]
    public string? MetadataJson { get; set; }

    private Dictionary<string, object>? _metadata;

    public Dictionary<string, object>? Metadata => _metadata ??= MetadataJson != null
        ? JsonSerializer.Deserialize<Dictionary<string, object>>(MetadataJson)
        : null;

    // History
    public List<JobLogModel> Logs { get; set; } = [];
}

/// <summary>
/// The inbound HTTP request that originated a job, matched via shared trace id
/// (the reverse of the request→jobs drill-down on the endpoint detail page).
/// </summary>
public sealed class JobOriginModel
{
    public string Method { get; set; } = string.Empty;

    public string RouteTemplate { get; set; } = string.Empty;

    public string? User { get; set; }

    public Guid CallId { get; set; }

    public string EndpointId { get; set; } = string.Empty;
}
