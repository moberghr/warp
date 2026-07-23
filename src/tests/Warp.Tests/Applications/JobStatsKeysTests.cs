using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Services;

namespace Warp.Tests.Applications;

/// <summary>
/// NoDb coverage for the per-job-TYPE / per-HANDLER execution counter-key layout (§8.19 multi-app
/// observability). Proves the new keys round-trip through their own parsers AND — the disjoint-namespace
/// guarantee (same discipline as Batch 5) — that the EXISTING <c>stats:</c> readers/parsers provably IGNORE
/// the new keys, and the new parsers provably reject the old <c>stats:</c> keys. A drift between the builders
/// and parsers (both in <see cref="JobStatsKeys"/>) fails here rather than silently zeroing the dashboard.
/// </summary>
[Trait("Category", "NoDb")]
public class JobStatsKeysTests
{
    private const string TypeId = "MyApp.Jobs.SendEmail, MyApp, Version=1.0.0.0";
    private const string HandlerId = "MyApp.Jobs.SendEmailHandler, MyApp, Version=1.0.0.0";

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
    public void History_RoundTripsAndLifetimeParsersRejectIt()
    {
        var hour = JobStatsKeys.HourBucket(new DateTime(2026, 7, 20, 14, 37, 12, DateTimeKind.Utc));
        hour.ShouldBe("2026-07-20-14");

        var key = JobStatsKeys.History(JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.FailedToken, hour);

        JobStatsKeys.TryParseHistory(key, out var dim, out var id, out var token, out var parsedHour).ShouldBeTrue();
        dim.ShouldBe(JobStatsKeys.TypeMarker);
        id.ShouldBe(TypeId);
        token.ShouldBe(JobStatsKeys.FailedToken);
        parsedHour.ShouldBe(new DateTime(2026, 7, 20, 14, 0, 0, DateTimeKind.Utc));

        // An hourly key is neither a lifetime total nor a histogram bucket — both must reject it.
        JobStatsKeys.TryParseTotal(key, out _, out _, out _).ShouldBeFalse();
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
        JobStatsKeys.TryParseAppHistory(key, out _, out _, out _, out _, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void AppHistory_RoundTripsAndAppTotalParserRejectsIt()
    {
        var key = JobStatsKeys.AppHistory("reporting-api", JobStatsKeys.HandlerMarker, HandlerId, JobStatsKeys.DurationToken, "2026-07-20-14");

        JobStatsKeys.TryParseAppHistory(key, out var app, out var dim, out var id, out var token, out var hour).ShouldBeTrue();
        app.ShouldBe("reporting-api");
        dim.ShouldBe(JobStatsKeys.HandlerMarker);
        id.ShouldBe(HandlerId);
        token.ShouldBe(JobStatsKeys.DurationToken);
        hour.ShouldBe(new DateTime(2026, 7, 20, 14, 0, 0, DateTimeKind.Utc));

        JobStatsKeys.TryParseApp(key, out _, out _, out _, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void Sanitize_ReplacesColonsSoKeyStaysParseable()
    {
        // A pathological id containing a colon would corrupt the colon-delimited key; Sanitize replaces it
        // so the key still parses (id names never contain ':' in practice, so this is a guarantee, not a
        // transformation that matters for real inputs).
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
        var hourly = JobStatsKeys.History(JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.SucceededToken, "2026-07-20-14");

        foreach (var key in new[] { lifetime, app, hourly })
        {
            // The GetStatsHistory prefix gate — the only reader that assigns succeeded/failed semantics to
            // hourly keys — never matches a new key.
            key.StartsWith("stats:succeeded:", StringComparison.Ordinal).ShouldBeFalse();
            key.StartsWith("stats:failed:", StringComparison.Ordinal).ShouldBeFalse();

            // The metric-card exact-key reads never equal a new key.
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
            JobStatsKeys.TryParseHistory(old, out _, out _, out _, out _).ShouldBeFalse();
            JobStatsKeys.TryParseApp(old, out _, out _, out _, out _).ShouldBeFalse();
            JobStatsKeys.TryParseAppHistory(old, out _, out _, out _, out _, out _).ShouldBeFalse();
        }
    }

    [TimedFact]
    public void Build_TypeAndHandler_EmitsBothDimensionsWithAppSlice()
    {
        var job = new Job { Type = TypeId, HandlerType = HandlerId };

        var counters = JobStatsKeys.Build(job, JobStatsKeys.SucceededToken, durationMs: 42.4, application: "reporting-api", hourBucket: "2026-07-20-14");

        // TYPE dimension: count + hist-count + dur + hist-dur + pct + app(count + hist-count + dur + hist-dur).
        Sum(counters, JobStatsKeys.Total(JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.SucceededToken)).ShouldBe(1);
        Sum(counters, JobStatsKeys.Total(JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.DurationToken)).ShouldBe(42);
        Sum(counters, JobStatsKeys.Pct(JobStatsKeys.TypeMarker, TypeId, 50)).ShouldBe(1);
        Sum(counters, JobStatsKeys.History(JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.SucceededToken, "2026-07-20-14")).ShouldBe(1);
        Sum(counters, JobStatsKeys.AppTotal("reporting-api", JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.SucceededToken)).ShouldBe(1);
        Sum(counters, JobStatsKeys.AppTotal("reporting-api", JobStatsKeys.TypeMarker, TypeId, JobStatsKeys.DurationToken)).ShouldBe(42);

        // HANDLER dimension emitted too.
        Sum(counters, JobStatsKeys.Total(JobStatsKeys.HandlerMarker, HandlerId, JobStatsKeys.SucceededToken)).ShouldBe(1);

        // The app slice deliberately carries NO histogram bucket (volume bound).
        counters.ShouldNotContain(c => c.Key.StartsWith(JobStatsKeys.AppPrefix, StringComparison.Ordinal) && c.Key.Contains(":pct:", StringComparison.Ordinal));
    }

    [TimedFact]
    public void Build_NullHandler_EmitsTypeOnly()
    {
        var job = new Job { Type = TypeId, HandlerType = null };

        var counters = JobStatsKeys.Build(job, JobStatsKeys.SucceededToken, durationMs: 5, application: null, hourBucket: "2026-07-20-14");

        counters.ShouldContain(c => c.Key.StartsWith($"{JobStatsKeys.Prefix}:{JobStatsKeys.TypeMarker}:", StringComparison.Ordinal));
        counters.ShouldNotContain(c => c.Key.StartsWith($"{JobStatsKeys.Prefix}:{JobStatsKeys.HandlerMarker}:", StringComparison.Ordinal));

        // application null ⇒ no per-app keys (behaviour unchanged when the feature is off).
        counters.ShouldNotContain(c => c.Key.StartsWith(JobStatsKeys.AppPrefix, StringComparison.Ordinal));
    }

    private static long Sum(IEnumerable<Counter> counters, string key)
        => counters.Where(c => string.Equals(c.Key, key, StringComparison.Ordinal)).Sum(c => (long)c.Value);
}
