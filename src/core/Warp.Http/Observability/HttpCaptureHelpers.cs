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
