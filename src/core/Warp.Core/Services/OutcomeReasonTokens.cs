using Warp.Core.Enums;

namespace Warp.Core.Services;

/// <summary>
/// Maps an <see cref="OutcomeReason"/> to the lowercase token used in <c>stats:{state}-{reason}</c> keys and
/// the <c>reason</c> meter tag.
/// </summary>
/// <remarks>
/// <para>
/// An explicit switch rather than <c>ToString().ToLowerInvariant()</c> for two reasons: it allocates nothing
/// per finalization, and it pins the wire format so renaming an enum member cannot silently rename a live
/// metric key (which would orphan every historical row under the old name). A guard test asserts every member
/// is mapped to something other than <see cref="Unknown"/>, so adding one to the enum without a token fails
/// the build's test run rather than shipping.
/// </para>
/// <para>
/// <b>The fallback returns a literal instead of throwing, deliberately.</b> Every caller is a finalization
/// site running INSIDE the job's own <c>catch (Exception e)</c>. A throw there is laundered into a fake
/// handler failure, finalization is re-entered from that catch, throws again, and escapes before
/// <c>SaveChangesAsync</c> — the job stays <c>Processing</c>, <c>StaleJobRecovery</c> requeues it, and it
/// re-poisons itself forever. Instrumentation must never out-throw (§8.19/§8.25), so an unmapped reason
/// degrades to one bounded extra key rather than taking the job down.
/// </para>
/// </remarks>
internal static class OutcomeReasonTokens
{
    /// <summary>
    /// The bounded fallback for an unmapped reason. Bounded matters: it is a metric key segment, so anything
    /// derived from the value itself would mint a new key family per bad value.
    /// </summary>
    internal const string Unknown = "unknown";

    internal static string For(OutcomeReason reason) =>
        reason switch
        {
            OutcomeReason.Retry => "retry",
            OutcomeReason.RetryExhausted => "retry-exhausted",
            OutcomeReason.Concurrency => "concurrency",
            OutcomeReason.RateLimit => "ratelimit",
            OutcomeReason.Timeout => "timeout",
            OutcomeReason.Saga => "saga",
            OutcomeReason.Manual => "manual",
            OutcomeReason.Recovery => "recovery",
            OutcomeReason.CircuitBreaker => "circuitbreaker",
            _ => Unknown,
        };
}
