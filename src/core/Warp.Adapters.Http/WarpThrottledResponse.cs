using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Warp.Core.Adapters;

namespace Warp.Adapters.Http;

/// <summary>
/// The synthetic <c>429 Too Many Requests</c> answer produced by <see cref="WarpAdapterHandler"/> under
/// <c>AdapterRateLimitOverflow.Respond429</c> — no request was sent; this is what the vendor would have
/// answered had it gone out. Carries the limiter's computed wait as <c>Retry-After</c>.
/// <para>
/// It is a distinct type rather than a plain <see cref="HttpResponseMessage"/> so
/// <see cref="WarpAdapterHandler"/> records the call as <c>Throttled</c> — a bare 429 would take the
/// non-success branch and be recorded <c>Failed</c>, losing exactly the classification this mode exists to
/// preserve, and matching on the STATUS instead would silently reclassify every real vendor 429 too. A
/// marker header would leak to the caller; the type does not (to every consumer it is an ordinary 429).
/// </para>
/// </summary>
internal sealed class WarpThrottledResponse : HttpResponseMessage
{
    /// <summary>
    /// Non-standard companion to <c>Retry-After</c> carrying the same wait in milliseconds — the standard
    /// header is whole seconds, which rounds a 200ms window wait up to a full second.
    /// </summary>
    internal const string RetryAfterMillisecondsHeader = "x-warp-retry-after-ms";

    private WarpThrottledResponse(AdapterRateLimitedException refusal)
        : base(HttpStatusCode.TooManyRequests)
        => Refusal = refusal;

    /// <summary>The refusal this response stands in for; drives the <c>Throttled</c> outcome upstream.</summary>
    public AdapterRateLimitedException Refusal { get; }

    public static WarpThrottledResponse Create(HttpRequestMessage request, AdapterRateLimitedException refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        var response = new WarpThrottledResponse(refusal)
        {
            ReasonPhrase = "Too Many Requests (Warp shared rate limit)",
            RequestMessage = request,
        };

        if (refusal.RetryAfter is { } retryAfter)
        {
            // Retry-After is whole seconds on the wire — round UP so a caller honouring it never retries
            // before the window actually frees up.
            var seconds = Math.Max(0, (long)Math.Ceiling(retryAfter.TotalSeconds));
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));

            response.Headers.TryAddWithoutValidation(
                RetryAfterMillisecondsHeader,
                ((long)Math.Ceiling(retryAfter.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture));
        }

        return response;
    }
}
