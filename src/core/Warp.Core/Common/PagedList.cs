using Microsoft.EntityFrameworkCore;

namespace Warp.Core;

public static class PagedListExtensions
{
    public static async Task<PagedList<T>> ToPagedListAsync<T>(this IQueryable<T> query, BaseListRequest request, CancellationToken ct = default)
        where T : class
    {
        var totalCount = await query.CountAsync(ct);

        var pageCount = (int)Math.Ceiling(totalCount / (double)request.PageSize);

        var data = await query
            .Skip(SkipFor(request))
            .Take(request.PageSize)
            .ToListAsync(ct);

        return new PagedList<T>(totalCount, data, pageCount);
    }

    /// <summary>
    /// One page of <paramref name="query"/> with no total count: reads one row past the page to
    /// learn whether another page follows.
    /// </summary>
    /// <remarks>
    /// For lists that grow without bound and are read often, where a COUNT would scan everything the
    /// page skips. The caller supplies a stable order and a sane page size.
    /// </remarks>
    public static async Task<PagedSlice<T>> ToPagedSliceAsync<T>(this IQueryable<T> query, BaseListRequest request, CancellationToken ct = default)
        where T : class
    {
        var rows = await query
            .Skip(SkipFor(request))
            .Take(request.PageSize + 1)
            .ToListAsync(ct);

        return new PagedSlice<T>
        {
            Items = [.. rows.Take(request.PageSize)],
            HasMore = rows.Count > request.PageSize,
            Page = request.Page,
            PageSize = request.PageSize,
        };
    }

    /// <summary>
    /// Rows to skip for <paramref name="request"/>'s page, computed without overflow.
    /// </summary>
    /// <remarks>
    /// <c>Page * PageSize</c> in <see cref="int"/> wraps negative for a large page from a query string
    /// (<c>?page=4294968&amp;pageSize=500</c>), and both providers reject a negative OFFSET with a 500.
    /// Saturating at <see cref="int.MaxValue"/> reads as "past the end": an empty page.
    /// </remarks>
    private static int SkipFor(BaseListRequest request)
        => (int)Math.Clamp((long)request.Page * request.PageSize, 0, int.MaxValue);
}

/// <summary>
/// One page of a list that pages previous/next instead of by count.
/// </summary>
/// <remarks>
/// Carries no total on purpose: counting a list that grows without bound (a chatty job's output,
/// a worker's history) costs a scan of every row the page skips, on every refresh.
/// <see cref="HasMore"/> is enough for previous/next paging.
/// </remarks>
public class PagedSlice<T>
{
    /// <summary>
    /// The page's rows.
    /// </summary>
    public List<T> Items { get; set; } = [];

    /// <summary>
    /// Whether at least one more row exists past this page.
    /// </summary>
    public bool HasMore { get; set; }

    /// <summary>
    /// The page that was read.
    /// </summary>
    public int Page { get; set; }

    /// <summary>
    /// The page size that was used.
    /// </summary>
    public int PageSize { get; set; }
}

public class PagedList<T>
{
    public PagedList(int totalCount, List<T> items, int pageCount)
    {
        TotalCount = totalCount;
        Items = items;
        PageCount = pageCount;
    }

    public int TotalCount { get; set; }

    public int PageCount { get; set; }

    public List<T> Items { get; set; }
}

public class BaseListRequest
{
    public int Page { get; set; }

    public int PageSize { get; set; } = 20;
}
