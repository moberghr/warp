using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Http;
using Warp.Tests.TestData;

namespace Warp.Tests.Http;

[Trait("Category", "NoDb")]
public sealed class StreamEndpointTests
{
    [TimedFact]
    public async Task Stream_Returns200WithEventStreamContentType()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var resp = await app.Client.GetAsync(new Uri("/api/stream/numbers?Count=3", UriKind.Relative));

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        resp.Content.Headers.ContentType.ShouldBe(new MediaTypeHeaderValue("text/event-stream") { CharSet = "utf-8" });
    }

    [TimedFact]
    public async Task Stream_EmitsOneDataFramePerYieldedItem()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var resp = await app.Client.GetAsync(new Uri("/api/stream/numbers?Count=3", UriKind.Relative));
        var body = await resp.Content.ReadAsStringAsync();

        body.ShouldContain("data: 0\n\n");
        body.ShouldContain("data: 1\n\n");
        body.ShouldContain("data: 2\n\n");
    }

    [TimedFact]
    public async Task Stream_ClientAbortsMidStream_HandlerTokenIsCancelled()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var probe = new StreamCancellationProbe();
        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: s => s.AddSingleton(probe),
            configureApp: a => a.MapWarpHttp());

        // The handler yields one frame and then waits on its token for ever, so the only way it
        // ends is RequestAborted reaching it.
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using (var resp = await app.Client.GetAsync(
            new Uri("/api/stream/endless", UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            abort.Token))
        {
            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[64];
            var read = await body.ReadAsync(buffer, ct);
            System.Text.Encoding.UTF8.GetString(buffer, 0, read).ShouldBe("data: 0\n\n");

            await abort.CancelAsync();
        }

        var sawCancellation = await probe.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        sawCancellation.ShouldBeTrue("the handler's wait ended without its token being cancelled");
    }
}
