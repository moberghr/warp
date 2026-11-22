using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Events;

namespace Warp.Core.Notifications;

/// <summary>
/// What a <see cref="NotificationKind.JobFinalized"/> notification says about the jobs that finished:
/// the parents worth checking, so the Orchestrator on the receiving side looks at those and not at
/// every parent in flight (rule 6.11). Three forms: a comma-separated list of parent ids, <c>-</c> for
/// "these jobs had no parent", and nothing at all for "unknown", which is what a server from before 8.0
/// sends and what asks the receiver to check every parent.
/// </summary>
internal static class FinalizedParentsPayload
{
    // A PostgreSQL NOTIFY payload holds 8,000 bytes; 64 ids are 2,111. A flush with more parents than
    // that sends "unknown".
    internal const int MaxParents = 64;

    private const string NoParent = "-";
    private const string IdFormat = "N";

    internal static string Encode(Guid? parentJobId) =>
        parentJobId is { } parent ? parent.ToString(IdFormat, CultureInfo.InvariantCulture) : NoParent;

    internal static string? Encode(IReadOnlyCollection<Guid> parentJobIds)
    {
        if (parentJobIds.Count == 0)
        {
            return NoParent;
        }

        if (parentJobIds.Count > MaxParents)
        {
            return null;
        }

        return string.Join(',', parentJobIds.Select(x => x.ToString(IdFormat, CultureInfo.InvariantCulture)));
    }

    internal static void Signal<TContext>(ServerTaskSignals<TContext> signals, string? payload)
        where TContext : DbContext
    {
        if (string.Equals(payload, NoParent, StringComparison.Ordinal))
        {
            signals.SignalJobFinalized(parentJobId: null);

            return;
        }

        if (!TryDecode(payload, out var parents))
        {
            signals.SignalJobFinalized();

            return;
        }

        signals.SignalJobsFinalized(parents);
    }

    private static bool TryDecode(string? payload, out List<Guid> parents)
    {
        parents = [];
        if (string.IsNullOrEmpty(payload))
        {
            return false;
        }

        foreach (var part in payload.Split(','))
        {
            if (!Guid.TryParseExact(part, IdFormat, out var parent))
            {
                return false;
            }

            parents.Add(parent);
        }

        return true;
    }
}
