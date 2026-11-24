using Warp.Core.Entities;

namespace Warp.Core.Data.Queries;

/// <summary>
/// Where a dispatcher's next claim from each queue starts reading the claim index (rule 6.16).
/// <para>
/// A job that leaves Enqueued leaves its claim-index entry behind until vacuum removes it, and those
/// dead entries sit exactly where every claim starts reading: the head of the queue, the oldest
/// schedule time. Under a backlog each claim walked all of them to find the live rows behind (about
/// a hundred entries read for ten jobs claimed). While claims come back full, the next one starts at
/// the newest schedule time the previous one took, so it skips what earlier claims already emptied.
/// No margin before it: at a thousand jobs a second, 100 ms of margin was a hundred dead entries
/// re-read by every claim, and what a margin would catch the head claim below catches anyway.
/// </para>
/// <para>
/// A row can become claimable with a schedule time older than that: a job committed late by a long
/// publishing transaction, or a scheduled job activated with its original due time. It is still
/// claimed promptly, because a claim that comes back short and a claim every
/// <see cref="HeadClaimInterval"/> both read from the head again.
/// </para>
/// <para>
/// One per dispatcher, used by its single claim loop; not thread-safe.
/// </para>
/// </summary>
public sealed class ClaimWatermark
{
    /// <summary>Read from the head at least this often, whatever the watermark says.</summary>
    public static readonly TimeSpan HeadClaimInterval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, Position> _queues = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public ClaimWatermark(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The schedule time the next claim from <paramref name="queue"/> starts at, or <c>null</c> to read
    /// from the head.
    /// </summary>
    public DateTime? StartFor(string queue)
    {
        if (!_queues.TryGetValue(queue, out var position) || position.From is not { } from)
        {
            return null;
        }

        if (_time.GetElapsedTime(position.HeadClaimedAt) >= HeadClaimInterval)
        {
            return null;
        }

        return from;
    }

    /// <summary>
    /// Records what a claim from <paramref name="queue"/> asked for and got. A full claim moves the
    /// watermark to the newest schedule time it took; a short one clears it, so the next claim reads
    /// from the head.
    /// </summary>
    public void Record(string queue, DateTime? startedAt, int requested, IReadOnlyList<Job> claimed)
    {
        _queues.TryGetValue(queue, out var position);
        var headClaimedAt = startedAt == null ? _time.GetTimestamp() : position.HeadClaimedAt;

        DateTime? from = null;
        if (claimed.Count >= requested && claimed.Count > 0)
        {
            from = claimed.Max(x => x.ScheduleTime);
        }

        _queues[queue] = new Position(from, headClaimedAt);
    }

    private readonly record struct Position(DateTime? From, long HeadClaimedAt);
}
