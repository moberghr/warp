using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Endpoints;
using Warp.Core.Enums;
using Warp.Http.Observability;

namespace Warp.Tests.Endpoints;

/// <summary>
/// NoDb coverage for the inbound observability middleware using a TestHost <c>WebApplication</c> (same
/// shape as <c>DashboardAuthTests</c> / the adapter endpoint host). The middleware only observes endpoints
/// carrying a <see cref="WarpEndpointIdentity"/>, records duration/outcome/status/caller metadata and
/// captured payloads, and hands a completed record to the lossy <see cref="IEndpointCallRecorder"/> — which
/// a capturing test double collects. Recording never blocks or fails the request.
/// </summary>
[Trait("Category", "NoDb")]
public class EndpointMiddlewareTests
{
    [TimedFact]
    public async Task WarpEndpoint_SuccessfulRequest_RecordsIdentityStatusIpAndBody()
    {
        var (app, client, recorder) = await CreateHost(o =>
        {
            o.CaptureRequestBodies = CaptureMode.Always;
            o.UseForwardedForIp = true;
        });

        try
        {
            using var content = new StringContent("hello-body");
            content.Headers.Add("X-Forwarded-For", "203.0.113.7");
            var response = await client.PostAsync("/probe", content, Xunit.TestContext.Current.CancellationToken);

            response.IsSuccessStatusCode.ShouldBeTrue();

            var record = recorder.Records.ShouldHaveSingleItem();
            record.Method.ShouldBe("POST");
            record.RouteTemplate.ShouldBe("/probe");
            record.Operation.ShouldBe("Probe");
            record.Outcome.ShouldBe(AdapterCallOutcome.Success);
            record.StatusCode.ShouldBe(200);
            record.RemoteIp.ShouldBe("203.0.113.7");
            record.RequestBody.ShouldBe("hello-body");
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task NonWarpEndpoint_NoIdentityMetadata_NotRecorded()
    {
        var (app, client, recorder) = await CreateHost();

        try
        {
            var response = await client.GetAsync("/plain", Xunit.TestContext.Current.CancellationToken);

            response.IsSuccessStatusCode.ShouldBeTrue();
            recorder.Records.ShouldBeEmpty();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task WarpEndpoint_ServerError_RecordsFailedOutcome()
    {
        var (app, client, recorder) = await CreateHost();

        try
        {
            var response = await client.GetAsync("/boom", Xunit.TestContext.Current.CancellationToken);

            ((int)response.StatusCode).ShouldBe(500);

            var record = recorder.Records.ShouldHaveSingleItem();
            record.RouteTemplate.ShouldBe("/boom");
            record.Outcome.ShouldBe(AdapterCallOutcome.Failed);
            record.StatusCode.ShouldBe(500);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task SampleRateZero_Success_SuppressesLogRow()
    {
        var (app, client, recorder) = await CreateHost(o => o.SampleRate = 0.0);

        try
        {
            var response = await client.PostAsync("/probe", new StringContent("x"), Xunit.TestContext.Current.CancellationToken);

            response.IsSuccessStatusCode.ShouldBeTrue();

            // Counters are still written by the flusher; the row is suppressed. The record is handed over
            // flagged SuppressLog (the recorder captures every record regardless of the flag).
            recorder.Records.ShouldHaveSingleItem().SuppressLog.ShouldBeTrue();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task SampleRateZero_ServerError_NotSuppressed()
    {
        var (app, client, recorder) = await CreateHost(o => o.SampleRate = 0.0);

        try
        {
            var response = await client.GetAsync("/boom", Xunit.TestContext.Current.CancellationToken);

            ((int)response.StatusCode).ShouldBe(500);
            recorder.Records.ShouldHaveSingleItem().SuppressLog.ShouldBeFalse();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task SampleRateOne_Success_NotSuppressed()
    {
        // The keep-all default writes every successful row — no behaviour change for existing hosts.
        var (app, client, recorder) = await CreateHost();

        try
        {
            var response = await client.PostAsync("/probe", new StringContent("x"), Xunit.TestContext.Current.CancellationToken);

            response.IsSuccessStatusCode.ShouldBeTrue();
            recorder.Records.ShouldHaveSingleItem().SuppressLog.ShouldBeFalse();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task ForceCapture_SampleRateZero_Success_WritesRowAndCapturesBodyEvenWhenTierNone()
    {
        // ForceCapture returns true → the row is written despite SampleRate=0, and the request body + headers
        // are captured even though every capture tier is None.
        var (app, client, recorder) = await CreateHost(o =>
        {
            o.SampleRate = 0.0;
            o.CaptureRequestBodies = CaptureMode.None;
            o.CaptureResponseBodies = CaptureMode.None;
            o.CaptureHeaders = CaptureMode.None;
            o.ForceCapture = _ => true;
        });

        try
        {
            var response = await client.PostAsync("/probe", new StringContent("forced-body"), Xunit.TestContext.Current.CancellationToken);

            response.IsSuccessStatusCode.ShouldBeTrue();

            var record = recorder.Records.ShouldHaveSingleItem();
            record.SuppressLog.ShouldBeFalse();
            record.RequestBody.ShouldBe("forced-body");
            record.RequestHeaders.ShouldNotBeNull();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private static async Task<(WebApplication App, HttpClient Client, CapturingRecorder Recorder)> CreateHost(
        Action<WarpEndpointObservabilityOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        var recorder = new CapturingRecorder();
        builder.Services.AddSingleton<IEndpointCallRecorder>(recorder);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<WarpConfiguration>(_ => { });
        builder.Services.Configure<WarpEndpointObservabilityOptions>(o => configure?.Invoke(o));

        var app = builder.Build();

        app.UseRouting();
        app.UseWarpHttpObservability();

        app.MapPost("/probe", () => Results.Ok())
            .WithMetadata(new WarpEndpointIdentity("POST", "/probe", "Probe"));
        app.MapGet("/boom", () => Results.StatusCode(500))
            .WithMetadata(new WarpEndpointIdentity("GET", "/boom", "Boom"));
        app.MapGet("/plain", () => Results.Ok());

        await app.StartAsync(CancellationToken.None);

        return (app, app.GetTestClient(), recorder);
    }

    private sealed class CapturingRecorder : IEndpointCallRecorder
    {
        private readonly Lock _gate = new();
        private readonly List<EndpointCallRecord> _records = [];

        public IReadOnlyList<EndpointCallRecord> Records
        {
            get
            {
                lock (_gate)
                {
                    return [.. _records];
                }
            }
        }

        public bool Record(EndpointCallRecord record)
        {
            lock (_gate)
            {
                _records.Add(record);
            }

            return true;
        }
    }
}
