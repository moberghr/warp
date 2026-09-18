namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// Counts the statements the HARNESS itself issues against the database it is measuring.
/// <para>
/// The drain poll and the progress count run once a second and once every fifteen against the same
/// database, so <c>pg_stat_statements</c> attributes them to Warp like anything else. They cannot be
/// filtered out at the source: that view strips comments when it normalizes a statement, so an EF
/// <c>TagWith</c> marker never survives into it — verified directly, where a leading comment and an
/// inline one both collapsed into the same untagged entry. Matching on query shape instead would be
/// worse than leaving them in, because the statements they most resemble are Warp's own.
/// </para>
/// <para>
/// Counting them here is exact by construction and needs nothing from the database. It does not
/// subtract them from the reported totals — the execution time they cost is inside
/// <c>total_exec_time</c> with no way to attribute it back out, so a corrected statement count beside
/// an uncorrected time figure would be the misleading half-measure. It is reported instead, which is
/// what makes it useful: on the short runs this was built for it is a rounding error (tens of calls
/// against hundreds of thousands, far under run-to-run variance), and on a long soak the number grows
/// until it is visibly worth caring about.
/// </para>
/// </summary>
public static class HarnessQueries
{
    private static long _count;

    /// <summary>Running total since the process started. Snapshot it around a measured window.</summary>
    public static long Total => Interlocked.Read(ref _count);

    /// <summary>Call immediately before issuing a bookkeeping query.</summary>
    public static void Count() => Interlocked.Increment(ref _count);
}
