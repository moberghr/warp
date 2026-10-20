using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Warp.Core.Services;

/// <summary>
/// SQL-side filters on time-bucketed metric keys (§8.30), so a reader can drop or narrow the bucket
/// rows in the query instead of loading the whole <c>Statistic</c>/<c>Counter</c> table and
/// classifying keys in memory.
/// </summary>
/// <remarks>
/// A pre-filter only: callers still classify with <see cref="MetricTiers.TryClassifyKey"/>, which has
/// the exact word. Patterns use only the <c>%</c> and <c>_</c> wildcards, which mean the same on both
/// providers.
/// </remarks>
internal static class BucketKeyFilters
{
    /// <summary>
    /// LIKE patterns matching every time-bucketed key, whatever its date: the marked fine, hourly and
    /// daily tiers and the legacy unmarked <c>:yyyy-MM-dd-HH</c> stamp.
    /// </summary>
    /// <remarks>
    /// Anchored to the END of the key with the full stamp shape, like <see cref="MetricTiers.TryClassifyKey"/>:
    /// a bare <c>%:h1:%</c> would also drop a lifetime counter for a queue, application or adapter that
    /// happens to be named <c>h1</c>.
    /// </remarks>
    public static readonly string[] AnyBucketKey =
    [
        "%:" + MetricTiers.FineMarker + ":____-__-__-__-__",
        "%:" + MetricTiers.HourlyMarker + ":____-__-__-__",
        "%:" + MetricTiers.DailyMarker + ":____-__-__",
        "%:____-__-__-__",
    ];

    /// <summary>
    /// Keeps only rows whose key is not a time bucket.
    /// </summary>
    public static IQueryable<T> WithoutBucketKeys<T>(IQueryable<T> query, Expression<Func<T, string>> key)
    {
        foreach (var pattern in AnyBucketKey)
        {
            query = query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.Not(LikeCall(key.Body, pattern)),
                key.Parameters));
        }

        return query;
    }

    /// <summary>
    /// Keeps rows whose key matches at least one of <paramref name="patterns"/>, as one OR of LIKEs.
    /// </summary>
    public static IQueryable<T> WhereKeyLikeAny<T>(IQueryable<T> query, Expression<Func<T, string>> key, IReadOnlyList<string> patterns)
    {
        var body = patterns
            .Select(x => (Expression)LikeCall(key.Body, x))
            .Aggregate((x, y) => Expression.OrElse(x, y));

        return query.Where(Expression.Lambda<Func<T, bool>>(body, key.Parameters));
    }

    /// <summary>
    /// LIKE patterns matching the bucket keys dated on any day from <paramref name="since"/> to
    /// <paramref name="until"/>, or <see cref="AnyBucketKey"/> past <paramref name="maxDays"/> days.
    /// </summary>
    /// <remarks>
    /// Per day: <c>%:{day}-__</c> catches the marked hourly tier and the legacy unmarked stamp (both end
    /// <c>:yyyy-MM-dd-HH</c>), <c>%:{day}-__-__</c> the fine tier, and <c>%:d1:{day}</c> the daily one.
    /// </remarks>
    public static string[] ForWindow(DateTime since, DateTime until, int maxDays)
    {
        var firstDay = since.Date;
        var days = (int)(until.Date - firstDay).TotalDays + 1;
        if (days > maxDays)
        {
            return AnyBucketKey;
        }

        return
        [
            .. Enumerable.Range(0, days)
                .Select(x => firstDay.AddDays(x).ToString(MetricTiers.DailyFormat, CultureInfo.InvariantCulture))
                .SelectMany(x => new[]
                {
                    "%:" + x + "-__",
                    "%:" + x + "-__-__",
                    "%:" + MetricTiers.DailyMarker + ":" + x,
                }),
        ];
    }

    /// <summary>
    /// <c>EF.Functions.Like(key, pattern)</c> as an expression, so a pattern list can be combined.
    /// </summary>
    private static MethodCallExpression LikeCall(Expression key, string pattern)
    {
        var like = typeof(DbFunctionsExtensions).GetMethod(
            nameof(DbFunctionsExtensions.Like),
            [typeof(DbFunctions), typeof(string), typeof(string)])!;

        return Expression.Call(like, Expression.Constant(EF.Functions), key, Expression.Constant(pattern));
    }
}
