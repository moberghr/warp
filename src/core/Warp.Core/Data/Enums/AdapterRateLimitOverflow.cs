namespace Warp.Core.Enums;

/// <summary>
/// Behaviour when a shared-rate-limited adapter cannot acquire a token within its budget.
/// <see cref="Wait"/> delays (up to a bounded max) for the next window/lease then throws
/// <c>AdapterRateLimitedException</c>; <see cref="FailFast"/> throws it immediately;
/// <see cref="Respond429"/> waits like <see cref="Wait"/> but then answers the call with a synthetic
/// <c>429 Too Many Requests</c> (carrying <c>Retry-After</c>) instead of throwing. All three surface as a
/// <c>Throttled</c> outcome.
/// </summary>
public enum AdapterRateLimitOverflow
{
    Wait = 1,
    FailFast = 2,

    /// <summary>
    /// Waits up to <c>maxWait</c> exactly like <see cref="Wait"/>, then — instead of throwing — completes
    /// the call with a synthetic <c>429 Too Many Requests</c> response carrying a <c>Retry-After</c> header
    /// with the wait the limiter computed. Pass <c>maxWait: TimeSpan.Zero</c> for fail-fast-with-429.
    /// <para>
    /// Exists for clients that classify by HTTP status rather than by exception type — notably Refit, which
    /// wraps every exception escaping the pipeline in <c>ApiRequestException</c> (so
    /// <c>catch (AdapterRateLimitedException)</c> never fires) and does not throw at all for a method
    /// returning <c>ApiResponse&lt;T&gt;</c>. A 429 travels both of those paths unchanged. No request is sent:
    /// the response is what the vendor would have answered had the call gone out.
    /// </para>
    /// <para>
    /// The 429 is synthesised at the <b>outermost</b> Warp handler, so handlers you add yourself never see
    /// it — inside the pipeline the refusal is the same throw the other two modes raise, and a resilience
    /// handler will not retry it back into the limiter or count it toward a circuit breaker. Only the
    /// caller sees a difference.
    /// </para>
    /// </summary>
    Respond429 = 3,
}
