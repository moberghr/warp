namespace Warp.Core.Webhooks;

// Secret checks shared by SendAsync and a redeliver that replaces the secret, so both reject the same
// shapes up front instead of leaving a delivery that can only ever fail at attempt time.
internal static class WebhookSecretValidation
{
    // StandardWebhooks signs with the base64-decoded secret (optionally whsec_-prefixed). A missing or
    // non-base64 secret would only fault at attempt time (a caught, recorded failed attempt that never
    // succeeds); reject it up front where the caller gets a clear error.
    internal static void ValidateStandardWebhooksSecret(string? secret, string paramName)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException(
                "WebhookSigning.StandardWebhooks requires a non-empty Secret (the whsec_ signing secret).",
                paramName);
        }

        var encoded = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret;
        var buffer = new byte[encoded.Length];
        if (!Convert.TryFromBase64String(encoded, buffer, out _))
        {
            throw new ArgumentException(
                "WebhookSigning.StandardWebhooks Secret must be base64 (optionally whsec_-prefixed).",
                paramName);
        }
    }
}
