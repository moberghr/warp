using Warp.Core.Enums;

namespace Warp.Core.Models;

public class RecurringJobModel
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Cron { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public DateTime? NextExecution { get; set; }

    public DateTime? LastExecution { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? DisabledAt { get; set; }

    // Last run = the newest non-skipped firing. HasLastRun distinguishes "never actually fired"
    // from "fired, but the job row has since been cleaned up" — both leave LastJobId/LastState null,
    // because deleting a Job sets RecurringJobLog.JobId to null (DeleteBehavior.SetNull).
    public bool HasLastRun { get; set; }

    public Guid? LastJobId { get; set; }

    public State? LastState { get; set; }
}

public class RecurringJobDetailModel : RecurringJobModel
{
    public string? Message { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
