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
        if (!TryParse(payload, out var parents, out var settled))
        {
            signals.SignalJobFinalized();

            return;
        }

        signals.SignalJobsFinalized(parents, settled);
    }

    /// <summary>
    /// Folds many payloads into as few as carry the same ids: the parents and the settled jobs of all of
    /// them, each once, split so no payload names more than <see cref="MaxIds"/>. A payload that says
    /// "unknown" (empty, or one this version cannot read) asks the receiver to check everything, which
    /// covers every other payload too, so the result is then that one "unknown" (null).
    /// </summary>
    internal static List<string?> Merge(IEnumerable<string?> payloads)
    {
        var parents = new HashSet<Guid>();
        var settled = new HashSet<Guid>();
        foreach (var payload in payloads)
        {
            if (!TryParse(payload, out var someParents, out var someSettled))
            {
                return [null];
            }

            parents.UnionWith(someParents);
            settled.UnionWith(someSettled);
        }

        var merged = new List<string?>();
        var parentList = parents.ToList();
        var settledList = settled.ToList();
        var p = 0;
        var s = 0;
        do
        {
            var takeParents = Math.Min(MaxIds, parentList.Count - p);
            var takeSettled = Math.Min(MaxIds - takeParents, settledList.Count - s);
            merged.Add(Encode(parentList.GetRange(p, takeParents), settledList.GetRange(s, takeSettled)));
            p += takeParents;
            s += takeSettled;
        }
        while (p < parentList.Count || s < settledList.Count);

        return merged;
    }

    private static bool TryParse(string? payload, out List<Guid> parents, out List<Guid> settled)
    {
        parents = [];
        settled = [];
        if (string.IsNullOrEmpty(payload))
        {
            return false;
        }

        var separator = payload.IndexOf(';', StringComparison.Ordinal);
        var parentsPart = separator < 0 ? payload : payload[..separator];
        var settledPart = separator < 0 ? None : payload[(separator + 1)..];

        return TryDecode(parentsPart, out parents) && TryDecode(settledPart, out settled);
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
