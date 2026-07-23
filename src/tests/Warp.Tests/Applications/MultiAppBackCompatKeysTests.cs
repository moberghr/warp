using Shouldly;
using Warp.Core.Adapters;
using Warp.Core.Endpoints;
using Warp.Core.Services;

namespace Warp.Tests.Applications;

/// <summary>
/// Batch 11 NoDb CONSOLIDATION of the disjoint-counter-key back-compat proof across ALL THREE per-app
/// families at once (§8.19 / §7): adapters (<c>adapter</c> → <c>adapter-app</c>), endpoints (<c>endpoint</c>
/// → <c>endpoint-app</c>), and job-execution stats (the OLD app-agnostic <c>stats:</c> family vs the NEW
/// <c>jobstat</c> / <c>jobstat-app</c> family). The exhaustive per-family round-trips live in
/// <see cref="PerAppMetricsTests"/> and <see cref="JobStatsKeysTests"/>; this class asserts only the ONE
/// cross-cutting invariant those establish separately — that an old-version reader sharing the
/// <c>Statistic</c> table on a rolling deploy can neither match nor mis-parse a new per-app key, and the new
/// parsers reject the old keys — so a regression to that guarantee in any single family fails here too.
/// </summary>
[Trait("Category", "NoDb")]
public class MultiAppBackCompatKeysTests
{
    private const string App = "reporting-api";
    private const string Hour = "2026-07-20-14";

    [TimedFact]
    public void NewPrefixes_AreDisjointFromOldColonDelimitedPrefixes()
    {
        // Every existing DB reader filters on StartsWith("{old-prefix}:") (colon boundary). The new prefixes
        // must break that filter so an old reader's WHERE never scoops a new key.
        (AdapterCounterKeys.AppPrefix + ":").StartsWith(AdapterCounterKeys.Prefix + ":", StringComparison.Ordinal).ShouldBeFalse();
        (EndpointCounterKeys.AppPrefix + ":").StartsWith(EndpointCounterKeys.Prefix + ":", StringComparison.Ordinal).ShouldBeFalse();

        // Job-stats replaced the legacy "stats:" family with a distinct "jobstat"/"jobstat-app" prefix.
        (JobStatsKeys.Prefix + ":").StartsWith("stats:", StringComparison.Ordinal).ShouldBeFalse();
        (JobStatsKeys.AppPrefix + ":").StartsWith("stats:", StringComparison.Ordinal).ShouldBeFalse();
    }

    [TimedFact]
    public void OldReaders_IgnoreNewPerAppKeys_AcrossAllThreeFamilies()
    {
        // Adapters: the app-agnostic lifetime + history parsers reject the per-app shapes.
        AdapterCounterKeys.TryParse(AdapterCounterKeys.AppTotal(App, "vendor", "success"), out _).ShouldBeFalse();
        AdapterCounterKeys.TryParseHistory(AdapterCounterKeys.AppHistory(App, "vendor", "failed", Hour), out _, out _, out _).ShouldBeFalse();

        // Endpoints: same, for the endpoint family.
        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");
        EndpointCounterKeys.TryParse(EndpointCounterKeys.AppTotal(App, route, "success"), out _).ShouldBeFalse();
        EndpointCounterKeys.TryParseHistory(EndpointCounterKeys.AppHistory(App, route, "failed", Hour), out _, out _, out _).ShouldBeFalse();

        // Job stats: the new keys never match the legacy "stats:" reader gates (exact + StartsWith).
        foreach (var key in NewJobStatKeys())
        {
            key.StartsWith("stats:succeeded:", StringComparison.Ordinal).ShouldBeFalse();
            key.StartsWith("stats:failed:", StringComparison.Ordinal).ShouldBeFalse();
            key.ShouldNotBe("stats:succeeded");
            key.ShouldNotBe("stats:failed");
        }
    }

    [TimedFact]
    public void NewParsers_RejectOldKeys_AcrossAllThreeFamilies()
    {
        // Adapters + endpoints: the per-app parser rejects the old app-agnostic total.
        AdapterCounterKeys.TryParseApp(AdapterCounterKeys.Total("vendor", "success"), out _, out _, out _).ShouldBeFalse();
        EndpointCounterKeys.TryParseApp(EndpointCounterKeys.Total("GET /orders", "success"), out _, out _, out _).ShouldBeFalse();

        // Job stats: every new parser rejects the legacy "stats:" family.
        foreach (var old in new[] { "stats:succeeded", "stats:failed", "stats:succeeded:" + Hour })
        {
            JobStatsKeys.TryParseTotal(old, out _, out _, out _).ShouldBeFalse();
            JobStatsKeys.TryParseApp(old, out _, out _, out _, out _).ShouldBeFalse();
        }
    }

    private static IEnumerable<string> NewJobStatKeys()
    {
        yield return JobStatsKeys.Total(JobStatsKeys.TypeMarker, "MyApp.Jobs.SendEmail", JobStatsKeys.SucceededToken);
        yield return JobStatsKeys.AppTotal(App, JobStatsKeys.TypeMarker, "MyApp.Jobs.SendEmail", JobStatsKeys.SucceededToken);
        yield return JobStatsKeys.History(JobStatsKeys.TypeMarker, "MyApp.Jobs.SendEmail", JobStatsKeys.SucceededToken, Hour);
    }
}
