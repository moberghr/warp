using Warp.Core.Enums;

namespace Warp.Core.Entities;

public class Job
{
    public Guid Id { get; set; } = Warp.Core.Data.WarpId.Next();

    public JobKind Kind { get; set; } = JobKind.Job;

    /// <summary>Opt-in provenance: the application that PUBLISHED this job (<c>WarpConfiguration.ApplicationName</c>), stamped at publish and preserved on requeue. Filter/display only — execution happens on a worker app, so this is not a metrics dimension. Null ⇒ feature off / legacy row.</summary>
    public string? Application { get; set; }

    public string? Type { get; set; }

    public string? Message { get; set; }

    public DateTime CreateTime { get; set; }

    public DateTime ScheduleTime { get; set; }

    public State CurrentState { get; set; }

    public string Queue { get; set; } = "default";

    public Guid? ParentJobId { get; set; }

    public Job? ParentJob { get; set; }

    public List<Job> ChildJobs { get; set; } = [];

    public Guid? CurrentWorkerId { get; set; }

    public string? HandlerType { get; set; }

    public DateTime? ExpireAt { get; set; }

    public DateTime? LastKeepAlive { get; set; }

    /// <summary>
    /// The run that holds this job while it is <c>Processing</c>: a fresh value written by every claim and
    /// cleared when the job leaves <c>Processing</c>. Every write a run makes to the row is guarded by it, so
    /// a run whose job was taken back and claimed again cannot write over the new run (#347). Null outside
    /// <c>Processing</c>.
    /// </summary>
    public Guid? RunId { get; set; }

    public Guid? TraceId { get; set; }

    public Guid? SpawnedByJobId { get; set; }

    public int JobCount { get; set; }

    public ContinuationOptions? ContinuationOptions { get; set; }

    public CancellationMode CancellationMode { get; set; }

    public string? Metadata { get; set; }

    public string? ParentSpanId { get; set; }
}
