using Shouldly;
using Warp.Core.Metrics;

namespace Warp.Tests.Metrics;

/// <summary>
/// Pins the two HTTP presets of <see cref="WarpMetricsOptions"/> (R10): the meter each one collects, the single
/// instrument it narrows that meter to, and the exact <c>KeepTags</c> allowlist. The allowlist is the privacy
/// control — a widened one would persist <c>url.full</c>/<c>url.path</c> or <c>server.port</c> values — so it is
/// asserted as a set, not by containment. The presets are configuration strings only; Warp takes no ASP.NET Core
/// dependency, so the names themselves are part of the contract.
/// </summary>
[Trait("Category", "NoDb")]
public class WarpMetricsOptionsTests
{
    [TimedFact]
    public void CollectAspNetCoreHttp_CollectsOnlyTheServerRequestDurationWithTheRouteMethodStatusAllowlist()
    {
        var options = new WarpMetricsOptions().CollectAspNetCoreHttp();

        var meter = options.Meters.ShouldHaveSingleItem();
        meter.Key.ShouldBe("Microsoft.AspNetCore.Hosting");
        meter.Value.CollectAll.ShouldBeFalse();
        meter.Value.InstrumentNames.ShouldBe(["http.server.request.duration"]);

        var (tags, buckets) = options.Resolve("Microsoft.AspNetCore.Hosting", "http.server.request.duration");

        tags.ShouldNotBeNull();
        tags.Order(StringComparer.Ordinal).ShouldBe(["http.request.method", "http.response.status_code", "http.route"]);
        buckets.ShouldBeNull();
    }

    [TimedFact]
    public void CollectHttpClient_CollectsOnlyTheClientRequestDurationWithTheMethodServerStatusErrorAllowlist()
    {
        var options = new WarpMetricsOptions().CollectHttpClient();

        var meter = options.Meters.ShouldHaveSingleItem();
        meter.Key.ShouldBe("System.Net.Http");
        meter.Value.CollectAll.ShouldBeFalse();
        meter.Value.InstrumentNames.ShouldBe(["http.client.request.duration"]);

        var (tags, buckets) = options.Resolve("System.Net.Http", "http.client.request.duration");

        tags.ShouldNotBeNull();
        tags.Order(StringComparer.Ordinal).ShouldBe(["error.type", "http.request.method", "http.response.status_code", "server.address"]);
        buckets.ShouldBeNull();
    }

    [TimedFact]
    public void Presets_OtherInstrumentsOfTheirMeters_AreNotCollected()
    {
        // Each preset narrows its meter to one instrument: active-request or connection gauges on the same meter stay off.
        var options = new WarpMetricsOptions().CollectAspNetCoreHttp().CollectHttpClient();

        options.Meters["Microsoft.AspNetCore.Hosting"].Matches("http.server.active_requests").ShouldBeFalse();
        options.Meters["System.Net.Http"].Matches("http.client.open_connections").ShouldBeFalse();
        options.Meters["Microsoft.AspNetCore.Hosting"].Matches("http.server.request.duration").ShouldBeTrue();
        options.Meters["System.Net.Http"].Matches("http.client.request.duration").ShouldBeTrue();
    }

    [TimedFact]
    public void ByVersion_OptsIn_AndIsOffByDefault()
    {
        new WarpMetricsOptions().CollectByVersion.ShouldBeFalse();
        new WarpMetricsOptions().ByVersion().CollectByVersion.ShouldBeTrue();
    }

    [TimedFact]
    public void PresetConstants_NameTheStandardMetersAndInstruments()
    {
        WarpMetricsOptions.AspNetCoreHostingMeter.ShouldBe("Microsoft.AspNetCore.Hosting");
        WarpMetricsOptions.AspNetCoreRequestDuration.ShouldBe("http.server.request.duration");
        WarpMetricsOptions.HttpClientMeter.ShouldBe("System.Net.Http");
        WarpMetricsOptions.HttpClientRequestDuration.ShouldBe("http.client.request.duration");
    }
}
