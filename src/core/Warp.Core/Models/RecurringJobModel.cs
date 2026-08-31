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

    // Last run = the newest non-skipped firing. HasLastRun distinguishes "never actually fired" from
    // "fired, but the job row has since been cleaned up" — the latter leaves LastJobId null, because
    // deleting a Job sets RecurringJobLog.JobId to null (DeleteBehavior.SetNull).
    public bool HasLastRun { get; set; }

    public Guid? LastJobId { get; set; }

    // The outcome, live from the Job row while it exists and otherwise from the outcome
    // ExpirationCleanup stamped onto the audit row before deleting it (RecurringJobLog.FinalState).
    // Null only when the run predates that stamping or was swept by a pre-upgrade deployment.
    public State? LastState { get; set; }

    // True when LastState comes from the stamp rather than a live Job row: the outcome is known but
    // there is no job detail page to open. Lets the dashboard say "Completed (cleaned up)" instead of
    // dropping the result entirely, and keeps it from linking into a 404.
    public bool LastRunCleanedUp { get; set; }
}

public class RecurringJobDetailModel : RecurringJobModel
{
    public string? Message { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
