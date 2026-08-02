using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Services;

namespace Warp.Tests.Applications;

/// <summary>
/// NoDb coverage for the per-job-TYPE / per-HANDLER execution counter-key layout (§8.19 multi-app
/// observability) and its tiered history keys (§8.30). Proves the keys round-trip through their own parsers
/// AND — the disjoint-namespace guarantee — that the EXISTING <c>stats:</c> readers/parsers provably IGNORE the
/// new keys, the new parsers reject the old <c>stats:</c> keys, and the tiered <c>hist</c>/<c>pcth</c> parsers
/// stay disjoint from each other and from the lifetime parsers. A drift between the builders and parsers (both
/// in <see cref="JobStatsKeys"/>) fails here rather than silently zeroing the dashboard.
/// </summary>
[Trait("Category", "NoDb")]
public class JobStatsKeysTests
{
    private const string TypeId = "MyApp.Jobs.SendEmail, MyApp, Version=1.0.0.0";
    private const string HandlerId = "MyApp.Jobs.SendEmailHandler, MyApp, Version=1.0.0.0";

    private static readonly DateTime Ts = new(2026, 7, 20, 14, 37, 12, DateTimeKind.Utc);

    private static string FineSuffix => MetricTiers.Suffix(MetricTier.Fine, Ts, 5);

    private static readonly DateTime FineBucket = new(2026, 7, 20, 14, 35, 0, DateTimeKind.Utc);

    [TimedFact]
    public void Total_RoundTripsDimensionIdAndToken()
    {
        var key = JobStatsKeys.Total(JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.SucceededToken);

        JobStatsKeys.TryParseTotal(key, out var dim, out var id, out var token).ShouldBeTrue();
        dim.ShouldBe(JobStatsKeys.TypeMarker);
        id.ShouldBe(TypeId);
        token.ShouldBe(JobStatsKeys.SucceededToken);
    }

    [TimedFact]
    public void Total_DurationToken_RoundTrips()
    {
        var key = JobStatsKeys.Total(JobStatsKeys.HandlerMarker, HandlerId, JobStatsKeys.DurationToken);

        JobStatsKeys.TryParseTotal(key, out var dim, out var id, out var token).ShouldBeTrue();
        dim.ShouldBe(JobStatsKeys.HandlerMarker);
        id.ShouldBe(HandlerId);
        token.ShouldBe(JobStatsKeys.DurationToken);
    }

