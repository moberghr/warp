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
        // RateLimitPerMinute = 5: first batch of 3 ok (3 total), second batch of 3 would be 6 > 5 ⇒ 429.
        (await PostAsync("pk_test", Origin, new { events = Enumerable.Range(0, 3).Select(i => new { type = "log", message = i.ToString() }).ToArray() })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await PostAsync("pk_test", Origin, new { events = Enumerable.Range(0, 3).Select(i => new { type = "log", message = i.ToString() }).ToArray() })).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Get_ClientScript_IsServed()
    {
        var response = await _client.GetAsync(Path + "/client.js", Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
        var body = await response.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        body.ShouldContain("window.warp");
    }

    private async Task<HttpResponseMessage> PostAsync(string key, string origin, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(payload) };
        request.Headers.Add("x-warp-key", key);
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
