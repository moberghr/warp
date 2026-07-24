using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Endpoints;
using Warp.Core.Logging;
using Warp.Http.Observability;
using Warp.Tests.Adapters;

namespace Warp.Tests.Observability;

/// <summary>
/// Part 2 (aggregate METRICS via OTel meters), NoDb slice. The always-on <c>warp.adapter.*</c> /
/// <c>warp.endpoint.*</c> / <c>warp.job.execution.*</c> meters carry the low-cardinality dashboard
/// dimensions (adapter/route, operation, outcome, application) so an OTel-sink user can reconstruct
/// count / error-rate / latency / per-app without the DB. Meters emit unconditionally (independent of the
/// recording Sink). The <c>application</c> tag comes from the process origin
/// (<see cref="WarpTelemetry.ApplicationName"/> for adapters / <see cref="WarpConfiguration.ApplicationName"/>
/// for endpoints) — a shared static, so each test sets and resets it in a <c>finally</c> (§ApplicationTracingTests).
/// </summary>
[Trait("Category", "NoDb")]
[Collection("Telemetry")]
public class OTelAggregateMetricsTests
{
    [TimedFact]
    public void AdapterCall_ApplicationNameSet_CallsCounterCarriesApplicationOperationOutcomeTags()
    {
        WarpTelemetry.ApplicationName = "orders-api";
        try
        {
            var adapterName = "app-tag-calls";
            var measurements = new List<IReadOnlyDictionary<string, object?>>();
            using var listener = AdapterTestHarness.CaptureLong("warp.adapter.calls", adapterName, measurements);
            var (adapters, _, _) = AdapterTestHarness.CreateAdapters(adapterName: adapterName);

            adapters.BeginCall(adapterName, "GetOrders").Succeed();

            var tags = measurements.ShouldHaveSingleItem();
            tags[WarpTelemetryAttributes.AdapterMeterOperation].ShouldBe("GetOrders");
            tags[WarpTelemetryAttributes.AdapterMeterOutcome].ShouldBe("Success");
            tags[WarpTelemetryAttributes.MeterApplication].ShouldBe("orders-api");
        }
        finally
        {
            WarpTelemetry.ApplicationName = null;
        }
    }

    [TimedFact]
    public void AdapterCall_ApplicationNameUnset_CallsCounterHasNoApplicationTag()
    {
        WarpTelemetry.ApplicationName = null;

        var adapterName = "no-app-tag-calls";
        var measurements = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = AdapterTestHarness.CaptureLong("warp.adapter.calls", adapterName, measurements);
        var (adapters, _, _) = AdapterTestHarness.CreateAdapters(adapterName: adapterName);

        adapters.BeginCall(adapterName, "GetOrders").Succeed();

        var tags = measurements.ShouldHaveSingleItem();
        tags.ContainsKey(WarpTelemetryAttributes.MeterApplication).ShouldBeFalse();
    }

    [TimedFact]
    public async Task EndpointCall_Success_EmitsCallsAndDurationMeters_WithRouteOutcomeApplicationTags()
    {
        var route = $"/otel-endpoint-{Guid.NewGuid():N}";
        var expectedRouteTag = $"GET {route}";

        var totals = new List<IReadOnlyDictionary<string, object?>>();
        var durations = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = CaptureEndpointMeters(expectedRouteTag, totals, durations);

        var (app, client) = await CreateHost(route, statusCode: 200, applicationName: "orders-api");
        try
        {
            var response = await client.GetAsync(route, Xunit.TestContext.Current.CancellationToken);
            response.IsSuccessStatusCode.ShouldBeTrue();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }

        var total = totals.ShouldHaveSingleItem();
        total[WarpTelemetryAttributes.EndpointMeterRoute].ShouldBe(expectedRouteTag);
        total[WarpTelemetryAttributes.EndpointMeterOutcome].ShouldBe("Success");
        total[WarpTelemetryAttributes.MeterApplication].ShouldBe("orders-api");

        durations.ShouldHaveSingleItem()[WarpTelemetryAttributes.EndpointMeterRoute].ShouldBe(expectedRouteTag);
    }

