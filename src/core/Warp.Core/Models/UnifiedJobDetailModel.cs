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

    public int RetriedTimes { get; set; }

    public int MaxRetries { get; set; }

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
