using Warp.Core.Entities;
using Warp.Core.Enums;

namespace Warp.Core.Data.Entities;

public class RecurringJobLog
{
    public int Id { get; set; }

    public int RecurringJobId { get; set; }

    public Guid? JobId { get; set; }

    public Job? Job { get; set; }

    public bool Skipped { get; set; }

    // The firing's outcome, preserved when its Job row is swept. RecurringJobLog is the immutable
    // audit trail (§8.9) but deleting a Job nulls JobId (DeleteBehavior.SetNull), so without this a
    // low-frequency definition — monthly, quarterly — reads as "cleaned up" for every run it ever
    // made once JobExpirationTimeout (1 day) passes. Stamped by ExpirationCleanup immediately before
    // the delete, never at finalization: the worker would need a lookup mid-finalization (§0.2/§6.1).
    // Null while the Job row still exists (read the live state from it) and for a skipped firing.
    public State? FinalState { get; set; }

    public DateTime CreatedAt { get; set; }
}
