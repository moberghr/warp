using Shouldly;
using Warp.Core.Adapters;
using Warp.Core.Endpoints;

namespace Warp.Tests.Applications;

/// <summary>
/// NoDb coverage for the DISJOINT per-application counter-key family (§8.19 multi-app observability).
/// The whole point of the new namespace is cross-version safety: an old-version deployment reading the
/// shared <c>Statistic</c> table must NEVER see or mis-attribute a per-app key. These tests prove three
/// things: (a) the new keys round-trip through the new parsers; (b) EVERY existing adapter/endpoint
/// parser rejects a new per-app key (the back-compat proof); and (c) the same route under two apps
/// produces two distinct endpoint keys (application joins the endpoint identity). The formatters and
/// parsers live in the same type, so a format change to one half that is not mirrored in the other fails
/// here rather than silently zeroing the dashboard.
/// </summary>
[Trait("Category", "NoDb")]
public class PerAppMetricsTests
{
    // ---- (a) new per-app keys round-trip through the new parsers ----
    [TimedFact]
    public void AdapterAppTotal_RoundTripsApplicationAdapterAndOutcome()
    {
        var key = AdapterCounterKeys.AppTotal("reporting-api", "vendor", "success");

        key.ShouldBe("adapter-app:reporting-api:vendor:success");

        AdapterCounterKeys.TryParseApp(key, out var app, out var adapter, out var outcome).ShouldBeTrue();
        app.ShouldBe("reporting-api");
        adapter.ShouldBe("vendor");
        outcome.ShouldBe("success");
    }

    [TimedFact]
    public void AdapterAppTotal_DurationToken_RoundTrips()
    {
        var key = AdapterCounterKeys.AppTotal("reporting-api", "vendor", AdapterCounterKeys.DurationToken);

        AdapterCounterKeys.TryParseApp(key, out _, out _, out var outcome).ShouldBeTrue();
        outcome.ShouldBe(AdapterCounterKeys.DurationToken);
    }

    [TimedFact]
    public void AdapterAppHistory_RoundTripsApplicationAdapterOutcomeAndHour()
    {
        var hour = AdapterCounterKeys.HourBucket(new DateTime(2026, 7, 20, 14, 37, 12, DateTimeKind.Utc));
        var key = AdapterCounterKeys.AppHistory("reporting-api", "vendor", "failed", hour);

        key.ShouldBe("adapter-app:reporting-api:vendor:hist:failed:2026-07-20-14");

        AdapterCounterKeys.TryParseAppHistory(key, out var app, out var adapter, out var outcome, out var parsedHour).ShouldBeTrue();
        app.ShouldBe("reporting-api");
        adapter.ShouldBe("vendor");
        outcome.ShouldBe("failed");
        parsedHour.ShouldBe(new DateTime(2026, 7, 20, 14, 0, 0, DateTimeKind.Utc));
    }

