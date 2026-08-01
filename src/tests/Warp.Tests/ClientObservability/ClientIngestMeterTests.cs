using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.ClientObservability;
using Warp.Http.ClientObservability;

namespace Warp.Tests.ClientObservability;

/// <summary>
/// The always-on client meters (§8.24) must fire from the ingest endpoint EVEN WHEN no recorder is registered
/// (the Otel-only sink shape) — the meter emission is independent of the DB recorder, and only allowlisted Core
/// Web Vitals become a <c>warp.client.vitals</c> tag (§8.27/§1.2). No recorder is registered here on purpose;
/// if a regression ever gated <c>RecordClientEvent</c>/<c>RecordClientVital</c> on the recorder, these fail.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class ClientIngestMeterTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private const string Path = "/warp/ingest";
    private const string Origin = "https://shop.test";

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<WarpClientObservabilityOptions>(o =>
        {
            o.AddIngestKey("shop", "pk_test");
            o.AllowedOrigins.Add(Origin);
            o.MaxEventsPerBatch = 10;
            o.RateLimitPerMinute = 100;
        });
        builder.Services.AddSingleton(x => new ClientIngestRateLimiter(
            x.GetRequiredService<IOptions<WarpClientObservabilityOptions>>().Value.RateLimitPerMinute,
            x.GetRequiredService<TimeProvider>()));

        // Deliberately NO IClientEventRecorder — the Otel-only shape.
        _app = builder.Build();
        _app.MapWarpClientObservability();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _client.Dispose();
    }

    [Fact]
    public async Task Post_WithNoRecorder_StillEmitsEventMeterAndAllowlistsVitals()
    {
        var eventCount = 0L;
        var vitalNames = new List<string>();

        using var events = CounterListener("warp.client.events", value => eventCount += value);
        using var vitals = VitalListener(vitalNames);

        await PostAsync(new
        {
            events = new object[]
            {
                new { type = "error", name = "TypeError", message = "boom" },
                new { type = "vital", name = "LCP", value = 1200.0 },
                new { type = "vital", name = "totally-made-up", value = 99.0 },
            },
        });

        // The event meter fired for every recognized event even though nothing was recorded to a DB.
        eventCount.ShouldBe(3);

        // Only the allowlisted vital became a warp.client.vitals sample; the arbitrary browser-sent name did not.
        vitalNames.ShouldContain("LCP");
        vitalNames.ShouldNotContain(x => x.Contains("made-up", StringComparison.OrdinalIgnoreCase));
    }

    private async Task PostAsync(object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(payload) };
        request.Headers.Add("x-warp-key", "pk_test");
        request.Headers.Add("Origin", Origin);

        (await _client.SendAsync(request, Xunit.TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
    }

    private static MeterListener CounterListener(string instrumentName, Action<long> onValue)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, instrumentName, StringComparison.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) => onValue(value));
        listener.Start();

        return listener;
    }

    private static MeterListener VitalListener(List<string> vitalNames)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (string.Equals(instrument.Meter.Name, "Warp", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, "warp.client.vitals", StringComparison.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, "vital", StringComparison.Ordinal) && tag.Value is string name)
                {
                    vitalNames.Add(name);
                }
            }
        });

        listener.Start();

        return listener;
    }
}
