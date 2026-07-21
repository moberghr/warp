using System.Text;

namespace Warp.Core.Endpoints;

/// <summary>
/// URL-safe encoding of the "{METHOD} {template}" endpoint route so it survives a dashboard path segment
/// (the raw route contains '/' and spaces). Used by the reverse trace drill-down (job → originating request)
/// to build the endpoint-detail link.
/// </summary>
public static class EndpointRouteId
{
    // MUST stay in sync with EndpointQueryService.EncodeId — the produced id has to match what the
    // endpoint list/detail pages use, so the drill-down link resolves to the same route.
    public static string Encode(string route)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(route))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
