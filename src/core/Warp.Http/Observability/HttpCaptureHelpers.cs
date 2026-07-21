using System.Text;
using Microsoft.AspNetCore.Http;

namespace Warp.Http.Observability;

/// <summary>
/// Header redaction + byte-bounded truncation for inbound capture — the inbound counterpart of the
/// capture helpers in <c>Warp.Adapters.Http</c>. Values on the denylist are masked to <c>***</c>; captured
/// strings are truncated to a byte cap on a UTF-8 boundary (§1.2).
/// </summary>
internal static class HttpCaptureHelpers
{
    public static string RedactHeaders(IHeaderDictionary headers, ISet<string> redacted, int maxBytes)
    {
        var builder = new StringBuilder();
        foreach (var header in headers)
        {
            var value = redacted.Contains(header.Key) ? "***" : header.Value.ToString();
            builder.Append(header.Key).Append(": ").Append(value).Append('\n');
        }

        return TruncateToBytes(builder.ToString(), maxBytes);
    }

    public static string TruncateToBytes(string value, int maxBytes)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount <= maxBytes)
        {
            return value;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var boundary = SafeBoundary(bytes, maxBytes);

        return Encoding.UTF8.GetString(bytes, 0, boundary) + "…";
    }

    // Decode a captured byte PREFIX to a string. `length` bytes are valid content; a complete
    // (non-truncated) body is valid UTF-8 and decodes verbatim. When the prefix was truncated we reserve
    // room for the marker and cut on a UTF-8 boundary — a raw Encoding.UTF8.GetString over a mid-character
    // cut surfaces U+FFFD, and the marker byte always lies within the captured buffer so SafeBoundary can
    // inspect it. Keeps the stored value within the byte cap, marker included (matches TruncateToBytes).
    public static string DecodePrefix(byte[] buffer, int length, bool truncated)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        if (!truncated)
        {
            return Encoding.UTF8.GetString(buffer, 0, length);
        }

        var markerBytes = Encoding.UTF8.GetByteCount("…");
        var boundary = SafeBoundary(buffer, Math.Max(0, length - markerBytes));

        return Encoding.UTF8.GetString(buffer, 0, boundary) + "…";
    }

    // Walk back off any UTF-8 continuation bytes (0b10xxxxxx) so a multibyte character is never split.
    private static int SafeBoundary(byte[] buffer, int limit)
    {
        var boundary = limit;
        while (boundary > 0 && (buffer[boundary] & 0xC0) == 0x80)
        {
            boundary--;
        }

        return boundary;
    }
}
