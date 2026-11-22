using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Warp.Http;
using Warp.Tests.TestData;

namespace Warp.Tests.Http;

[Trait("Category", "NoDb")]
public sealed class StreamEdgeCasesTests
{
    [TimedFact]
    public async Task EmptyStream_Returns200WithNoFrames()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var resp = await app.Client.GetAsync(new Uri("/api/stream/empty", UriKind.Relative));
        var body = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task SingleItemStream_EmitsOneFrame()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var resp = await app.Client.GetAsync(new Uri("/api/stream/numbers?Count=1", UriKind.Relative));
        var body = await resp.Content.ReadAsStringAsync();

        body.ShouldBe("data: 0\n\n");
    }

    [TimedFact]
    public async Task LargeStream_EmitsAllFrames()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var resp = await app.Client.GetAsync(new Uri("/api/stream/numbers?Count=200", UriKind.Relative));
        var body = await resp.Content.ReadAsStringAsync();

        // Each frame is "data: <n>\n\n" — count the frame separators.
        var frames = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        frames.Length.ShouldBe(200);
    }

    [TimedFact]
    public async Task StreamThrowsMidWay_FirstFrameDelivered_ThenTheResponseFails()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        // Headers first, so the frame written before the throw can be read on its own.
        using var resp = await app.Client.GetAsync(
            new Uri("/api/stream/throws", UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body);
        (await reader.ReadLineAsync(ct)).ShouldBe("data: \"first\"");

        // The handler then throws: the response is cut short with the handler's exception rather
        // than ending as if the stream had finished.
        var failure = await Should.ThrowAsync<Exception>(async () => await reader.ReadToEndAsync(ct));
        failure.ToString().ShouldContain("stream-failure");
    }
}
