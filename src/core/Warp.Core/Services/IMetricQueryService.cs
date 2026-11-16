using Warp.Core.Enums;
using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>
/// Reads collected application metrics for the dashboard (custom application metrics). Registered by
/// <c>AddWarp</c> itself, so dashboard-only hosts resolve it. Read-only: nothing is written.
/// </summary>
public interface IMetricQueryService
{
    /// <summary>The longest range <see cref="GetSeries"/> reads, in days.</summary>
    const int MaxRangeDays = 90;

    /// <summary>The most tag filters <see cref="GetSeries"/> accepts.</summary>
    const int MaxFilters = 20;

    /// <summary>
    /// The earliest From <see cref="GetSeries"/> reads (UTC): a 7-day compare overlay plus a one-day tier floor is read
    /// below From, and must not underflow.
    /// </summary>
    static readonly DateTime EarliestFrom = DateTime.SpecifyKind(DateTime.MinValue.AddDays(8), DateTimeKind.Utc);

    /// <summary>The latest To <see cref="GetSeries"/> reads (UTC): one day of headroom for the bucket arithmetic.</summary>
    static readonly DateTime LatestTo = DateTime.SpecifyKind(DateTime.MaxValue.AddDays(-1), DateTimeKind.Utc);

    /// <summary>The registry rows only — never scans <c>metric_point</c>.</summary>
    Task<MetricInstrumentModel[]> ListInstruments(CancellationToken ct);

    /// <summary>
    /// The registry kind of one instrument (a single-row read by primary key); null when the instrument is not in the
    /// registry yet.
    /// </summary>
    Task<MetricKind?> GetInstrumentKindAsync(string name, CancellationToken ct);

    /// <summary>One instrument's series over a range; null when the instrument is not in the registry.</summary>
    /// <remarks>
    /// <para>
    /// Filters follow the SLO filter semantics exactly: a trailing <c>*</c> is a prefix match, a bare <c>*</c> matches
    /// any present value, and an absent tag reads as <c>{none}</c>, matched only by an exact <c>{none}</c>. Breakdown
    /// lines come back ranked by the absolute headline value over the range (sum for counters, count for histograms,
    /// average for gauges), ties by ordinal value; callers should keep that order.
    /// </para>
    /// <para>
    /// Known limits. On SQL Server with the default case-insensitive collation, the grouped read
    /// (<c>GROUP BY Tags</c>) merges tag sets that differ only by letter case into one series. Under locking READ
    /// COMMITTED (SQL Server without RCSI), a read racing a rollup can transiently see a value in both the source and
    /// the parent tier, so one bucket may briefly read high until the rollup commits.
    /// </para>
    /// <para>
    /// Throws <see cref="MetricQueryValidationException"/> for an invalid query: an empty or inverted range, a range
    /// over <see cref="MaxRangeDays"/> days, more than <see cref="MaxFilters"/> filters, or a malformed filter. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for a percentile outside 1–99 or a range within eight days of
    /// <see cref="DateTime.MinValue"/> or one day of <see cref="DateTime.MaxValue"/>.
    /// </para>
    /// </remarks>
    Task<MetricSeriesModel?> GetSeries(string instrument, MetricSeriesQuery query, CancellationToken ct);
}
