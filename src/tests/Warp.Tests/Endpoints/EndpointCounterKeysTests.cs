using Shouldly;
using Warp.Core.Endpoints;
using Warp.Core.Enums;

namespace Warp.Tests.Endpoints;

/// <summary>
/// NoDb coverage for the inbound-endpoint counter-key layout — the inbound mirror of
/// <c>AdapterCoreTests</c>'s counter-key round-trip. Route normalisation (uppercase method +
/// constraint-stripping) and the build↔parse round-trip live in the same type
/// (<see cref="EndpointCounterKeys"/>), so a format change to one half that is not mirrored in the other
/// fails here rather than silently zeroing the dashboard (which drops unparseable keys).
/// </summary>
[Trait("Category", "NoDb")]
public class EndpointCounterKeysTests
{
    [TimedFact]
    public void NormalizeRoute_MethodAndInlineConstraints_UppercasesAndStrips()
    {
        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");

        route.ShouldBe("GET /orders/{id}");
    }

    [TimedFact]
    public void NormalizeTemplate_TemplateWithoutConstraints_Unchanged()
    {
        var template = EndpointCounterKeys.NormalizeTemplate("/orders/{id}");

        template.ShouldBe("/orders/{id}");
    }

    [TimedFact]
    public void TryParse_TotalKey_RoundTripsRouteWithSlashAndOutcome()
    {
        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");

        EndpointCounterKeys.TryParse(EndpointCounterKeys.Total(route, "success"), out var parsed).ShouldBeTrue();

        parsed.Route.ShouldBe("GET /orders/{id}");
        parsed.Dimension.ShouldBe(EndpointStatDimension.Total);
        parsed.Group.ShouldBe(string.Empty);
        parsed.Outcome.ShouldBe("success");
    }

    [TimedFact]
    public void TryParse_TotalDurationKey_RoundTripsWithDurationToken()
    {
        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");

        EndpointCounterKeys.TryParse(EndpointCounterKeys.Total(route, EndpointCounterKeys.DurationToken), out var parsed).ShouldBeTrue();

        parsed.Dimension.ShouldBe(EndpointStatDimension.Total);
        parsed.Outcome.ShouldBe(EndpointCounterKeys.DurationToken);
    }

    [TimedFact]
    public void TryParse_GroupKey_RoundTripsRouteGroupAndOutcome()
    {
        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");

        EndpointCounterKeys.TryParse(EndpointCounterKeys.Group(route, "shop-1", "failed"), out var parsed).ShouldBeTrue();

        parsed.Route.ShouldBe("GET /orders/{id}");
        parsed.Dimension.ShouldBe(EndpointStatDimension.Group);
        parsed.Group.ShouldBe("shop-1");
        parsed.Outcome.ShouldBe("failed");
    }

    [TimedFact]
    public void OutcomeToken_MapsOutcomeToTrailingSegment()
    {
        EndpointCounterKeys.OutcomeToken(AdapterCallOutcome.Success).ShouldBe("success");
        EndpointCounterKeys.OutcomeToken(AdapterCallOutcome.Failed).ShouldBe("failed");
    }

    [TimedFact]
    public void TryParse_NonEndpointKey_ReturnsFalse()
    {
        EndpointCounterKeys.TryParse("adapter:vendor:success", out _).ShouldBeFalse();
    }

    [TimedFact]
    public void Pct_BuildsHistogramKey_And_TryParseIgnoresIt()
    {
        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");
        var key = EndpointCounterKeys.Pct(route, 50);

        key.ShouldBe("endpoint:GET /orders/{id}:pct:50");

        // A pct key is a latency-histogram row, NOT a count/error row — TryParse must reject it so the
        // histogram counters never pollute the count/error StatSet.
        EndpointCounterKeys.TryParse(key, out _).ShouldBeFalse();

        // TryParsePct is the disjoint parser that DOES accept it, round-tripping the route + bucket bound.
        EndpointCounterKeys.TryParsePct(key, out var parsedRoute, out var upperMs).ShouldBeTrue();
        parsedRoute.ShouldBe("GET /orders/{id}");
        upperMs.ShouldBe(50);
    }

