using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;

namespace Warp.Core.Metrics;

/// <summary>
/// Where <c>MetricFlusher</c> writes. One implementation per process shape, resolved in a scope per flush:
/// <see cref="ContextMetricPointSink{TContext}"/> on the host's <c>TContext</c> in an <c>AddWarp</c>-only process,
/// and <c>ServerMetricPointSink</c> on the server context in a Warp server (§2.14), which replaces it whatever the
/// registration order. Internal across Core and Worker only (§0.5).
/// </summary>
internal interface IMetricPointSink
{
    /// <summary>
    /// Inserts the points with a single <c>SaveChangesAsync</c>. A rejected batch surfaces as a
    /// <see cref="DbUpdateException"/> after the change tracker was cleared, so the caller can retry row by row.
    /// </summary>
    Task WritePointsAsync(IReadOnlyList<MetricPoint> points, CancellationToken ct);

    /// <summary>
    /// Inserts or updates the registry rows by name in one <c>SaveChangesAsync</c>, separate from the points. A
    /// primary-key race with another process surfaces as a <see cref="DbUpdateException"/> after the change tracker
    /// was cleared; the caller retries the rows one by one, and leaves any that still conflict for its next flush.
    /// </summary>
    Task UpsertInstrumentsAsync(IReadOnlyList<MetricInstrument> rows, CancellationToken ct);
}

/// <summary>The EF work both sinks share; each only differs in which context it runs on.</summary>
internal static class MetricSinkWriter
{
    public static async Task WritePointsAsync(DbContext context, IReadOnlyList<MetricPoint> points, CancellationToken ct)
    {
        context.Set<MetricPoint>().AddRange(points);

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The flusher retries a rejected batch row by row on this same context: nothing of the failed batch
            // may still be tracked as Added, or every retry would re-insert it.
            context.ChangeTracker.Clear();

            throw;
        }
    }

    /// <summary>
    /// Reads the named registry rows, inserts the missing ones and merges into the existing ones, in one save. A
    /// primary-key race rethrows its <see cref="DbUpdateException"/> after clearing the change tracker.
    /// </summary>
    /// <param name="context">The context to write on.</param>
    /// <param name="rows">The registry rows to insert or merge into the existing ones.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="beforeSave">
    /// Test seam: runs after the existing rows were read and before the save, the window in which another process
    /// can insert the same name. Null in production.
    /// </param>
    internal static async Task UpsertInstrumentsAsync(
        DbContext context,
        IReadOnlyList<MetricInstrument> rows,
        CancellationToken ct,
        Func<CancellationToken, Task>? beforeSave = null)
    {
        var names = rows
            .Select(x => x.Name)
            .ToList();

        var existing = await context.Set<MetricInstrument>()
            .Where(x => names.Contains(x.Name))
            .ToListAsync(ct);

        // Case-insensitive, because SQL Server's default collation makes the primary key case-insensitive: inserting
        // "Payments.Count" beside an existing "payments.count" is a key violation there. A name differing only by
        // case merges into the row the read returned (first writer wins: meter, kind and bounds untouched). Under a
        // case-sensitive collation (PostgreSQL) the read returns only the exact name and the two stay distinct rows,
        // which is no conflict there. Rows added by this batch join the map too, so two such names in one batch never
        // both insert.
        var byName = new Dictionary<string, MetricInstrument>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in existing)
        {
            byName.TryAdd(row.Name, row);
        }

        foreach (var row in rows)
        {
            if (byName.TryGetValue(row.Name, out var current))
            {
                Merge(current, row);

                continue;
            }

            context.Set<MetricInstrument>().Add(row);
            byName.Add(row.Name, row);
        }

        try
        {
            if (beforeSave is not null)
            {
                await beforeSave(ct);
            }

            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two processes inserted the same instrument, or one of them lost an update race. Nothing partial may
            // linger on this context; the flusher retries on its next flush.
            context.ChangeTracker.Clear();

            throw;
        }
    }

    // First publisher wins: meter, kind and bounds are never rewritten. Everything that can legitimately grow
    // is widened rather than replaced, so two processes with different views of the instrument converge.
    private static void Merge(MetricInstrument current, MetricInstrument incoming)
    {
        if (incoming.LastSeenAt > current.LastSeenAt)
        {
            current.LastSeenAt = incoming.LastSeenAt;
        }

        current.Unit = incoming.Unit ?? current.Unit;
        current.Description = incoming.Description ?? current.Description;
        current.TagKeys = MetricPointMapper.UnionCsv(current.TagKeys, incoming.TagKeys, MetricPointMapper.MaxTagKeysLength);

        if (incoming.CollapsedAt is { } collapsedAt)
        {
            current.CollapsedAt = collapsedAt;
            current.CollapsedTags = MetricPointMapper.UnionCsv(current.CollapsedTags, incoming.CollapsedTags, MetricPointMapper.MaxTagKeysLength);
        }
    }
}
