using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Warp.Core.Webhooks;

/// <summary>
/// The built-in <see cref="IWebhookSigner"/> implementing the Standard Webhooks spec
/// (https://github.com/standard-webhooks/standard-webhooks): HMAC-SHA256 over
/// <c>{webhook-id}.{webhook-timestamp}.{payload}</c>, emitting three headers —
/// <c>webhook-id</c> (the delivery's stable <c>EventId</c>, constant across retries so the consumer can
/// deduplicate), <c>webhook-timestamp</c> (unix seconds of the current attempt), and
/// <c>webhook-signature</c> in the space-delimited <c>v1,&lt;base64&gt;</c> form. The secret is the
/// Standard Webhooks <c>whsec_&lt;base64&gt;</c> string; the HMAC key is the base64-decoded portion after
/// the optional <c>whsec_</c> prefix.
/// <para>
/// Verified against the published Standard Webhooks test vector
/// (secret <c>whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw</c>, id <c>msg_p5jXN8AQM9LWM0D4loKWxJek</c>,
/// timestamp <c>1614265330</c>, payload <c>{"test": 2432232314}</c> ⇒
/// <c>v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=</c>) in <c>WebhookSigningTests</c>.
/// </para>
/// </summary>
public sealed class StandardWebhooksSigner : IWebhookSigner
{
    private const string SecretPrefix = "whsec_";

    public IReadOnlyDictionary<string, string> Sign(WebhookSignatureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.Secret))
        {
            throw new InvalidOperationException(
                "StandardWebhooks signing requires a secret on the delivery (WebhookSend.Secret). "
                + "Use WebhookSigning.None for hosts that embed their own signature in the payload.");
        }

        var timestamp = request.Timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signedContent = $"{request.WebhookId}.{timestamp}.{request.Payload}";

        var key = DecodeSecret(request.Secret);

        using var hmac = new HMACSHA256(key);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signedContent)));

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["webhook-id"] = request.WebhookId,
            ["webhook-timestamp"] = timestamp,
            ["webhook-signature"] = $"v1,{signature}",
        };
    }

    private static byte[] DecodeSecret(string secret)
    {
        var encoded = secret.StartsWith(SecretPrefix, StringComparison.Ordinal)
            ? secret[SecretPrefix.Length..]
            : secret;

        return Convert.FromBase64String(encoded);
    }
}
