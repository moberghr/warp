using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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

    [TimedFact]
    public async Task RequestBody_MultibyteExceedsCap_TruncatesOnCharBoundary_NoReplacementChar()
    {
        // A raw Encoding.UTF8.GetString over a byte prefix that cuts a multibyte char mid-sequence surfaces
        // U+FFFD. Ten 'é' (2 bytes each = 20 bytes) with an 8-byte cap must truncate on a char boundary with
        // the marker — never a replacement char, and never over the cap.
        var (app, client, recorder) = await CreateHost(o =>
        {
            o.CaptureRequestBodies = CaptureMode.Always;
            o.MaxCapturedBodySize = 8;
        });

        try
        {
            using var content = new StringContent(new string('é', 10));
            var response = await client.PostAsync("/probe", content, Xunit.TestContext.Current.CancellationToken);

            response.IsSuccessStatusCode.ShouldBeTrue();

            var body = recorder.Records.ShouldHaveSingleItem().RequestBody;
            body.ShouldNotBeNull();
            body.ShouldNotContain("�");
            body.ShouldEndWith("…");
            System.Text.Encoding.UTF8.GetByteCount(body).ShouldBeLessThanOrEqualTo(8);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task ResponseBody_CaptureAlways_CapturesResponsePayload()
    {
        var (app, client, recorder) = await CreateHost(o => o.CaptureResponseBodies = CaptureMode.Always);

        try
        {
            var response = await client.GetAsync("/text", Xunit.TestContext.Current.CancellationToken);

            // The caller still receives the full, unmodified response (the capture stream is write-through).
            (await response.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken)).ShouldBe("response-payload");
            recorder.Records.ShouldHaveSingleItem().ResponseBody.ShouldBe("response-payload");
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task ResponseBody_CaptureOnFailure_Success_DoesNotCapture()
    {
        var (app, client, recorder) = await CreateHost(o => o.CaptureResponseBodies = CaptureMode.OnFailure);

        try
        {
            var response = await client.GetAsync("/text", Xunit.TestContext.Current.CancellationToken);

            response.IsSuccessStatusCode.ShouldBeTrue();
            recorder.Records.ShouldHaveSingleItem().ResponseBody.ShouldBeNull();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task ResponseBody_MultibyteExceedsCap_TruncatesOnCharBoundary_NoReplacementChar()
    {
        var (app, client, recorder) = await CreateHost(o =>
        {
            o.CaptureResponseBodies = CaptureMode.Always;
            o.MaxCapturedBodySize = 8;
        });

        try
        {
            var response = await client.GetAsync("/accented", Xunit.TestContext.Current.CancellationToken);

            // The caller reads the full 20-byte body; only the stored prefix is truncated on a char boundary.
            (await response.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken)).Length.ShouldBe(10);

            var body = recorder.Records.ShouldHaveSingleItem().ResponseBody;
            body.ShouldNotBeNull();
            body.ShouldNotContain("�");
            body.ShouldEndWith("…");
            System.Text.Encoding.UTF8.GetByteCount(body).ShouldBeLessThanOrEqualTo(8);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task WarpEndpoint_UnhandledException_Rethrows()
    {
        // The middleware must stay transparent to the exception (rethrow, never swallow). The record itself
        // is deferred to Response.OnCompleted, which a real server (Kestrel/IIS) fires after writing its 500
        // — but TestServer's abort path never completes the response, so the record is unobservable here; the
        // 500-mapped test below covers the deferred Failed record.
        var (app, client, _) = await CreateHost();

        try
        {
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await client.GetAsync("/throw", Xunit.TestContext.Current.CancellationToken));
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task WarpEndpoint_UnhandledException_Kestrel_RecordsServerWritten500()
    {
        // The production shape of a truly unhandled exception: no host exception middleware at all. A real
        // server (unlike TestServer, see above) converts it to a 500 and completes the response, so the
        // deferred record lands with the server-written wire status.
        var (app, client, recorder) = await CreateHost(useKestrel: true);

        try
        {
            var response = await client.GetAsync("/throw", Xunit.TestContext.Current.CancellationToken);

            ((int)response.StatusCode).ShouldBe(500);

            var record = await WaitForRecordAsync(recorder);
            record.RouteTemplate.ShouldBe("/throw");
            record.Outcome.ShouldBe(AdapterCallOutcome.Failed);
            record.StatusCode.ShouldBe(500);
            record.ExceptionType.ShouldBe(typeof(InvalidOperationException).FullName);
            record.ExceptionMessage.ShouldBe("unhandled-boom");

            // The trace join key survives the deferral even on the fully-unhandled path, where nothing but
            // the server itself handles the exception.
            record.TraceId.ShouldNotBeNull();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task WarpEndpoint_ExceptionMappedTo500Upstream_RecordsFailedWithWireStatus()
    {
        // A host exception handler that answers 500 (the same shape Kestrel produces for a fully unhandled
        // exception): the deferred record derives Failed from the final wire status and keeps the exception.
        var (app, client, recorder) = await CreateHost(mapExceptionsToStatus: 500);

        try
        {
            var response = await client.GetAsync("/throw", Xunit.TestContext.Current.CancellationToken);

            ((int)response.StatusCode).ShouldBe(500);

            var record = await WaitForRecordAsync(recorder);
            record.RouteTemplate.ShouldBe("/throw");
            record.Outcome.ShouldBe(AdapterCallOutcome.Failed);
            record.StatusCode.ShouldBe(500);
            record.ExceptionType.ShouldBe(typeof(InvalidOperationException).FullName);
            record.ExceptionMessage.ShouldBe("unhandled-boom");
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task WarpEndpoint_ExceptionMappedTo401Upstream_RecordsWireStatusAsClientError()
    {
        // The handler throws; the HOST's exception middleware (registered outside Warp) maps it to 401 AFTER
        // Warp's frame has unwound. The recorded status must be the wire 401 — not the never-written 200
        // default — and a 4xx is a client error (Success) per the §8.21 stance, with the exception kept on
        // the row for diagnosis.
        var (app, client, recorder) = await CreateHost(mapExceptionsToStatus: 401);

        try
        {
            var response = await client.GetAsync("/throw", Xunit.TestContext.Current.CancellationToken);

            ((int)response.StatusCode).ShouldBe(401);

            var record = await WaitForRecordAsync(recorder);
            record.StatusCode.ShouldBe(401);
            record.Outcome.ShouldBe(AdapterCallOutcome.Success);
            record.ExceptionType.ShouldBe(typeof(InvalidOperationException).FullName);
            record.ExceptionMessage.ShouldBe("unhandled-boom");
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task WarpEndpoint_ExceptionMappedTo401Upstream_StillCarriesTraceId()
    {
        // The deferred record runs from Response.OnCompleted, after the app delegate returned — assert the
        // ambient request Activity is still flowing there, because TraceId is the join key the unified trace
        // view uses to tie this call to the jobs it spawned. Losing it would be a silent regression.
        var (app, client, recorder) = await CreateHost(mapExceptionsToStatus: 401);

        try
        {
            var response = await client.GetAsync("/throw", Xunit.TestContext.Current.CancellationToken);

            ((int)response.StatusCode).ShouldBe(401);
            (await WaitForRecordAsync(recorder)).TraceId.ShouldNotBeNull();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task RecordCallsFailuresOnly_ExceptionMappedTo401_NotSuppressed()
    {
        // An exception-bearing call is never suppressed: even though the wire outcome is a 4xx client error,
        // the row carries the exception detail a FailuresOnly host is asking to keep.
        var (app, client, recorder) = await CreateHost(o => o.RecordCalls = CallRecording.FailuresOnly, mapExceptionsToStatus: 401);

        try
        {
            var response = await client.GetAsync("/throw", Xunit.TestContext.Current.CancellationToken);

            ((int)response.StatusCode).ShouldBe(401);
            (await WaitForRecordAsync(recorder)).SuppressLog.ShouldBeFalse();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Headers_CaptureOnFailure_ExceptionMappedTo401_CapturesHeaders()
    {
        // OnFailure treats exception-bearing calls as capture-worthy even when the outcome is Success (4xx).
        var (app, client, recorder) = await CreateHost(o => o.CaptureHeaders = CaptureMode.OnFailure, mapExceptionsToStatus: 401);

        try
        {
            var response = await client.GetAsync("/throw", Xunit.TestContext.Current.CancellationToken);

            ((int)response.StatusCode).ShouldBe(401);
            (await WaitForRecordAsync(recorder)).RequestHeaders.ShouldNotBeNull();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task WarpEndpoint_ExceptionAfterResponseStarted_RecordsFailedWithStartedStatus()
    {
        // A throw AFTER the response started means the client received a truncated 2xx — that IS a failure,
        // and the started status is the real wire status, so the record is written inline: Failed + 200.
        var (app, client, recorder) = await CreateHost();

        try
        {
            try
            {
                using var response = await client.GetAsync("/midstream", HttpCompletionOption.ResponseHeadersRead, Xunit.TestContext.Current.CancellationToken);
                await response.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
            }
            catch (HttpRequestException)
            {
                // The aborted body read is the client-visible symptom — the record is what's under test.
            }

            var record = await WaitForRecordAsync(recorder);
            record.RouteTemplate.ShouldBe("/midstream");
            record.Outcome.ShouldBe(AdapterCallOutcome.Failed);
            record.StatusCode.ShouldBe(200);
            record.ExceptionMessage.ShouldBe("midstream-boom");
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task Headers_CaptureAlways_RedactsDenylistedHeader()
    {
        var (app, client, recorder) = await CreateHost(o => o.CaptureHeaders = CaptureMode.Always);

        try
        {
            using var content = new StringContent("x");
            content.Headers.Add("X-Api-Key", "super-secret-key");
            var response = await client.PostAsync("/hdr", content, Xunit.TestContext.Current.CancellationToken);

            response.IsSuccessStatusCode.ShouldBeTrue();

            var headers = recorder.Records.ShouldHaveSingleItem().RequestHeaders;
            headers.ShouldNotBeNull();
            headers.ShouldContain("X-Api-Key: ***");
            headers.ShouldNotContain("super-secret-key");
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task WarpEndpoint_ClientError4xx_CountedSuccess()
    {
        // 4xx is a client error, not a server failure — it is classified Success (outcome Failed is reserved
        // for status >= 500 or an unhandled exception) so the error rate stays a meaningful server-health
        // signal. The status code is still recorded verbatim.
        var (app, client, recorder) = await CreateHost();

        try
        {
            var response = await client.GetAsync("/notfound", Xunit.TestContext.Current.CancellationToken);

            ((int)response.StatusCode).ShouldBe(404);

            var record = recorder.Records.ShouldHaveSingleItem();
            record.Outcome.ShouldBe(AdapterCallOutcome.Success);
            record.StatusCode.ShouldBe(404);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task RecordCallsFailuresOnly_Success_SuppressesRow()
    {
        // FailuresOnly skips the raw row for successful calls (counters are still written, so aggregates stay
        // exact) — the volume knob for chatty endpoints. The record is handed over flagged SuppressLog.
        var (app, client, recorder) = await CreateHost(o => o.RecordCalls = CallRecording.FailuresOnly);

        try
        {
            var response = await client.PostAsync("/probe", new StringContent("x"), Xunit.TestContext.Current.CancellationToken);

            response.IsSuccessStatusCode.ShouldBeTrue();
            recorder.Records.ShouldHaveSingleItem().SuppressLog.ShouldBeTrue();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task RecordCallsFailuresOnly_ServerError_NotSuppressed()
    {
        var (app, client, recorder) = await CreateHost(o => o.RecordCalls = CallRecording.FailuresOnly);

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
    public async Task RecorderThrows_RequestStillSucceeds()
    {
        // Recording is diagnostics, never a request failure — a recorder that throws must not surface to the
        // caller (RecordAsync swallows anything the capture/record path throws).
        var (app, client, _) = await CreateHost(recorderOverride: new ThrowingRecorder());

        try
        {
            var response = await client.PostAsync("/probe", new StringContent("x"), Xunit.TestContext.Current.CancellationToken);

            response.IsSuccessStatusCode.ShouldBeTrue();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private static async Task<(WebApplication App, HttpClient Client, CapturingRecorder Recorder)> CreateHost(
        Action<WarpEndpointObservabilityOptions>? configure = null,
        IEndpointCallRecorder? recorderOverride = null,
        int? mapExceptionsToStatus = null,
        bool useKestrel = false)
    {
        var builder = WebApplication.CreateBuilder();

        // Kestrel (loopback, ephemeral port) is only used by the truly-unhandled-exception test: TestServer's
        // abort path never completes the response, so the deferred record is unobservable there, while a real
        // server converts the exception to a 500 and fires OnCompleted.
        if (useKestrel)
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        }
        else
        {
            builder.WebHost.UseTestServer();
        }

        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        var recorder = new CapturingRecorder();
        builder.Services.AddSingleton<IEndpointCallRecorder>(recorderOverride ?? recorder);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<WarpConfiguration>(_ => { });
        builder.Services.Configure<WarpEndpointObservabilityOptions>(o => configure?.Invoke(o));

        var app = builder.Build();

        // A host-owned exception-to-status middleware registered OUTSIDE the Warp middleware — the common
        // production shape (domain exceptions mapped to 401/404/...) that writes the real wire status only
        // AFTER Warp's frame has unwound.
        if (mapExceptionsToStatus is not null)
        {
            app.Use(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (InvalidOperationException)
                {
                    context.Response.StatusCode = mapExceptionsToStatus.Value;
                    await context.Response.WriteAsync("handled-by-host");
                }
            });
        }

        app.UseRouting();
        app.UseWarpHttpObservability();

        app.MapPost("/probe", () => Results.Ok())
            .WithMetadata(new WarpEndpointIdentity("POST", "/probe", "Probe"));
        app.MapGet("/boom", () => Results.StatusCode(500))
            .WithMetadata(new WarpEndpointIdentity("GET", "/boom", "Boom"));
        app.MapGet("/plain", () => Results.Ok());
        app.MapGet("/text", () => Results.Text("response-payload"))
            .WithMetadata(new WarpEndpointIdentity("GET", "/text", "Text"));
        app.MapGet("/accented", () => Results.Text(new string('é', 10)))
            .WithMetadata(new WarpEndpointIdentity("GET", "/accented", "Accented"));
        app.MapGet("/throw", void () => throw new InvalidOperationException("unhandled-boom"))
            .WithMetadata(new WarpEndpointIdentity("GET", "/throw", "Throw"));
        app.MapGet("/midstream", async context =>
        {
            await context.Response.WriteAsync("partial");
            await context.Response.Body.FlushAsync();

            throw new InvalidOperationException("midstream-boom");
        }).WithMetadata(new WarpEndpointIdentity("GET", "/midstream", "MidStream"));
        app.MapPost("/hdr", () => Results.Ok())
            .WithMetadata(new WarpEndpointIdentity("POST", "/hdr", "Hdr"));
        app.MapGet("/notfound", () => Results.NotFound())
            .WithMetadata(new WarpEndpointIdentity("GET", "/notfound", "NotFound"));

        await app.StartAsync(CancellationToken.None);

        if (!useKestrel)
        {
            return (app, app.GetTestClient(), recorder);
        }

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        return (app, new HttpClient { BaseAddress = new Uri(address) }, recorder);
    }

    // Exception-path records are deferred to Response.OnCompleted, which can land after the client's await
    // resumes — poll (yield, no delay) until the single record arrives; [TimedFact] bounds the wait.
    private static async Task<EndpointCallRecord> WaitForRecordAsync(CapturingRecorder recorder)
    {
        while (recorder.Records.Count == 0)
        {
            await Task.Yield();
        }

        return recorder.Records.ShouldHaveSingleItem();
    }

    private sealed class ThrowingRecorder : IEndpointCallRecorder
    {
        public bool Record(EndpointCallRecord record) => throw new InvalidOperationException("recorder blew up");
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
