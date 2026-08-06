using Shouldly;
using Warp.Core.Enums;
using Warp.Core.Services;

namespace Warp.Tests.Observability;

/// <summary>
/// The reason token is a wire format: it becomes part of a <c>Statistic</c> key that accumulates for 90 days
/// and a meter tag that external dashboards group by. These tests pin the mapping so a rename or a new enum
/// member cannot silently change or omit a token — an unmapped member would otherwise surface as a throwing
/// worker finalization, and a changed token would orphan every historical row under the old name.
/// </summary>
[Trait("Category", "NoDb")]
public class OutcomeReasonTokenTests
{
    [TimedFact]
    public void For_EveryEnumMember_ReturnsADistinctLowercaseToken()
    {
        var reasons = Enum.GetValues<OutcomeReason>();
        reasons.ShouldNotBeEmpty();

        var tokens = new List<string>();

        foreach (var reason in reasons)
        {
            var token = OutcomeReasonTokens.For(reason);

            // The map no longer throws on an unmapped member (it degrades to "unknown" — a throw at a
            // finalization site poisons the job, see OutcomeReasonTokens' remarks), so the completeness
            // guard lives HERE instead: a new enum member with no token falls through to "unknown" and
            // fails this assertion.
            token.ShouldNotBe(OutcomeReasonTokens.Unknown, $"{reason} has no token mapped — add one to OutcomeReasonTokens.For.");
            token.ShouldNotBeNullOrWhiteSpace();
            token.ShouldBe(token.ToLowerInvariant(), $"Token for {reason} must be lowercase — it is shown verbatim on the Counters page.");
            token.ShouldNotContain(":", Case.Sensitive, "':' separates key segments and would break tier/bucket parsing.");
            tokens.Add(token);
        }

        tokens.Distinct(StringComparer.Ordinal).Count().ShouldBe(reasons.Length, "two reasons mapping to the same token would silently merge their counters.");
    }

    [TimedFact]
    public void For_KnownReasons_MatchTheDocumentedWireFormat()
    {
        // Spelled out rather than derived: if these ever need to change, it should be a deliberate edit here
        // with a migration story for the existing rows, not a side effect of renaming an enum member.
        OutcomeReasonTokens.For(OutcomeReason.Retry).ShouldBe("retry");
        OutcomeReasonTokens.For(OutcomeReason.RetryExhausted).ShouldBe("retry-exhausted");
        OutcomeReasonTokens.For(OutcomeReason.Concurrency).ShouldBe("concurrency");
        OutcomeReasonTokens.For(OutcomeReason.RateLimit).ShouldBe("ratelimit");
        OutcomeReasonTokens.For(OutcomeReason.Timeout).ShouldBe("timeout");
        OutcomeReasonTokens.For(OutcomeReason.Saga).ShouldBe("saga");
        OutcomeReasonTokens.For(OutcomeReason.Manual).ShouldBe("manual");
        OutcomeReasonTokens.For(OutcomeReason.Recovery).ShouldBe("recovery");
    }

    [TimedFact]
    public void For_UnmappedValue_ReturnsUnknownAndDoesNotThrow()
    {
        // Every caller runs inside the job's own catch block at finalization. A throw there is caught as if
        // the HANDLER had failed, finalization re-runs, throws again, and escapes before SaveChangesAsync —
        // leaving the job Processing for StaleJobRecovery to requeue, forever. So an unmapped reason must
        // degrade to a bounded literal, never take the job down (§8.19/§8.25: instrumentation never out-throws).
        var token = Should.NotThrow(() => OutcomeReasonTokens.For((OutcomeReason)999));

        token.ShouldBe("unknown");
    }
}