    [TimedFact]
    public void AdapterAppParsers_AreDisjoint_TotalVsHistory()
    {
        var total = AdapterCounterKeys.AppTotal("reporting-api", "vendor", "success");
        var history = AdapterCounterKeys.AppHistory("reporting-api", "vendor", "success", "2026-07-20-14");

        // A total key is not a history key and vice-versa — each parser rejects the other's shape.
        AdapterCounterKeys.TryParseApp(history, out _, out _, out _).ShouldBeFalse();
        AdapterCounterKeys.TryParseAppHistory(total, out _, out _, out _, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void EndpointAppTotal_RoundTripsApplicationRouteAndOutcome()
    {
        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");
        var key = EndpointCounterKeys.AppTotal("reporting-api", route, "success");

        key.ShouldBe("endpoint-app:reporting-api:GET /orders/{id}:success");

        EndpointCounterKeys.TryParseApp(key, out var app, out var parsedRoute, out var outcome).ShouldBeTrue();
        app.ShouldBe("reporting-api");
        parsedRoute.ShouldBe("GET /orders/{id}");
        outcome.ShouldBe("success");
    }

    [TimedFact]
    public void EndpointAppHistory_RoundTripsApplicationRouteOutcomeAndHour()
    {
        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");
        var hour = EndpointCounterKeys.HourBucket(new DateTime(2026, 7, 20, 14, 37, 12, DateTimeKind.Utc));
        var key = EndpointCounterKeys.AppHistory("reporting-api", route, "failed", hour);

        key.ShouldBe("endpoint-app:reporting-api:GET /orders/{id}:hist:failed:2026-07-20-14");

        EndpointCounterKeys.TryParseAppHistory(key, out var app, out var parsedRoute, out var outcome, out var parsedHour).ShouldBeTrue();
        app.ShouldBe("reporting-api");
        parsedRoute.ShouldBe("GET /orders/{id}");
        outcome.ShouldBe("failed");
        parsedHour.ShouldBe(new DateTime(2026, 7, 20, 14, 0, 0, DateTimeKind.Utc));
    }

    // ---- (b) back-compat proof: EVERY existing parser rejects a new per-app key ----
    [TimedFact]
    public void ExistingAdapterParsers_RejectPerAppKeys()
    {
        var appTotal = AdapterCounterKeys.AppTotal("reporting-api", "vendor", "success");
        var appHistory = AdapterCounterKeys.AppHistory("reporting-api", "vendor", "failed", "2026-07-20-14");

        // The existing lifetime/pct/history parsers gate on parts[0] == "adapter" (exact first-segment
        // equality). "adapter-app" is a DIFFERENT first segment, so all three reject — an old-version
        // reader can never fold a per-app key into the app-agnostic totals / percentiles / history.
        AdapterCounterKeys.TryParse(appTotal, out _).ShouldBeFalse();
        AdapterCounterKeys.TryParse(appHistory, out _).ShouldBeFalse();
        AdapterCounterKeys.TryParsePct(appTotal, out _, out _).ShouldBeFalse();
        AdapterCounterKeys.TryParseHistory(appHistory, out _, out _, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void ExistingEndpointParsers_RejectPerAppKeys()
    {
        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");
        var appTotal = EndpointCounterKeys.AppTotal("reporting-api", route, "success");
        var appHistory = EndpointCounterKeys.AppHistory("reporting-api", route, "failed", "2026-07-20-14");

        EndpointCounterKeys.TryParse(appTotal, out _).ShouldBeFalse();
        EndpointCounterKeys.TryParse(appHistory, out _).ShouldBeFalse();
        EndpointCounterKeys.TryParsePct(appTotal, out _, out _).ShouldBeFalse();
        EndpointCounterKeys.TryParseHistory(appHistory, out _, out _, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void PerAppPrefixes_DoNotStartWithExistingColonDelimitedPrefix()
    {
        // The existing DB readers filter on StartsWith("adapter:") / StartsWith("endpoint:") (colon
        // boundary). The disjoint prefix must NOT match that filter — the hyphen breaks it at index 7/8.
        AdapterCounterKeys.AppPrefix.ShouldBe("adapter-app");
        (AdapterCounterKeys.AppPrefix + ":").StartsWith(AdapterCounterKeys.Prefix + ":", StringComparison.Ordinal).ShouldBeFalse();

        EndpointCounterKeys.AppPrefix.ShouldBe("endpoint-app");
        (EndpointCounterKeys.AppPrefix + ":").StartsWith(EndpointCounterKeys.Prefix + ":", StringComparison.Ordinal).ShouldBeFalse();
    }

    [TimedFact]
    public void NewParsers_RejectOldFormatKeys()
    {
        // Symmetry: the new per-app parsers must also reject the OLD app-agnostic keys so the two
        // namespaces never bleed into each other.
        AdapterCounterKeys.TryParseApp(AdapterCounterKeys.Total("vendor", "success"), out _, out _, out _).ShouldBeFalse();
        EndpointCounterKeys.TryParseApp(EndpointCounterKeys.Total("GET /orders", "success"), out _, out _, out _).ShouldBeFalse();
    }

    // ---- (c) endpoint identity split: same route under two apps → two distinct keys ----
    [TimedFact]
    public void EndpointAppTotal_SameRouteTwoApps_ProducesDistinctKeys()
    {
        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");

        var keyA = EndpointCounterKeys.AppTotal("app-a", route, "success");
        var keyB = EndpointCounterKeys.AppTotal("app-b", route, "success");

        keyA.ShouldNotBe(keyB);

        EndpointCounterKeys.TryParseApp(keyA, out var appA, out var routeA, out _).ShouldBeTrue();
        EndpointCounterKeys.TryParseApp(keyB, out var appB, out var routeB, out _).ShouldBeTrue();

        // Same route identity, different application — the application is part of the endpoint identity.
        routeA.ShouldBe(routeB);
        appA.ShouldBe("app-a");
        appB.ShouldBe("app-b");
    }
}