    [TimedFact]
    public void Pct_RoundTripsAndTotalParserRejectsIt()
    {
        var key = JobStatsKeys.Pct(JobStatsKeys.TypeMarker, TypeId, 50);

        JobStatsKeys.TryParsePct(key, out var dim, out var id, out var upperMs).ShouldBeTrue();
        dim.ShouldBe(JobStatsKeys.TypeMarker);
        id.ShouldBe(TypeId);
        upperMs.ShouldBe(50);

        // A pct key is a histogram row, never a count/error row — the lifetime parser must reject it.
        JobStatsKeys.TryParseTotal(key, out _, out _, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void BucketFor_ReturnsSmallestBoundAtOrAboveDuration()
    {
        JobStatsKeys.BucketFor(42).ShouldBe(50);
        JobStatsKeys.BucketFor(50).ShouldBe(50);
        JobStatsKeys.BucketFor(0).ShouldBe(5);
        JobStatsKeys.BucketFor(20_000).ShouldBe(int.MaxValue);
    }

    [TimedFact]
    public void History_RoundTripsTierAndBucketAndLifetimeParsersRejectIt()
    {
        FineSuffix.ShouldBe(":m5:2026-07-20-14-35");

        var key = JobStatsKeys.History(JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.FailedToken, FineSuffix);

        JobStatsKeys.TryParseHistory(key, out var dim, out var id, out var token, out var tier, out var bucket).ShouldBeTrue();
        dim.ShouldBe(JobStatsKeys.TypeMarker);
        id.ShouldBe(TypeId);
        token.ShouldBe(JobStatsKeys.FailedToken);
        tier.ShouldBe(MetricTier.Fine);
        bucket.ShouldBe(FineBucket);

        // A history key is neither a lifetime total, a lifetime pct, nor a pcth bucket — all must reject it.
        JobStatsKeys.TryParseTotal(key, out _, out _, out _).ShouldBeFalse();
        JobStatsKeys.TryParsePct(key, out _, out _, out _).ShouldBeFalse();
        JobStatsKeys.TryParsePctHistory(key, out _, out _, out _, out _, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void PctHistory_RoundTripsAndIsDisjointFromHist()
    {
        var suffix = MetricTiers.Suffix(MetricTier.Hourly, Ts, 5);
        var key = JobStatsKeys.PctHistory(JobStatsKeys.TypeMarker, TypeId, 50, suffix);

        JobStatsKeys.TryParsePctHistory(key, out var dim, out var id, out var upperMs, out var tier, out var bucket).ShouldBeTrue();
        dim.ShouldBe(JobStatsKeys.TypeMarker);
        id.ShouldBe(TypeId);
        upperMs.ShouldBe(50);
        tier.ShouldBe(MetricTier.Hourly);
        bucket.ShouldBe(new DateTime(2026, 7, 20, 14, 0, 0, DateTimeKind.Utc));

        JobStatsKeys.TryParseHistory(key, out _, out _, out _, out _, out _).ShouldBeFalse();
        JobStatsKeys.TryParsePct(key, out _, out _, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void AppTotal_RoundTripsApplicationDimensionIdAndToken()
    {
        var key = JobStatsKeys.AppTotal("reporting-api", JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.SucceededToken);

        JobStatsKeys.TryParseApp(key, out var app, out var dim, out var id, out var token).ShouldBeTrue();
        app.ShouldBe("reporting-api");
        dim.ShouldBe(JobStatsKeys.TypeMarker);
        id.ShouldBe(TypeId);
        token.ShouldBe(JobStatsKeys.SucceededToken);

        // An app total is NOT a history key.
        JobStatsKeys.TryParseAppHistory(key, out _, out _, out _, out _, out _, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void AppHistory_RoundTripsTierAndAppTotalParserRejectsIt()
    {
        var suffix = MetricTiers.Suffix(MetricTier.Daily, Ts, 5);
        var key = JobStatsKeys.AppHistory("reporting-api", JobStatsKeys.HandlerMarker, HandlerId, JobStatsKeys.DurationToken, suffix);

        JobStatsKeys.TryParseAppHistory(key, out var app, out var dim, out var id, out var token, out var tier, out var bucket).ShouldBeTrue();
        app.ShouldBe("reporting-api");
        dim.ShouldBe(JobStatsKeys.HandlerMarker);
        id.ShouldBe(HandlerId);
        token.ShouldBe(JobStatsKeys.DurationToken);
        tier.ShouldBe(MetricTier.Daily);
        bucket.ShouldBe(new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));

        JobStatsKeys.TryParseApp(key, out _, out _, out _, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void Sanitize_ReplacesColonsSoKeyStaysParseable()
    {
        var raw = "weird:id";
        var key = JobStatsKeys.Total(JobStatsKeys.TypeMarker, JobStatsKeys.Sanitize(raw), JobStatsKeys.SucceededToken);

        JobStatsKeys.TryParseTotal(key, out var dim, out var id, out var token).ShouldBeTrue();
        dim.ShouldBe(JobStatsKeys.TypeMarker);
        id.ShouldBe("weird-id");
        token.ShouldBe(JobStatsKeys.SucceededToken);
    }

    // Disjoint-namespace proof (back-compat). The existing job-stats family is stats:{outcome} +
    // stats:{outcome}:{hour}; readers gate on StartsWith("stats:succeeded:") / exact equality. The new family
    // must be provably invisible to those, and the new parsers must reject the old family.
    [TimedFact]
    public void NewKeys_AreDisjointFromExistingStatsFamily()
    {
        var lifetime = JobStatsKeys.Total(JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.SucceededToken);
        var app = JobStatsKeys.AppTotal("reporting-api", JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.SucceededToken);
        var hist = JobStatsKeys.History(JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.SucceededToken, FineSuffix);

        foreach (var key in new[] { lifetime, app, hist })
        {
            key.StartsWith("stats:succeeded:", StringComparison.Ordinal).ShouldBeFalse();
            key.StartsWith("stats:failed:", StringComparison.Ordinal).ShouldBeFalse();
            key.ShouldNotBe("stats:succeeded");
            key.ShouldNotBe("stats:failed");
        }
    }

    [TimedFact]
    public void NewParsers_RejectOldStatsKeys()
    {
        foreach (var old in new[] { "stats:succeeded", "stats:failed", "stats:deleted", "stats:requeued", "stats:succeeded:2026-07-20-14" })
        {
            JobStatsKeys.TryParseTotal(old, out _, out _, out _).ShouldBeFalse();
            JobStatsKeys.TryParsePct(old, out _, out _, out _).ShouldBeFalse();
            JobStatsKeys.TryParseHistory(old, out _, out _, out _, out _, out _).ShouldBeFalse();
            JobStatsKeys.TryParsePctHistory(old, out _, out _, out _, out _, out _).ShouldBeFalse();
            JobStatsKeys.TryParseApp(old, out _, out _, out _, out _).ShouldBeFalse();
            JobStatsKeys.TryParseAppHistory(old, out _, out _, out _, out _, out _, out _).ShouldBeFalse();
        }
    }

    [TimedFact]
    public void Build_TypeAndHandler_EmitsBothDimensionsWithTieredHistAndAppSlice()
    {
        var job = new Job { Type = TypeId, HandlerType = HandlerId };
        var suffix = MetricTiers.Suffix(MetricTier.Fine, Ts, 5);

        var counters = JobStatsKeys.Build(job, JobStatsKeys.SucceededToken, durationMs: 42.4, application: "reporting-api", tierSuffix: suffix);

        // TYPE dimension: lifetime count + dur + pct, tiered hist-count + pcth, app(count + dur).
        Sum(counters, JobStatsKeys.Total(JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.SucceededToken)).ShouldBe(1);
        Sum(counters, JobStatsKeys.Total(JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.DurationToken)).ShouldBe(42);
        Sum(counters, JobStatsKeys.Pct(JobStatsKeys.TypeMarker, TypeId, 50)).ShouldBe(1);
        Sum(counters, JobStatsKeys.History(JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.SucceededToken, suffix)).ShouldBe(1);
        Sum(counters, JobStatsKeys.PctHistory(JobStatsKeys.TypeMarker, TypeId, 50, suffix)).ShouldBe(1);
        Sum(counters, JobStatsKeys.AppTotal("reporting-api", JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.SucceededToken)).ShouldBe(1);
        Sum(counters, JobStatsKeys.AppTotal("reporting-api", JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.DurationToken)).ShouldBe(42);

        // HANDLER dimension emitted too.
        Sum(counters, JobStatsKeys.Total(JobStatsKeys.HandlerMarker, HandlerId, JobStatsKeys.SucceededToken)).ShouldBe(1);

        // The app slice deliberately carries NO histogram (lifetime pct or tiered pcth) — volume bound.
        counters.ShouldNotContain(c => c.Key.StartsWith(JobStatsKeys.AppPrefix, StringComparison.Ordinal) && c.Key.Contains(":pct:", StringComparison.Ordinal));
        counters.ShouldNotContain(c => c.Key.StartsWith(JobStatsKeys.AppPrefix, StringComparison.Ordinal) && c.Key.Contains(":pcth:", StringComparison.Ordinal));
    }

    [TimedFact]
    public void Build_NullHandler_EmitsTypeOnly()
    {
        var job = new Job { Type = TypeId, HandlerType = null };
        var suffix = MetricTiers.Suffix(MetricTier.Fine, Ts, 5);

        var counters = JobStatsKeys.Build(job, JobStatsKeys.SucceededToken, durationMs: 5, application: null, tierSuffix: suffix);

        counters.ShouldContain(c => c.Key.StartsWith($"{JobStatsKeys.Prefix}:{JobStatsKeys.TypeMarker}:", StringComparison.Ordinal));
        counters.ShouldNotContain(c => c.Key.StartsWith($"{JobStatsKeys.Prefix}:{JobStatsKeys.HandlerMarker}:", StringComparison.Ordinal));

        // application null ⇒ no per-app keys (behaviour unchanged when the feature is off).
        counters.ShouldNotContain(c => c.Key.StartsWith(JobStatsKeys.AppPrefix, StringComparison.Ordinal));
    }

    private static long Sum(IEnumerable<Counter> counters, string key)
        => counters.Where(c => string.Equals(c.Key, key, StringComparison.Ordinal)).Sum(c => (long)c.Value);
}
