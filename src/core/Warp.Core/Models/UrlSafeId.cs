using System.Text;

namespace Warp.Core.Models;

/// <summary>
/// URL-safe base64 codec for a string identity so it survives a dashboard path segment (the raw value
/// may contain '/' and spaces). A neutral shared utility used by BOTH the endpoints detail route (the
/// "{METHOD} {template}" route identity, and the reverse trace drill-down job → originating request) and
/// the applications routes (the application-name identity, §8.19). Kept protocol/feature-neutral so the
/// two surfaces provably share one scheme.
/// </summary>
public static class UrlSafeId
{
    // MUST stay in sync with EndpointQueryService.EncodeId — the produced id has to match what the
    // endpoint list/detail pages use, so the drill-down link resolves to the same route.
    public static string Encode(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
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
