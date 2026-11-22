using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Events;

namespace Warp.Core.Notifications;

/// <summary>
/// What a <see cref="NotificationKind.JobFinalized"/> notification says about the jobs that finished, so
/// the Orchestrator on the receiving side looks at those and not at everything in flight (rule 6.11).
/// The grammar is <c>parents[;settled]</c>: the parents to check for completion (a comma-separated list
/// of ids, or <c>-</c> for none), then the jobs that reached a terminal state themselves, which a
/// continuation may be waiting on. Nothing at all means "unknown": it is what a server from before 8.0
/// sends, and it asks the receiver to check everything.
/// </summary>
internal static class FinalizedParentsPayload
{
    // A PostgreSQL NOTIFY payload holds 8,000 bytes; 128 ids are 4,225. A flush that names more than
    // that sends "unknown".
    internal const int MaxIds = 128;

    private const string None = "-";
    private const string IdFormat = "N";

    internal static string Encode(Guid? parentJobId, Guid? settledJobId = null)
    {
        var parents = parentJobId is { } parent ? Format(parent) : None;

        return settledJobId is { } settled ? parents + ";" + Format(settled) : parents;
    }

    internal static string? Encode(IReadOnlyCollection<Guid> parentJobIds, IReadOnlyCollection<Guid>? settledJobIds = null)
    {
        settledJobIds ??= [];
        if (parentJobIds.Count + settledJobIds.Count > MaxIds)
        {
            return null;
        }

        var parents = parentJobIds.Count == 0 ? None : string.Join(',', parentJobIds.Select(Format));

        return settledJobIds.Count == 0 ? parents : parents + ";" + string.Join(',', settledJobIds.Select(Format));
    }

    internal static void Signal<TContext>(ServerTaskSignals<TContext> signals, string? payload)
        where TContext : DbContext
    {
        if (string.IsNullOrEmpty(payload))
        {
            signals.SignalJobFinalized();

            return;
        }

        var separator = payload.IndexOf(';', StringComparison.Ordinal);
        var parentsPart = separator < 0 ? payload : payload[..separator];
        var settledPart = separator < 0 ? None : payload[(separator + 1)..];

        if (!TryDecode(parentsPart, out var parents) || !TryDecode(settledPart, out var settled))
        {
            signals.SignalJobFinalized();

            return;
        }

        signals.SignalJobsFinalized(parents, settled);
    }

    private static string Format(Guid id) => id.ToString(IdFormat, CultureInfo.InvariantCulture);

    private static bool TryDecode(string part, out List<Guid> ids)
    {
        ids = [];
        if (string.Equals(part, None, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var item in part.Split(','))
        {
            if (!Guid.TryParseExact(item, IdFormat, out var id))
            {
                return false;
            }

            ids.Add(id);
        }

        return true;
    }
}