    [TimedFact]
    public async Task EndpointCall_ServerError_EmitsCallsMeter_WithFailedOutcome()
    {
        var route = $"/otel-endpoint-fail-{Guid.NewGuid():N}";
        var expectedRouteTag = $"GET {route}";

        var totals = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = CaptureEndpointMeters(expectedRouteTag, totals, []);

        var (app, client) = await CreateHost(route, statusCode: 500, applicationName: null);
        try
        {
            var response = await client.GetAsync(route, Xunit.TestContext.Current.CancellationToken);
            ((int)response.StatusCode).ShouldBe(500);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }

        var total = totals.ShouldHaveSingleItem();
        total[WarpTelemetryAttributes.EndpointMeterOutcome].ShouldBe("Failed");

        // ApplicationName unset ⇒ no application tag on the meter.
        total.ContainsKey(WarpTelemetryAttributes.MeterApplication).ShouldBeFalse();
    }

    [TimedFact]
    public void RecordJobExecution_RoutedMessage_IncludesHandlerTag()
    {
        var jobType = $"type-{Guid.NewGuid():N}";
        var handlerType = $"handler-{Guid.NewGuid():N}";

        var totals = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = CaptureJobExecutionTotal(jobType, totals);

        WarpTelemetry.RecordJobExecution(jobType, handlerType, "succeeded", 12.0, "orders-api");

        var tags = totals.ShouldHaveSingleItem();
        tags[WarpTelemetryAttributes.JobMeterType].ShouldBe(jobType);
        tags[WarpTelemetryAttributes.JobMeterHandler].ShouldBe(handlerType);
        tags[WarpTelemetryAttributes.JobMeterOutcome].ShouldBe("succeeded");
        tags[WarpTelemetryAttributes.MeterApplication].ShouldBe("orders-api");
    }

    [TimedFact]
    public void RecordJobExecution_NoHandlerNoApplication_OmitsThoseTags()
    {
        var jobType = $"type-{Guid.NewGuid():N}";

        var totals = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = CaptureJobExecutionTotal(jobType, totals);

        WarpTelemetry.RecordJobExecution(jobType, handlerType: null, "failed", durationMs: null, application: null);

        var tags = totals.ShouldHaveSingleItem();
        tags[WarpTelemetryAttributes.JobMeterOutcome].ShouldBe("failed");
        tags.ContainsKey(WarpTelemetryAttributes.JobMeterHandler).ShouldBeFalse();
        tags.ContainsKey(WarpTelemetryAttributes.MeterApplication).ShouldBeFalse();
    }

    private static MeterListener CaptureEndpointMeters(
        string routeTag,
        List<IReadOnlyDictionary<string, object?>> totals,
        List<IReadOnlyDictionary<string, object?>> durations)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                    && (string.Equals(instrument.Name, "warp.endpoint.calls", StringComparison.Ordinal)
                        || string.Equals(instrument.Name, "warp.endpoint.duration", StringComparison.Ordinal)))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, WarpTelemetryAttributes.EndpointMeterRoute, routeTag))
            {
                totals.Add(Snapshot(tags));
            }
        });

        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, WarpTelemetryAttributes.EndpointMeterRoute, routeTag))
            {
                durations.Add(Snapshot(tags));
            }
        });

        listener.Start();

        return listener;
    }

    private static MeterListener CaptureJobExecutionTotal(string jobType, List<IReadOnlyDictionary<string, object?>> totals)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, "warp.job.execution.total", StringComparison.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            if (HasTag(tags, WarpTelemetryAttributes.JobMeterType, jobType))
            {
                totals.Add(Snapshot(tags));
            }
        });

        listener.Start();

        return listener;
    }

    private static async Task<(WebApplication App, HttpClient Client)> CreateHost(string route, int statusCode, string? applicationName)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        builder.Services.AddSingleton<IEndpointCallRecorder>(new NoopRecorder());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<WarpConfiguration>(o => o.ApplicationName = applicationName);
        builder.Services.Configure<WarpEndpointObservabilityOptions>(_ => { });

        var app = builder.Build();

        app.UseRouting();
        app.UseWarpHttpObservability();

        app.MapGet(route, () => Results.StatusCode(statusCode))
            .WithMetadata(new WarpEndpointIdentity("GET", route, "Op"));

        await app.StartAsync(CancellationToken.None);

        return (app, app.GetTestClient());
    }

    private static Dictionary<string, object?> Snapshot(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            snapshot[tag.Key] = tag.Value;
        }

        return snapshot;
    }

    private static bool HasTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key, string value)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal)
                && string.Equals(tag.Value?.ToString(), value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class NoopRecorder : IEndpointCallRecorder
    {
        public bool Record(EndpointCallRecord record) => true;
    }
}
