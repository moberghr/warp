namespace Warp.Core.Enums;

/// <summary>
/// Why a delivery's last attempt failed (<c>WebhookDelivery.LastFailureReason</c>, 8.1). Values start at 1 (§8.11).
/// </summary>
public enum WebhookFailureReason
{
    /// <summary>The attempt ran past <c>WebhookAttemptTimeout</c> (or the client's own timeout) before the response headers arrived.</summary>
    Timeout = 1,

    /// <summary>The destination host name did not resolve.</summary>
    DnsFailure = 2,

    /// <summary>The destination refused the connection.</summary>
    ConnectionRefused = 3,

    /// <summary>The TLS handshake failed (certificate, protocol).</summary>
    TlsError = 4,

    /// <summary>The connection failed or broke for another reason (reset, unreachable, closed mid-response).</summary>
    ConnectionFailed = 5,

    /// <summary><c>PublicAddressesOnly</c> refused the destination: it resolves to no public address.</summary>
    AddressNotPublic = 6,

    /// <summary>The receiver answered with a status code outside the delivery's success codes (see <c>LastStatusCode</c>).</summary>
    UnsuccessfulStatus = 7,

    /// <summary>Anything else: building or signing the request failed, a rate limiter refused it, an unexpected error.</summary>
    Other = 8,
}
