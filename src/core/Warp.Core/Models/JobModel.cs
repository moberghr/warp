using Warp.Core.Enums;

namespace Warp.Core.Models;

public class JobModel
{
    public Guid Id { get; set; }

    public string? Type { get; set; }

    public string? Message { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime? ScheduleTime { get; set; }

    public DateTime? ProcessedTime { get; set; }

    public State CurrentState { get; set; }

    public CancellationMode CancellationMode { get; set; }

    public string? HandlerType { get; set; }

    /// <summary>
    /// Attempts already spent, read from <c>Job.Metadata</c>. Null means "not computed for this
    /// listing" rather than "never retried" — only the retrying list populates it, so a zero here
    /// would be a claim the other listings have not checked.
    /// </summary>
    public int? RetryCount { get; set; }
}