    [TimedFact]
    public void BucketFor_ReturnsSmallestBoundAtOrAboveDuration()
    {
        EndpointCounterKeys.BucketFor(42).ShouldBe(50);
        EndpointCounterKeys.BucketFor(50).ShouldBe(50);
        EndpointCounterKeys.BucketFor(0).ShouldBe(5);
        EndpointCounterKeys.BucketFor(20_000).ShouldBe(int.MaxValue);
    }

    [TimedFact]
    public void TryParsePct_NonPctKey_ReturnsFalse()
    {
        EndpointCounterKeys.TryParsePct(EndpointCounterKeys.Total("GET /orders", "success"), out _, out _).ShouldBeFalse();
        EndpointCounterKeys.TryParsePct("adapter:vendor:pct:50", out _, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void History_BuildsHourlyKey_RoundTripsRouteOutcomeAndHour()
    {
        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");
        var hour = EndpointCounterKeys.HourBucket(new DateTime(2026, 7, 20, 14, 37, 12, DateTimeKind.Utc));

        hour.ShouldBe("2026-07-20-14");
        var key = EndpointCounterKeys.History(route, "failed", hour);
        key.ShouldBe("endpoint:GET /orders/{id}:hist:failed:2026-07-20-14");

        EndpointCounterKeys.TryParseHistory(key, out var parsedRoute, out var outcome, out var parsedHour).ShouldBeTrue();
        parsedRoute.ShouldBe("GET /orders/{id}");
        outcome.ShouldBe("failed");
        parsedHour.ShouldBe(new DateTime(2026, 7, 20, 14, 0, 0, DateTimeKind.Utc));
    }

    [TimedFact]
    public void History_DoesNotPolluteLifetimeStats()
    {
        var route = EndpointCounterKeys.NormalizeRoute("get", "/orders/{id:int}");
        var key = EndpointCounterKeys.History(route, "success", "2026-07-20-14");

        // An hourly history key is neither a count/error row nor a latency-histogram row — both lifetime
        // parsers must reject it so it never inflates the totals or the percentiles.
        EndpointCounterKeys.TryParse(key, out _).ShouldBeFalse();
        EndpointCounterKeys.TryParsePct(key, out _, out _).ShouldBeFalse();
    }

    [TimedFact]
    public void NormalizeTemplate_LiteralColonInRoute_MadeColonFree_AndKeyRoundTrips()
    {
        // A custom-method route ("orders/{id}:export") leaves a literal ':' after constraint-stripping.
        // It must be made colon-free so the colon-delimited counter key parses (otherwise the endpoint
        // silently vanishes from the aggregate stats).
        var template = EndpointCounterKeys.NormalizeTemplate("/orders/{id:int}:export");
        template.ShouldNotContain(":");
        template.ShouldBe("/orders/{id}-export");

        var route = EndpointCounterKeys.NormalizeRoute("post", "/orders/{id:int}:export");
        EndpointCounterKeys.TryParse(EndpointCounterKeys.Total(route, "success"), out var parsed).ShouldBeTrue();
        parsed.Route.ShouldBe("POST /orders/{id}-export");
        parsed.Dimension.ShouldBe(EndpointStatDimension.Total);
        parsed.Outcome.ShouldBe("success");
    }

    [TimedFact]
    public void TryParseHistory_NonHistoryKey_ReturnsFalse()
    {
        EndpointCounterKeys.TryParseHistory(EndpointCounterKeys.Total("GET /orders", "success"), out _, out _, out _).ShouldBeFalse();
        EndpointCounterKeys.TryParseHistory(EndpointCounterKeys.Group("GET /orders", "shop-1", "failed"), out _, out _, out _).ShouldBeFalse();
    }
}
