using System.Text;

namespace Warp.Core.Endpoints;

/// <summary>
/// URL-safe base64 encoding of a string identity so it survives a dashboard path segment (the raw value
/// may contain '/' and spaces). Used for the "{METHOD} {template}" endpoint route (the reverse trace
/// drill-down, job → originating request, builds the endpoint-detail link) and for the application-name
/// identity on the Applications detail routes (§8.19).
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

    // Inverse of Encode: restores base64 padding and the two URL-safe substitutions. Returns null for a
    // malformed id (bad base64) so callers surface a NotFound rather than throwing.
    public static string? TryDecode(string id)
    {
        var normalized = id.Replace('-', '+').Replace('_', '/');
        var padded = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
