using System.Text.Json;
using System.Text.Json.Serialization;
using Warp.Core.Enums;

namespace Warp.Core.Models;

public class UnifiedJobDetailModel
{
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

    public List<ContinuationInfo> Continuations { get; set; } = [];

    public List<ContinuationInfo> SpawnedJobs { get; set; } = [];

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
