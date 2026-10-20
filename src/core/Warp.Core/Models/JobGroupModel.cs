using Warp.Core.Enums;

namespace Warp.Core.Models;

public class JobGroupModel
{
    public Guid Id { get; set; }

    public JobKind Kind { get; set; }

    public State CurrentState { get; set; }

    public int JobCount { get; set; }

    public DateTime CreateTime { get; set; }

    // Message-specific
    public string? Type { get; set; }

    public string? Payload { get; set; }

    public string? Queue { get; set; }

    // Batch-specific
    public int TotalJobs { get; set; }

    public ContinuationOptions? ContinuationOptions { get; set; }

    public int CompletedJobs { get; set; }

    public int FailedJobs { get; set; }
}

public class JobGroupDetailModel : JobGroupModel
{
    public Guid? ParentJobId { get; set; }

    public JobKind? ParentJobKind { get; set; }

    public Guid? TraceId { get; set; }

    public int SpawnedJobsCount { get; set; }

    /// <summary>
    /// The first <see cref="UnifiedJobDetailModel.MaxRelatedJobs"/> continuation batches, oldest first.
    /// <see cref="ContinuationCount"/> has the total.
    /// </summary>
    public List<ContinuationInfo> Continuations { get; set; } = [];

    /// <summary>
    /// How many continuation batches exist, including any beyond the <see cref="Continuations"/> cap.
    /// </summary>
    public int ContinuationCount { get; set; }

    /// <summary>
    /// The group's lifecycle events. Handler output and progress are not included.
    /// </summary>
    public List<JobLogModel> Logs { get; set; } = [];
}

public class ContinuationInfo
{
    public Guid Id { get; set; }

    public JobKind Kind { get; set; }

    public State CurrentState { get; set; }

    public string? Type { get; set; }

    public string? HandlerType { get; set; }
}
