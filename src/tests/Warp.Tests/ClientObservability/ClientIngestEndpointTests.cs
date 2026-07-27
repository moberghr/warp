using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.ClientObservability;
using Warp.Core.Enums;
using Warp.Http.ClientObservability;

namespace Warp.Tests.ClientObservability;

/// <summary>
/// NoDb TestHost coverage for the public ingest endpoint (§8.27): DSN key resolution (header + 401),
/// CORS origin allowlist, batch/rate caps, redaction, and the browser-script route. Uses a capturing recorder
/// (no DB), so it isolates the HTTP binding from the flusher.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class ClientIngestEndpointTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private CapturingRecorder _recorder = null!;

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
            o.MaxEventsPerBatch = 2;
            o.RateLimitPerMinute = 5;
        });
        builder.Services.AddSingleton(x => new ClientIngestRateLimiter(
            x.GetRequiredService<IOptions<WarpClientObservabilityOptions>>().Value.RateLimitPerMinute,
            x.GetRequiredService<TimeProvider>()));
        _recorder = new CapturingRecorder();
        builder.Services.AddSingleton<IClientEventRecorder>(_recorder);

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
    public async Task Post_ValidKeyAndOrigin_RecordsWithTrustedApplication()
    {
        var response = await PostAsync("pk_test", Origin, new
        {
            session = "s1",
            events = new[] { new { type = "error", name = "TypeError", message = "boom" } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldContain(Origin);

        var record = _recorder.Records.ShouldHaveSingleItem();
        record.Application.ShouldBe("shop");     // trusted, from the key mapping
        record.Type.ShouldBe(ClientEventType.Error);
        record.Name.ShouldBe("TypeError");
    }

    [Fact]
    public async Task Post_UnknownKey_ReturnsUnauthorized()
    {
        var response = await PostAsync("pk_wrong", Origin, new { events = new[] { new { type = "log", message = "x" } } });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _recorder.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_DisallowedOrigin_ReturnsForbidden()
    {
        var response = await PostAsync("pk_test", "https://evil.test", new { events = new[] { new { type = "log", message = "x" } } });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _recorder.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_RedactsDenylistedProperties()
    {
        await PostAsync("pk_test", Origin, new
        {
            events = new[] { new { type = "event", name = "login", props = new { password = "hunter2", user = "amy" } } },
        });

        var record = _recorder.Records.ShouldHaveSingleItem();
        var props = record.Properties.ShouldNotBeNull();
        props.ShouldContain("[redacted]");
        props.ShouldNotContain("hunter2");
        props.ShouldContain("amy");
    }

    [Fact]
    public async Task Post_RedactsDenylistedProperties_NestedNotJustTopLevel()
    {
        await PostAsync("pk_test", Origin, new
        {
            events = new[]
            {
                new { type = "event", name = "signup", props = new { user = new { name = "amy", password = "hunter2" }, tokens = new[] { new { authorization = "Bearer abc" } } } },
            },
        });

        var record = _recorder.Records.ShouldHaveSingleItem();
        var props = record.Properties.ShouldNotBeNull();

        // A secret nested one (object) or two (array of objects) levels deep must still be redacted (§1.2).
        props.ShouldNotContain("hunter2");
        props.ShouldNotContain("Bearer abc");
        props.ShouldContain("[redacted]");
        props.ShouldContain("amy");     // a non-denylisted nested value is preserved
    }

    [Fact]
    public async Task Post_BeyondBatchCap_DropsExtraEvents()
    {
        await PostAsync("pk_test", Origin, new
        {
            events = new[]
            {
                new { type = "log", message = "1" },
                new { type = "log", message = "2" },
                new { type = "log", message = "3" },
            },
        });

        // MaxEventsPerBatch = 2 — the third is dropped.
        _recorder.Records.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Post_ExceedsRateLimit_ReturnsTooManyRequests()
    {
        // Rate limit is per-request per caller IP (RateLimitPerMinute = 5): the 6th request in the window is 429.
        for (var i = 0; i < 5; i++)
        {
            (await PostAsync("pk_test", Origin, OneLog())).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await PostAsync("pk_test", Origin, OneLog())).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Post_SameOrigin_AllowedWithoutAllowlistEntry()
    {
        // Origin == the server's own scheme://host is same-origin — accepted even though it isn't allowlisted.
        var sameOrigin = $"{_client.BaseAddress!.Scheme}://{_client.BaseAddress.Authority}";

        var response = await PostAsync("pk_test", sameOrigin, OneLog());

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _recorder.Records.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Post_KeyInBody_AuthorizesWhenHeaderAbsent()
    {
        // sendBeacon can't set the x-warp-key header, so the key may travel in the body.
        var response = await PostAsync(key: null, Origin, new { key = "pk_test", events = new[] { new { type = "log", message = "beacon" } } });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _recorder.Records.ShouldHaveSingleItem().Application.ShouldBe("shop");
    }

    [Fact]
    public async Task Post_UnrecognizedType_IsDroppedNotRecorded()
    {
        var response = await PostAsync("pk_test", Origin, new { events = new[] { new { type = "warning", message = "unknown type" } } });

        // Accepted (never fails the caller) but the unparseable-type event is dropped, not recorded.
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _recorder.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_CraftedOutOfRangeTimestamp_DoesNotFail()
    {
        var response = await PostAsync("pk_test", Origin, new { events = new[] { new { type = "log", message = "x", ts = long.MaxValue } } });

        // A crafted ts must never fault the request; the event is still recorded with a sane timestamp.
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _recorder.Records.ShouldHaveSingleItem().Timestamp.ShouldBeLessThanOrEqualTo(DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Post_OversizedMessage_IsTruncated()
    {
        var huge = new string('x', 20_000);   // exceeds MaxCapturedBodySize (8 KB)

        await PostAsync("pk_test", Origin, new { events = new[] { new { type = "log", message = huge } } });

        var record = _recorder.Records.ShouldHaveSingleItem();
        record.Message!.Length.ShouldBeLessThan(huge.Length);
    }

    [Fact]
    public async Task Post_OversizedBody_ReturnsPayloadTooLarge()
    {
        var huge = new string('y', 200_000);   // exceeds MaxIngestBytes (64 KB)
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(new { events = new[] { new { type = "log", message = huge } } }) };
        request.Headers.Add("x-warp-key", "pk_test");

        var response = await _client.SendAsync(request, Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Options_Preflight_SetsCorsHeaders()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, Path);
        request.Headers.Add("Origin", Origin);

        var response = await _client.SendAsync(request, Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldContain(Origin);
        response.Headers.GetValues("Access-Control-Allow-Methods").ShouldContain(v => v.Contains("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_RequestEvent_ParsesTraceIdToGuid()
    {
        var traceHex = "0af7651916cd43dd8448eb211c80319c";   // valid W3C trace id (32 hex)

        await PostAsync("pk_test", Origin, new
        {
            events = new[] { new { type = "request", name = "GET", url = "/api/orders", value = 42, traceId = traceHex } },
        });

        var record = _recorder.Records.ShouldHaveSingleItem();
        record.Type.ShouldBe(ClientEventType.Request);
        record.TraceId.ShouldBe(Guid.ParseExact(traceHex, "N"));   // joins EndpointCallLog.TraceId
    }

    [Fact]
    public async Task Post_InvalidTraceId_DegradesToNull()
    {
        await PostAsync("pk_test", Origin, new { events = new[] { new { type = "request", name = "GET", traceId = "not-a-trace" } } });

        _recorder.Records.ShouldHaveSingleItem().TraceId.ShouldBeNull();
    }

    [Fact]
    public async Task Post_MalformedJson_ReturnsBadRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new StringContent("{ this is not json ", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-warp-key", "pk_test");
        request.Headers.Add("Origin", Origin);

        var response = await _client.SendAsync(request, Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _recorder.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_EmptyEvents_IsAcceptedNoOp()
    {
        var response = await PostAsync("pk_test", Origin, new { events = Array.Empty<object>() });

        // An empty batch is a valid no-op (the beacon fired with nothing to send) — accepted, nothing recorded.
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _recorder.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_WhenNoIngestKeysConfigured_ReturnsNotFound()
    {
        // A host that enabled the endpoint but configured no DSN key must not silently accept writes.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<WarpClientObservabilityOptions>(o => o.AllowedOrigins.Add(Origin));
        builder.Services.AddSingleton(x => new ClientIngestRateLimiter(
            x.GetRequiredService<IOptions<WarpClientObservabilityOptions>>().Value.RateLimitPerMinute,
            x.GetRequiredService<TimeProvider>()));

        await using var app = builder.Build();
        app.MapWarpClientObservability();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(OneLog()) };
        request.Headers.Add("x-warp-key", "pk_test");
        request.Headers.Add("Origin", Origin);

        var response = await client.SendAsync(request, Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static object OneLog() => new { events = new[] { new { type = "log", message = "x" } } };

    [Fact]
    public async Task Get_ClientScript_IsServed()
    {
        var response = await _client.GetAsync(Path + "/client.js", Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
        var body = await response.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        body.ShouldContain("window.warp");
    }

    private async Task<HttpResponseMessage> PostAsync(string? key, string origin, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(payload) };
        if (key is not null)
        {
            request.Headers.Add("x-warp-key", key);
        }

        request.Headers.Add("Origin", origin);

        return await _client.SendAsync(request, Xunit.TestContext.Current.CancellationToken);
    }

    private sealed class CapturingRecorder : IClientEventRecorder
    {
        public List<ClientEventRecord> Records { get; } = [];

        public bool Record(ClientEventRecord record)
        {
            Records.Add(record);

            return true;
        }
    }
}
