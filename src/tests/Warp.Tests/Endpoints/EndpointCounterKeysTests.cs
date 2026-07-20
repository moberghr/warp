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
}
