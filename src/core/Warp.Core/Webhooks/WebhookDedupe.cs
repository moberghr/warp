using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Warp.Core.Webhooks;

// The dedupe key of a delivery (8.1): SHA-256 over the stored event id and the URL, lowercase hex (64 chars). Hashed
// because the URL alone can exceed an index key's size limit. The event id is length-prefixed, so two different
// (event id, URL) pairs never produce the same input whatever characters they contain.
internal static class WebhookDedupe
{
    internal static string Key(string eventId, string url)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{eventId.Length}:{eventId}{url}"))));
}
