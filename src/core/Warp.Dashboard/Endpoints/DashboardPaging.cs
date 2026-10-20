using Microsoft.AspNetCore.Http;
using Warp.Core;

namespace Warp.Dashboard.Endpoints;

/// <summary>
/// Bounds the paging the dashboard API accepts from the query string.
/// </summary>
/// <remarks>
/// Applied at the API boundary rather than inside <see cref="PagedListExtensions.ToPagedListAsync{T}"/>:
/// that helper and <see cref="BaseListRequest"/> are public and host code may page its own queries with
/// them, so capping there would silently change a host's results. Here it only bounds what an HTTP caller
/// can ask for: <c>?pageSize=1000000</c> would otherwise return a whole table, <c>pageSize=0</c> divides
/// by zero in the page count, and a negative page is a negative <c>Skip</c> that EF rejects with a 500.
/// </remarks>
internal static class DashboardPaging
{
    /// <summary>
    /// The largest page any dashboard list returns. The SPA never asks for more than 100.
    /// </summary>
    public const int MaxPageSize = 500;

    /// <summary>
    /// Clamps every <see cref="BaseListRequest"/> bound from the query string (<c>[AsParameters]</c>)
    /// before the handler runs.
    /// </summary>
    public static ValueTask<object?> ClampAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments)
        {
            if (argument is BaseListRequest request)
            {
                Clamp(request);
            }
        }

        return next(context);
    }

    /// <summary>
    /// Clamps <paramref name="request"/> in place: <c>Page</c> to 0 or more, <c>PageSize</c> to 1..<see cref="MaxPageSize"/>.
    /// </summary>
    public static BaseListRequest Clamp(BaseListRequest request)
    {
        request.Page = Math.Max(0, request.Page);
        request.PageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);

        return request;
    }
}
