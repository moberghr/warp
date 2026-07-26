using Warp.Core.Enums;

namespace Warp.Core.Entities;

public class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public JobKind Kind { get; set; } = JobKind.Job;

    /// <summary>Opt-in provenance: the application that PUBLISHED this job (<c>WarpConfiguration.ApplicationName</c>), stamped at publish and preserved on requeue. Filter/display only — execution happens on a worker app, so this is not a metrics dimension. Null ⇒ feature off / legacy row.</summary>
    public string? Application { get; set; }

    /// <summary>Client session id (OTel <c>session.id</c>) propagated via W3C baggage from the browser through the API to this job, stamped at publish and inherited by spawned jobs (§8.27). Ties a job to the frontend session that ultimately caused it. Null when no session baggage was in scope.</summary>
    public string? Session { get; set; }

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

    public Guid? TraceId { get; set; }

    public Guid? SpawnedByJobId { get; set; }

    public int JobCount { get; set; }

    public ContinuationOptions? ContinuationOptions { get; set; }

    public CancellationMode CancellationMode { get; set; }

    public string? Metadata { get; set; }

    public string? ParentSpanId { get; set; }
}
