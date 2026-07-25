using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Endpoints;
using Warp.Core.Enums;
using Warp.Core.Logging;
using Warp.Core.Observability;
using Warp.Http.Observability;
using Warp.Tests.Helpers;

namespace Warp.Tests.Observability;

/// <summary>
/// NoDb coverage for the Otel/Both recording sink: instead of a DB row (or a structured log), the captured
/// call detail is attached to the span Warp already emits — the adapter Client span and the ambient inbound
/// request span (§8.24). Under Database the span carries identity/outcome only. Enrichment fires only when the
/// span is being recorded (sampled in) so trace sampling governs the whole call/request coherently.
/// </summary>
[Trait("Category", "NoDb")]
public class SpanEnrichmentTests
{
    [TimedFact]
    public void Adapter_OtelSink_EnrichesClientSpan_WithCapturedDetail()
    {
        using var harness = new ActivityListenerHarness();
        var adapters = CreateAdapters(enrichSpanDetail: true);

        using (var scope = adapters.BeginCall("vendor", "GetOrders", "shop-eu"))
        {
            scope.SetStatusCode(200);
            scope.SetRequestSummary("GET /orders/{id}");
            scope.SetRequestHeaders("Accept: application/json");
            scope.SetResponseHeaders("Content-Type: application/json");
            scope.SetRequestBody("{\"id\":42}");
            scope.SetResponseBody("{\"ok\":true}");
            scope.SetCorrelation("delivery-42");
            scope.SetTag("region", "eu");
            scope.Succeed();
        }

        var activity = harness.FirstByName("vendor.GetOrders").ShouldNotBeNull();
        activity.GetTagItem(WarpTelemetryAttributes.WarpAdapterOutcome).ShouldBe("Success");
        activity.GetTagItem(WarpTelemetryAttributes.WarpAdapterStatusCode).ShouldBe(200);
        activity.GetTagItem(WarpTelemetryAttributes.WarpAdapterCorrelationId).ShouldBe("delivery-42");
        activity.GetTagItem(WarpTelemetryAttributes.WarpAdapterRequestSummary).ShouldBe("GET /orders/{id}");
        activity.GetTagItem(WarpTelemetryAttributes.WarpAdapterRequestHeaders).ShouldBe("Accept: application/json");
        activity.GetTagItem(WarpTelemetryAttributes.WarpAdapterResponseHeaders).ShouldBe("Content-Type: application/json");
        activity.GetTagItem(WarpTelemetryAttributes.WarpAdapterRequestBody).ShouldBe("{\"id\":42}");
        activity.GetTagItem(WarpTelemetryAttributes.WarpAdapterResponseBody).ShouldBe("{\"ok\":true}");
        activity.GetTagItem("warp.adapter.tag.region").ShouldBe("eu");
    }

    [TimedFact]
    public void Adapter_DatabaseSink_LeavesSpanDetailOff()
    {
        using var harness = new ActivityListenerHarness();
        var adapters = CreateAdapters(enrichSpanDetail: false);

        using (var scope = adapters.BeginCall("vendor", "GetOrders"))
        {
            scope.SetStatusCode(200);
            scope.SetRequestBody("{\"id\":42}");
            scope.Succeed();
        }

        var activity = harness.FirstByName("vendor.GetOrders").ShouldNotBeNull();

        // Identity + outcome are always-on telemetry; the captured DETAIL is not attached under Database.
        activity.GetTagItem(WarpTelemetryAttributes.WarpAdapterOutcome).ShouldBe("Success");
        activity.GetTagItem(WarpTelemetryAttributes.WarpAdapterRequestBody).ShouldBeNull();
        activity.GetTagItem(WarpTelemetryAttributes.WarpAdapterStatusCode).ShouldBeNull();
    }

    [TimedFact]
    public async Task Endpoint_OtelSink_EnrichesRequestSpan_WithCapturedDetail()
    {
        using var harness = new ActivityListenerHarness();
        var middleware = CreateMiddleware(RecordingSink.Otel, WriteOkResponse);
        var context = CreateContext();

        using var activity = WarpTelemetry.ActivitySource.StartActivity("inbound");
        activity.ShouldNotBeNull();

        await middleware.InvokeAsync(context);

        activity.GetTagItem(WarpTelemetryAttributes.WarpEndpointRoute).ShouldBe("GET /orders/{id}");
        activity.GetTagItem(WarpTelemetryAttributes.WarpEndpointStatusCode).ShouldBe(200);
        activity.GetTagItem(WarpTelemetryAttributes.WarpEndpointOutcome).ShouldBe("Success");
        activity.GetTagItem(WarpTelemetryAttributes.WarpEndpointClientIp).ShouldBe("203.0.113.4");
        activity.GetTagItem(WarpTelemetryAttributes.WarpEndpointUserAgent).ShouldBe("curl/8");
        activity.GetTagItem(WarpTelemetryAttributes.WarpEndpointRequestBody).ShouldBe("{\"x\":1}");
        activity.GetTagItem(WarpTelemetryAttributes.WarpEndpointResponseBody).ShouldBe("{\"ok\":true}");
    }

    [TimedFact]
    public async Task Endpoint_DatabaseSink_LeavesSpanDetailOff()
    {
        using var harness = new ActivityListenerHarness();
        var middleware = CreateMiddleware(RecordingSink.Database, WriteOkResponse);
        var context = CreateContext();

        using var activity = WarpTelemetry.ActivitySource.StartActivity("inbound");
        activity.ShouldNotBeNull();

        await middleware.InvokeAsync(context);

        activity.GetTagItem(WarpTelemetryAttributes.WarpEndpointRequestBody).ShouldBeNull();
        activity.GetTagItem(WarpTelemetryAttributes.WarpEndpointRoute).ShouldBeNull();
    }

    private static WarpAdapters CreateAdapters(bool enrichSpanDetail) =>
        new(
            new AdapterRegistry(),
            new NoopAdapterRecorder(),
            TimeProvider.System,
            NullLogger<WarpAdapters>.Instance,
            enrichSpanDetail ? [new AdapterRecordingSettings(true)] : []);

    private static WarpInboundObservabilityMiddleware CreateMiddleware(RecordingSink sink, RequestDelegate next) =>
        new(
            next,
            [],
            Options.Create(new WarpEndpointObservabilityOptions
            {
                Sink = sink,
                CaptureRequestBodies = CaptureMode.Always,
                CaptureResponseBodies = CaptureMode.Always,
                CaptureHeaders = CaptureMode.Always,
            }),
            Options.Create(new WarpConfiguration()),
            TimeProvider.System);

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"x\":1}"));
        context.Request.ContentLength = context.Request.Body.Length;
        context.Request.Headers.UserAgent = "curl/8";
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.4");
        context.SetEndpoint(new Endpoint(
            null,
            new EndpointMetadataCollection(new WarpEndpointIdentity("GET", "/orders/{id}", "GET /orders/{id}")),
            "test"));

        return context;
    }

    private static async Task WriteOkResponse(HttpContext context)
    {
        context.Response.StatusCode = 200;
        await context.Response.WriteAsync("{\"ok\":true}");
    }

    private sealed class NoopAdapterRecorder : IAdapterCallRecorder
    {
        public bool Record(AdapterCallRecord record) => true;
    }
}
