using Warp.Core.Enums;

namespace Warp.Core.Models;

public class RecurringJobHistoryModel
{
    public Guid? JobId { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool JobExists { get; set; }

    public string? Type { get; set; }

    // Live from the Job row while it exists, otherwise the outcome ExpirationCleanup stamped onto the
    // audit row before deleting it. Null for a skipped firing (nothing ran) and for runs swept before
    // the stamp existed. JobExists stays the "is there a detail page to link to" flag.
    public State? CurrentState { get; set; }

    public bool Skipped { get; set; }
}
