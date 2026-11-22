using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace Warp.Core.Notifications;

/// <summary>
/// Per-DbContext bookkeeping for <see cref="EnqueueNotificationInterceptor{TContext}"/>: the
/// notifications captured for the save in flight, the ones held back until an explicit transaction
/// commits, and the two flags the explicit call sites use to cooperate with the interceptor.
/// Keyed weakly on the context, so a disposed context takes its state with it.
/// </summary>
internal sealed class EnqueueNotificationState
{
    private static readonly ConditionalWeakTable<DbContext, EnqueueNotificationState> States = [];

    /// <summary>Notifications captured in <c>SavingChanges</c> for the save in flight.</summary>
    public List<Notification>? Captured { get; set; }

    /// <summary>Notifications from saves that committed inside an explicit transaction that has not
    /// committed yet. Sent on commit, dropped on rollback.</summary>
    public List<Notification>? Deferred { get; set; }

    /// <summary>Set by the interceptor whenever it sees a save on this context. A caller resets it,
    /// saves, and reads it back to learn whether the interceptor is wired onto this context.</summary>
    public bool Observed { get; set; }

    public static EnqueueNotificationState For(DbContext context) => States.GetValue(context, _ => new EnqueueNotificationState());
}
