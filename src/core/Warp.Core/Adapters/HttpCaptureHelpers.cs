using System.Text;
using Warp.Core.Enums;

namespace Warp.Core.Adapters;

/// <summary>
/// Capture primitives shared by Core-resident adapter callers (currently the webhook executor): decide
/// whether a tier captures for a given outcome, redact a header set against a denylist, and truncate a
/// captured value to a byte budget on a UTF-8 boundary (§1.2). Mirrors the byte-exact logic the
/// <c>Warp.Adapters.Http</c> <c>WarpAdapterHandler</c> applies for the auto-recording HTTP path; a caller
/// that owns its request/response (no downstream to preserve) feeds already-redacted/truncated values into
/// the <see cref="AdapterCallScope"/> capture setters.
/// </summary>
internal static class HttpCaptureHelpers
{
    private const string TruncationMarker = "…";

    public static bool ShouldCapture(CaptureMode mode, bool isFailure) => mode switch
    {
        CaptureMode.Always => true,
        CaptureMode.OnFailure => isFailure,
        _ => false,
    };

    public static string RedactHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
        ISet<string> redacted,
        int maxBytes)
    {
        var builder = new StringBuilder();
        foreach (var header in headers)
        {
            var value = redacted.Contains(header.Key) ? "***" : string.Join(", ", header.Value);
            builder.Append(header.Key).Append(": ").Append(value).Append('\n');
        }

        return TruncateToBytes(builder.ToString().TrimEnd('\n'), maxBytes);
    }

    public static string TruncateToBytes(string value, int maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount <= maxBytes)
        {
            return value;
        }

        var markerBytes = Encoding.UTF8.GetByteCount(TruncationMarker);
        var budget = Math.Max(0, maxBytes - markerBytes);
        var buffer = Encoding.UTF8.GetBytes(value);
        var boundary = SafeBoundary(buffer, Math.Min(budget, buffer.Length));

        return Encoding.UTF8.GetString(buffer, 0, boundary) + TruncationMarker;
    }

    private static int SafeBoundary(byte[] buffer, int limit)
    {
        var boundary = limit;

        // Walk back off any UTF-8 continuation byte (0b10xxxxxx) so we never split a multi-byte char.
        while (boundary > 0 && (buffer[boundary] & 0xC0) == 0x80)
        {
            boundary--;
        }

        return boundary;
    }
}
